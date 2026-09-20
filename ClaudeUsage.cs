#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property PackAsTool=true
#:property ToolCommandName=claude-usage
#:property PackageId=ClaudeUsage
#:property Authors=nockawa
#:property Description=Terminal dashboard for your Claude subscription usage — session/weekly windows, per-model burn-rate pace, extra-usage spend, and live service status.
#:property PackageTags=claude;anthropic;usage;cli;tui;dashboard;dotnet-tool;spectre-console
#:property PackageProjectUrl=https://github.com/nockawa/ClaudeUsage
#:property RepositoryUrl=https://github.com/nockawa/ClaudeUsage.git
#:property RepositoryType=git
#:property PackageLicenseExpression=Unlicense
#:property PackageReadmeFile=README.md
#:property PublishAot=false
#:package Spectre.Console@0.57.0

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Rendering;

// Force UTF-8 so box-drawing and status markers render on consoles whose
// default code page isn't UTF-8 (notably Windows). Guarded: throws if output
// is redirected with no attached console.
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* redirected / no console */ }

var refreshMinutes = 15;
var once = false;
var dump = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--once" or "-1":
            once = true;
            break;
        case "--dump":
            dump = true;
            break;
        case "--interval" when i + 1 < args.Length:
            refreshMinutes = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--help" or "-h":
            AnsiConsole.MarkupLine("[bold]Claude Usage Monitor[/]");
            AnsiConsole.MarkupLine("  --once, -1            Print the report once and exit");
            AnsiConsole.MarkupLine("  --interval <minutes>  Refresh interval (default 15)");
            AnsiConsole.MarkupLine("  --dump                Probe candidate endpoints and dump raw JSON");
            return 0;
    }
}

var credPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".claude", ".credentials.json");

// macOS stores the Claude CLI credentials as a login-Keychain generic password
// under this service name instead of the .credentials.json file.
const string KeychainService = "Claude Code-credentials";

string token = "";
DateTime credLastWriteUtc = DateTime.MinValue;
HttpClient client = MakeClient();

if (!await ReloadToken(force: true))
{
    AnsiConsole.MarkupLine("[red]❌ Could not read Claude credentials.[/]");
    if (OperatingSystem.IsMacOS())
        AnsiConsole.MarkupLine($"Looked in the login Keychain ([yellow]{KeychainService}[/]) and at {Markup.Escape(credPath)}.");
    else
        AnsiConsole.MarkupLine($"Looked at {Markup.Escape(credPath)}.");
    AnsiConsole.MarkupLine("Run [yellow]claude login[/] first, or set [yellow]CLAUDE_CODE_OAUTH_TOKEN[/].");
    return 1;
}

var refreshInterval = TimeSpan.FromMinutes(refreshMinutes);
RateLimit? rateLimit = null;
DateTimeOffset? serverRetryHint = null;
int failures = 0;
var history = LoadHistory();

// Status client: public endpoints, no auth headers, separate pool.
var statusClient = new HttpClient(new System.Net.Http.SocketsHttpHandler
{
    PooledConnectionLifetime    = TimeSpan.FromMinutes(5),
    ConnectTimeout              = TimeSpan.FromSeconds(10),
}) { Timeout = TimeSpan.FromSeconds(15) };

// Both pages are Statuspage-hosted and expose the same v2 summary schema, so a
// single fetch/parse/render path serves them.
var statusSources = new StatusSource[]
{
    new("Claude", "status.claude.com", "https://status.claude.com/api/v2/summary.json"),
    new("GitHub", "githubstatus.com",  "https://www.githubstatus.com/api/v2/summary.json"),
};
var statusHolder = new StatusHolder(statusSources);

if (dump)
{
    // Resolve org uuid from profile so we can probe org-scoped endpoints.
    string? orgUuid = null;
    try
    {
        using var p = await client.GetAsync("https://api.anthropic.com/api/oauth/profile");
        if (p.IsSuccessStatusCode)
        {
            using var pd = JsonDocument.Parse(await p.Content.ReadAsStringAsync());
            if (pd.RootElement.TryGetProperty("organization", out var org)
                && org.TryGetProperty("uuid", out var u)
                && u.ValueKind == JsonValueKind.String)
                orgUuid = u.GetString();
        }
    }
    catch { }
    AnsiConsole.MarkupLine($"[grey]org uuid:[/] {orgUuid ?? "(unknown)"}");

    var candidates = new List<string>
    {
        "https://api.anthropic.com/api/oauth/usage",
        "https://api.anthropic.com/api/oauth/profile",
        "https://api.anthropic.com/api/oauth/account",
        "https://api.anthropic.com/api/oauth/claude_cli/client_data",
        "https://api.anthropic.com/v1/models",
    };

    foreach (var url in candidates)
    {
        AnsiConsole.Write(new Rule($"[cyan]{url}[/]") { Justification = Justify.Left });
        try
        {
            using var resp = await client.GetAsync(url);
            AnsiConsole.MarkupLine($"[grey]HTTP {(int)resp.StatusCode} {resp.StatusCode}[/]");
            var body = await resp.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body)) { AnsiConsole.MarkupLine("[grey](empty body)[/]"); continue; }
            try
            {
                using var doc = JsonDocument.Parse(body);
                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                    doc.RootElement.WriteTo(writer);
                AnsiConsole.WriteLine(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
            }
            catch
            {
                AnsiConsole.WriteLine(body.Length > 2000 ? body[..2000] + "…" : body);
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
        }
    }
    return 0;
}

if (once)
{
    var now = DateTimeOffset.UtcNow;
    UsageSnapshot snap;
    DateTimeOffset nextAttemptAt;
    try
    {
        snap = await FetchOnce();
        failures = 0;
        nextAttemptAt = snap.FetchedAt + refreshInterval;
        RecordHistory(history, snap);
    }
    catch (Exception ex)
    {
        failures = 1;
        var hint = serverRetryHint is { } sh && sh > now ? sh - now : (TimeSpan?)null;
        var (label, delay) = ClassifyAndDelay(ex, failures, refreshInterval, hint);
        nextAttemptAt = now + delay;
        var cached = TryLoadCache();
        var msg = $"{label}: {ex.Message}";
        snap = cached is not null
            ? cached with { Error = msg }
            : new UsageSnapshot(null, null, Array.Empty<ModelUsage>(), null, now, msg);
    }

    var profile = await TryFetchJson("https://api.anthropic.com/api/oauth/profile", ProfileInfo.From);
    var statusSlots = await FetchAllStatuses(statusClient, statusHolder.Current);
    var vm = new ViewModel(snap, profile, rateLimit, nextAttemptAt, failures, ComputeTrends(history, snap, now));
    AnsiConsole.Write(BuildView(vm, statusSlots, now, refreshInterval, liveMode: false));
    return snap.Error is null ? 0 : 1;
}

// ---- Live mode ---------------------------------------------------------------
// Model/view split: a background fetcher owns the network + history and publishes
// immutable ViewModels; the render loop only reads the latest model and paints.
// Rendering is never blocked by an in-flight request, so the clock, countdown and
// keypresses stay responsive even while a fetch (or its 30s timeout) is happening.
var model = new ModelHolder();

// Seed from cache so the panels paint immediately instead of waiting on the first
// round-trip. NextAttemptAt = now makes the fetcher refresh right away.
{
    var now = DateTimeOffset.UtcNow;
    var seed = TryLoadCache() ?? new UsageSnapshot(null, null, Array.Empty<ModelUsage>(), null, now, null);
    model.Set(new ViewModel(seed, null, rateLimit, now, 0, ComputeTrends(history, seed, now)));
}

var quitCts = new CancellationTokenSource();
var refreshSignal = new SemaphoreSlim(0, 1);
var statusRefreshSignal = new SemaphoreSlim(0, 1);

// Blocking key reads live on their own thread: 'r' nudges the fetcher, 'q' quits.
var keyThread = new Thread(() =>
{
    while (!quitCts.IsCancellationRequested)
    {
        try
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.R)
            {
                if (refreshSignal.CurrentCount == 0) refreshSignal.Release();
                if (statusRefreshSignal.CurrentCount == 0) statusRefreshSignal.Release();
            }
            else if (k.Key == ConsoleKey.Q) { quitCts.Cancel(); break; }
        }
        catch { Thread.Sleep(100); }
    }
}) { IsBackground = true, Name = "keys" };
keyThread.Start();

// Fetcher: sole owner of `client`, `history`, `failures`, `rateLimit`, `serverRetryHint`.
// It publishes a fresh ViewModel on every attempt; the renderer picks it up next tick.
async Task FetchLoop()
{
    var ct = quitCts.Token;
    while (!ct.IsCancellationRequested)
    {
        var wait = model.Current.NextAttemptAt - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            try { await refreshSignal.WaitAsync(wait, ct); } // returns early on 'r'
            catch (OperationCanceledException) { break; }
        }
        if (ct.IsCancellationRequested) break;

        var now = DateTimeOffset.UtcNow;
        var prev = model.Current;
        try
        {
            var snap = await FetchOnce();
            failures = 0;
            RecordHistory(history, snap);
            var profile = prev.Profile
                ?? await TryFetchJson("https://api.anthropic.com/api/oauth/profile", ProfileInfo.From);
            model.Set(new ViewModel(snap, profile, rateLimit, snap.FetchedAt + refreshInterval, 0,
                                    ComputeTrends(history, snap, snap.FetchedAt)));
        }
        catch (Exception ex)
        {
            failures++;
            var hint = serverRetryHint is { } sh && sh > now ? sh - now : (TimeSpan?)null;
            var (label, delay) = ClassifyAndDelay(ex, failures, refreshInterval, hint);
            var snap = prev.Snap with { Error = $"{label}: {ex.Message}" };
            model.Set(prev with { Snap = snap, RateLimit = rateLimit, NextAttemptAt = now + delay, Failures = failures });
            // After a streak of failures, drop the connection pool entirely. The retry
            // logic alone can't escape a poisoned pool because every attempt reuses the
            // same dead socket. Triggers at 3, 6, 9, … consecutive failures.
            if (failures % 3 == 0) RecreateClient();
        }
    }
}

// Status fetcher: polls every status page every 5 min, best-effort, no auth.
// Uses page.updated_at as a change sentinel to skip unnecessary re-parses.
async Task StatusFetchLoop()
{
    var ct = quitCts.Token;
    var next = DateTimeOffset.UtcNow; // fetch immediately on start
    var interval = TimeSpan.FromMinutes(5);
    while (!ct.IsCancellationRequested)
    {
        var wait = next - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            try { await statusRefreshSignal.WaitAsync(wait, ct); }
            catch (OperationCanceledException) { break; }
        }
        if (ct.IsCancellationRequested) break;
        statusHolder.Set(await FetchAllStatuses(statusClient, statusHolder.Current));
        next = DateTimeOffset.UtcNow + interval;
    }
}

var fetcher       = Task.Run(FetchLoop);
var statusFetcher = Task.Run(StatusFetchLoop);

await AnsiConsole.Live(BuildView(model.Current, statusHolder.Current, DateTimeOffset.UtcNow, refreshInterval, liveMode: true))
    .AutoClear(false)
    .Overflow(VerticalOverflow.Ellipsis)
    .StartAsync(async ctx =>
    {
        while (!quitCts.IsCancellationRequested)
        {
            ctx.UpdateTarget(BuildView(model.Current, statusHolder.Current, DateTimeOffset.UtcNow, refreshInterval, liveMode: true));
            try { await Task.Delay(1000, quitCts.Token); }
            catch (OperationCanceledException) { break; }
        }
    });

quitCts.Cancel();
await Task.WhenAny(fetcher, statusFetcher, Task.Delay(500)); // let tasks unwind, but never hang quit
return 0;

async Task<UsageSnapshot> FetchOnce()
{
    // Pick up a token rotation that happened between fetches, before we waste a 401 round-trip.
    await ReloadIfChanged();
    try { return await DoFetch(); }
    catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
    {
        if (await ReloadToken(force: false))
            return await DoFetch();
        throw;
    }
}

async Task<UsageSnapshot> DoFetch()
{
    using var resp = await client.GetAsync("https://api.anthropic.com/api/oauth/usage");
    rateLimit = RateLimit.From(resp);
    if (!resp.IsSuccessStatusCode)
    {
        serverRetryHint = null;
        if (resp.Headers.RetryAfter is { } ra)
        {
            if (ra.Delta is { } d)      serverRetryHint = DateTimeOffset.UtcNow + d;
            else if (ra.Date is { } dt) serverRetryHint = dt;
        }
        throw new HttpRequestException($"HTTP {(int)resp.StatusCode}", null, resp.StatusCode);
    }
    serverRetryHint = null;
    var body = await resp.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(body);
    var snap = UsageSnapshot.From(doc.RootElement);
    await SaveCache(body, snap.FetchedAt);
    return snap;
}

async Task<bool> ReloadToken(bool force)
{
    try
    {
        // Source priority: the CLAUDE_CODE_OAUTH_TOKEN env var carries the bearer
        // token directly (useful over SSH / headless where the Keychain is locked);
        // otherwise fall back to the local Claude CLI credentials store.
        string? newToken;
        var envToken = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken))
        {
            newToken = envToken.Trim();
            credLastWriteUtc = DateTime.MinValue;   // no file to watch for rotation
        }
        else
        {
            var blob = await ReadCredentialBlobAsync();
            if (blob is null) return false;
            using var doc = JsonDocument.Parse(blob);
            newToken = doc.RootElement.GetProperty("claudeAiOauth").GetProperty("accessToken").GetString();
            // Only the file source has a cheap change signal; MinValue means "always re-read".
            credLastWriteUtc = File.Exists(credPath) ? File.GetLastWriteTimeUtc(credPath) : DateTime.MinValue;
        }
        if (string.IsNullOrEmpty(newToken)) return false;
        if (!force && newToken == token) return false;
        token = newToken;
        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        return true;
    }
    catch { return false; }
}

async Task<bool> ReloadIfChanged()
{
    try
    {
        // File source: skip the reload unless the file's mtime moved. Env var and
        // Keychain have no cheap change signal (credLastWriteUtc == MinValue), so
        // fall through and re-read — both are cheap on the refresh cadence.
        if (credLastWriteUtc != DateTime.MinValue && File.Exists(credPath)
            && File.GetLastWriteTimeUtc(credPath) == credLastWriteUtc)
            return false;
        return await ReloadToken(force: false);
    }
    catch { return false; }
}

// Read the raw credentials JSON blob from the local Claude CLI store.
// Windows/Linux: the ~/.claude/.credentials.json file.
// macOS: the same file if present, otherwise the login Keychain.
async Task<string?> ReadCredentialBlobAsync()
{
    if (File.Exists(credPath))
        return await File.ReadAllTextAsync(credPath);
    if (OperatingSystem.IsMacOS())
        return await ReadMacKeychainAsync(KeychainService);
    return null;
}

// Shell out to the macOS `security` CLI to fetch the credentials blob. `-w` prints
// only the password (the JSON). May trigger a one-time Keychain access prompt.
static async Task<string?> ReadMacKeychainAsync(string service)
{
    try
    {
        var psi = new ProcessStartInfo("/usr/bin/security")
        {
            ArgumentList           = { "find-generic-password", "-s", service, "-w" },
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        using var p = Process.Start(psi);
        if (p is null) return null;
        var outTask = p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        var outp = (await outTask).Trim();
        return p.ExitCode == 0 && outp.Length > 0 ? outp : null;
    }
    catch { return null; }
}

HttpClient MakeClient()
{
    // Cap pooled connection lifetime so stale TCP/TLS state can't poison every retry —
    // the symptom of the old behavior was an app that needed to be restarted to recover.
    var handler = new System.Net.Http.SocketsHttpHandler
    {
        PooledConnectionLifetime    = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        ConnectTimeout              = TimeSpan.FromSeconds(10),
    };
    var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    c.DefaultRequestHeaders.Add("anthropic-beta",    "oauth-2025-04-20");
    c.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    if (!string.IsNullOrEmpty(token))
        c.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
    return c;
}

void RecreateClient()
{
    var old = client;
    client = MakeClient();
    try { old.Dispose(); } catch { }
}

static (string label, TimeSpan delay) ClassifyAndDelay(Exception ex, int attempt, TimeSpan cap, TimeSpan? serverHint)
{
    var status = (ex as HttpRequestException)?.StatusCode;
    var (baseSeconds, label) = status switch
    {
        System.Net.HttpStatusCode.Unauthorized      => (3.0,  "401 unauthorized"),
        System.Net.HttpStatusCode.Forbidden         => (30.0, "403 forbidden"),
        System.Net.HttpStatusCode.TooManyRequests   => (30.0, "429 rate limited"),
        >= System.Net.HttpStatusCode.InternalServerError => (5.0, $"{(int)status!} server error"),
        not null                                    => (60.0, $"{(int)status!} {status}"),
        _ when ex is TaskCanceledException          => (5.0,  "request timed out"),
        _                                           => (5.0,  "network error"),
    };

    // Honor server-provided Retry-After when present (clamped sensibly).
    if (serverHint is { } sh)
    {
        var d = sh < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1)
              : sh > cap                      ? cap
              : sh;
        return (label, d);
    }

    var exp = Math.Min(attempt - 1, 8);
    var seconds = Math.Min(baseSeconds * Math.Pow(2, exp), cap.TotalSeconds);
    return (label, TimeSpan.FromSeconds(seconds));
}

static string CachePath() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "ClaudeUsage", "cache.json");

static string HistoryPath() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "ClaudeUsage", "history.json");

static HistoryStore LoadHistory()
{
    try
    {
        var path = HistoryPath();
        if (!File.Exists(path)) return new();
        var store = new HistoryStore();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("series", out var series)) return store;
        foreach (var prop in series.EnumerateObject())
        {
            var list = new List<HistorySample>();
            foreach (var item in prop.Value.EnumerateArray())
            {
                list.Add(new HistorySample
                {
                    At       = DateTimeOffset.Parse(item.GetProperty("at").GetString()!,       CultureInfo.InvariantCulture),
                    Util     = item.GetProperty("util").GetDouble(),
                    ResetsAt = DateTimeOffset.Parse(item.GetProperty("resetsAt").GetString()!, CultureInfo.InvariantCulture),
                });
            }
            store.Series[prop.Name] = list;
        }
        return store;
    }
    catch { return new(); }
}

static void SaveHistory(HistoryStore s)
{
    try
    {
        var path = HistoryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WritePropertyName("series");
            w.WriteStartObject();
            foreach (var (key, list) in s.Series)
            {
                w.WritePropertyName(key);
                w.WriteStartArray();
                foreach (var sample in list)
                {
                    w.WriteStartObject();
                    w.WriteString("at",       sample.At.ToString("o",       CultureInfo.InvariantCulture));
                    w.WriteNumber("util",     sample.Util);
                    w.WriteString("resetsAt", sample.ResetsAt.ToString("o", CultureInfo.InvariantCulture));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        File.WriteAllBytes(path, ms.ToArray());
    }
    catch { }
}

static void RecordHistory(HistoryStore s, UsageSnapshot snap)
{
    AppendHistory(s, "session", snap.Session, snap.FetchedAt);
    AppendHistory(s, "weekly",  snap.Weekly,  snap.FetchedAt);
    foreach (var m in snap.Models)
        if (m.Percent is { } pct && m.ResetsAt is { } resets)
            AppendHistory(s, ModelSeries(m.Model), new WindowStats(pct, resets), snap.FetchedAt);
    SaveHistory(s);
}

static string ModelSeries(string model) => "model:" + model;

static void AppendHistory(HistoryStore s, string key, WindowStats? w, DateTimeOffset at)
{
    if (w is null) return;
    if (!s.Series.TryGetValue(key, out var list))
        s.Series[key] = list = new();
    // Drop samples from a previous window. The API jitters resets_at by milliseconds
    // across requests, so compare with a 10-minute tolerance — the actual rollover
    // shifts resets_at by the full window length (5h or 7d) so this is unambiguous.
    list.RemoveAll(x => !SameWindow(x.ResetsAt, w.ResetsAt));
    list.Add(new HistorySample { At = at, Util = w.UtilPct, ResetsAt = w.ResetsAt });
    // Retain by age, not by count: the weekly pace reads a trailing 24h, and a count cap
    // would silently shorten that as --interval drops. Keep one extra hour of slack so a
    // sample still brackets `now - 24h` from below instead of being pruned just past it.
    var keepFrom = at - PaceLookback() - TimeSpan.FromHours(1);
    list.RemoveAll(x => x.At < keepFrom);
    if (list.Count > 4096) list.RemoveRange(0, list.Count - 4096); // pathologically small intervals
}

static bool SameWindow(DateTimeOffset a, DateTimeOffset b) =>
    Math.Abs((a - b).TotalMinutes) < 10;

static TrendSet ComputeTrends(HistoryStore s, UsageSnapshot snap, DateTimeOffset now)
{
    var models = new Dictionary<string, Trend?>();
    foreach (var m in snap.Models)
    {
        if (m.Percent is not { } pct || m.ResetsAt is not { } resets) continue; // no data → no trend
        models[m.Model] = ComputeTrend(s, ModelSeries(m.Model),
            new WindowStats(pct, resets), now, TimeSpan.FromHours(3));
    }
    return new TrendSet(
        ComputeTrend(s, "session", snap.Session, now, TimeSpan.FromMinutes(45)),
        ComputeTrend(s, "weekly",  snap.Weekly,  now, TimeSpan.FromHours(3)),
        ComputePace(s, "weekly", snap.Weekly, now),
        models);
}

static TimeSpan PaceLookback() => TimeSpan.FromHours(24);

// Recent burn rate for the weekly window: %/day over the trailing 24h rather than averaged
// since the window opened, so a quiet Monday stops masking a heavy Friday. Needs at least
// an hour of samples to be worth showing, since a shorter span multiplies sampling noise by
// 24. Returns null when history is too thin — the panel then falls back to the average.
static Pace? ComputePace(HistoryStore s, string key, WindowStats? w, DateTimeOffset now)
{
    if (w is null) return null;
    if (!s.Series.TryGetValue(key, out var list)) return null;
    var current = list.Where(x => SameWindow(x.ResetsAt, w.ResetsAt)).ToList();
    if (current.Count < 2) return null;

    // Bracket the cutoff from below: the last sample at or before `now - 24h`, so a reading
    // a few minutes too old is used rather than discarded. Retention bounds how far back
    // that can reach, so the measured span stays within an interval of 24h.
    var cutoff   = now - PaceLookback();
    var earliest = current.LastOrDefault(x => x.At <= cutoff) ?? current[0];
    var latest   = current[^1];
    var hours    = (latest.At - earliest.At).TotalHours;
    if (hours < 1.0) return null;

    return new Pace(Math.Max(0, (latest.Util - earliest.Util) / hours * 24), hours);
}

static Trend? ComputeTrend(HistoryStore s, string key, WindowStats? w, DateTimeOffset now, TimeSpan lookback)
{
    if (w is null) return null;
    if (!s.Series.TryGetValue(key, out var list) || list.Count < 2) return null;
    var current = list.Where(x => SameWindow(x.ResetsAt, w.ResetsAt)).ToList();
    if (current.Count < 2) return null;

    var cutoff = now - lookback;
    var earliest = current.FirstOrDefault(x => x.At >= cutoff) ?? current[0];
    var latest = current[^1];
    var hours = (latest.At - earliest.At).TotalHours;
    if (hours < 1.0 / 60.0) return null; // need at least one minute of separation

    var observed = (latest.Util - earliest.Util) / hours;
    var hoursRemaining = (w.ResetsAt - now).TotalHours;
    var target = hoursRemaining > 0 ? Math.Max(0, (100 - w.UtilPct) / hoursRemaining) : 0;

    int bucket;
    if (observed <= 0)        bucket = -2;          // util didn't grow in lookback
    else if (target <= 0)     bucket =  2;          // window cap reached but still burning
    else
    {
        var ratio = observed / target;
        bucket = ratio < 0.5  ? -2
               : ratio < 0.85 ? -1
               : ratio < 1.15 ?  0
               : ratio < 1.5  ?  1
               :                 2;
    }
    return new Trend(observed, target, bucket);
}

static async Task SaveCache(string usageJson, DateTimeOffset fetchedAt)
{
    try
    {
        var path = CachePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var wrapper = $"{{\"fetched_at\":\"{fetchedAt:o}\",\"body\":{usageJson}}}";
        await File.WriteAllTextAsync(path, wrapper);
    }
    catch { }
}

static UsageSnapshot? TryLoadCache()
{
    try
    {
        var path = CachePath();
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var fetchedAt = DateTimeOffset.Parse(
            doc.RootElement.GetProperty("fetched_at").GetString()!,
            CultureInfo.InvariantCulture);
        return UsageSnapshot.From(doc.RootElement.GetProperty("body")) with { FetchedAt = fetchedAt };
    }
    catch { return null; }
}

async Task<T?> TryFetchJson<T>(string url, Func<JsonElement, T> parse) where T : class
{
    try
    {
        using var resp = await client.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return parse(doc.RootElement);
    }
    catch { return null; }
}

// Polls every page in parallel. A page that fails (or hasn't changed since the last
// poll) keeps its previous reading, so one flaky endpoint never blanks the others.
static async Task<StatusSlot[]> FetchAllStatuses(HttpClient httpClient, StatusSlot[] prev)
{
    var fetched = await Task.WhenAll(prev.Select(p => TryFetchStatus(httpClient, p.Source)));
    var next = new StatusSlot[prev.Length];
    for (int i = 0; i < prev.Length; i++)
        next[i] = fetched[i] is { } s && s.PageUpdatedAt != prev[i].Status?.PageUpdatedAt
            ? prev[i] with { Status = s }
            : prev[i];
    return next;
}

static async Task<ServiceStatus?> TryFetchStatus(HttpClient httpClient, StatusSource source)
{
    try
    {
        using var resp = await httpClient.GetAsync(source.Url);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return ParseStatus(doc.RootElement);
    }
    catch { return null; }
}

static ServiceStatus ParseStatus(JsonElement root)
{
    var statusEl    = root.GetProperty("status");
    var indicator   = statusEl.GetProperty("indicator").GetString()!;
    var description = statusEl.GetProperty("description").GetString() ?? "";

    // Skip group headers and components the page itself hides (GitHub carries a
    // "Visit www.githubstatus.com…" pseudo-component that isn't a real service).
    var components = root.GetProperty("components").EnumerateArray()
        .Where(c => !(c.TryGetProperty("group", out var g) && g.GetBoolean()))
        .Where(c => !(c.TryGetProperty("showcase", out var s) && s.ValueKind == JsonValueKind.False))
        .Select(c => new StatusComponent(
            c.GetProperty("name").GetString()!,
            c.GetProperty("status").GetString()!))
        .ToArray();

    StatusIncident? incident = null;
    foreach (var inc in root.GetProperty("incidents").EnumerateArray())
    {
        var incStatus = inc.GetProperty("status").GetString()!;
        if (incStatus == "resolved") continue;
        string? latestUpdate = null;
        if (inc.TryGetProperty("incident_updates", out var upd) && upd.ValueKind == JsonValueKind.Array)
        {
            foreach (var u in upd.EnumerateArray())
            {
                if (u.TryGetProperty("body", out var b)) latestUpdate = b.GetString();
                break;
            }
        }
        incident = new StatusIncident(
            inc.GetProperty("name").GetString()!,
            incStatus,
            inc.GetProperty("impact").GetString()!,
            DateTimeOffset.Parse(inc.GetProperty("started_at").GetString()!, CultureInfo.InvariantCulture),
            latestUpdate);
        break; // show only the most recent active incident
    }

    // Soonest upcoming or in-progress maintenance window (completed ones skipped).
    StatusMaintenance? maintenance = null;
    if (root.TryGetProperty("scheduled_maintenances", out var maints) && maints.ValueKind == JsonValueKind.Array)
        foreach (var m in maints.EnumerateArray())
        {
            var st = m.TryGetProperty("status", out var stEl) ? stEl.GetString() : null;
            if (st is null or "completed") continue;
            var forT   = ParseDto(m, "scheduled_for");
            var untilT = ParseDto(m, "scheduled_until");
            var cand = new StatusMaintenance(
                m.GetProperty("name").GetString()!, st!, forT, untilT,
                m.TryGetProperty("impact", out var im) ? im.GetString() ?? "" : "");
            if (maintenance is null
                || (forT is { } f && (maintenance.ScheduledFor is not { } mf || f < mf)))
                maintenance = cand;
        }

    var pageUpdatedAt = DateTimeOffset.Parse(
        root.GetProperty("page").GetProperty("updated_at").GetString()!,
        CultureInfo.InvariantCulture);

    return new ServiceStatus(indicator, description, components, incident, maintenance, pageUpdatedAt, DateTimeOffset.UtcNow);

    static DateTimeOffset? ParseDto(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(p.GetString()!, CultureInfo.InvariantCulture) : null;
}

static IRenderable BuildView(ViewModel vm, StatusSlot[] statusSlots, DateTimeOffset now, TimeSpan refreshInterval, bool liveMode)
{
    var snap          = vm.Snap;
    var profile       = vm.Profile;
    var rateLimit     = vm.RateLimit;
    var nextAttemptAt = vm.NextAttemptAt;
    var failures      = vm.Failures;
    var trends        = vm.Trends;

    var parts = new List<string> { "[bold cyan]📊 Claude Usage[/]" };
    if (profile?.Plan is not null) parts.Add($"[yellow]{Markup.Escape(profile.Plan)}[/]");
    if (profile?.Display is not null) parts.Add(Markup.Escape(profile.Display));
    parts.Add($"{now:yyyy-MM-dd HH:mm:ss} UTC");
    var header = new Rule(string.Join("  ·  ", parts)) { Justification = Justify.Left };

    var leftCol = new Rows(
        BuildWindowPanel("🕐 Session (5h)", snap.Session, TimeSpan.FromHours(5), now, showPace: false, pace: null, trends.Session),
        BuildModelPanel(snap, trends)
    );

    var rightCol = new Rows(
        BuildWindowPanel("📅 Weekly (7d)", snap.Weekly, TimeSpan.FromDays(7), now, showPace: true, trends.WeeklyPace, trends.Weekly),
        BuildExtraPanel(snap.Extra)
    );

    var body = new Columns(leftCol, rightCol).Collapse();

    var statusRow = BuildStatusRow(statusSlots, now);

    string rlSuffix = "";
    if (rateLimit is not null && (rateLimit.Remaining is not null || rateLimit.Limit is not null))
    {
        var rem = rateLimit.Remaining?.ToString() ?? "?";
        var lim = rateLimit.Limit?.ToString() ?? "?";
        var resetIn = rateLimit.Reset is { } r && r > now ? $" · resets in {FormatDuration(r - now)}" : "";
        rlSuffix = $"  ·  [grey]API budget {rem}/{lim}{resetIn}[/]";
    }

    IRenderable footer;
    if (snap.Error is not null)
    {
        var hasData = snap.Session is not null || snap.Weekly is not null;
        var staleness = hasData ? $"  ·  [yellow]data from {snap.FetchedAt:HH:mm:ss} UTC[/]" : "";
        var until = nextAttemptAt - now;
        var retryWhen = until.TotalSeconds > 1 ? $"in {FormatDuration(until)}" : "now…";
        var attemptTag = liveMode
            ? $"  ·  retry attempt #{failures + 1} {retryWhen}"
            : $"  ·  attempt #{failures} failed";
        footer = new Markup($"[red]Last fetch failed (attempt {failures}):[/] {Markup.Escape(snap.Error)}{attemptTag}{staleness}{rlSuffix}");
    }
    else if (!liveMode)
    {
        footer = new Markup($"[grey]Fetched at {snap.FetchedAt:HH:mm:ss} UTC[/]{rlSuffix}");
    }
    else
    {
        var nextFetch = nextAttemptAt - now;
        var when = nextFetch.TotalSeconds > 1 ? $"in {FormatDuration(nextFetch)}" : "now…";
        footer = new Markup($"[grey]Next refresh {when}  ·  [bold]r[/] refresh now  ·  [bold]q[/] quit[/]{rlSuffix}");
    }

    return new Rows(header, body, statusRow, footer);
}

// One compact line while every page reports green (the common case), a bordered
// panel with one line per service as soon as any of them reports trouble.
static IRenderable BuildStatusRow(StatusSlot[] slots, DateTimeOffset now)
{
    var troubled = slots.Where(s => s.Status is { } st && st.Indicator != "none").ToArray();

    if (troubled.Length == 0)
    {
        var known   = slots.Where(s => s.Status is not null).ToArray();
        var pending = slots.Where(s => s.Status is null).ToArray();
        var line = new System.Text.StringBuilder("  ");
        if (known.Length > 0)
        {
            var hosts = string.Join(" · ", known.Select(s => Markup.Escape(s.Source.Host)));
            line.Append($"[green]● All systems operational[/]  [grey]{hosts}[/]");
            foreach (var s in known)
                line.Append(FormatMaintenance(s.Status!.NextMaintenance, now, s.Source.Label));
        }
        if (pending.Length > 0)
        {
            if (known.Length > 0) line.Append("  [grey]·[/]  ");
            line.Append($"[grey]○ {string.Join(" · ", pending.Select(s => Markup.Escape(s.Source.Host)))} …[/]");
        }
        return new Markup(line.ToString());
    }

    var worst = troubled.Select(s => s.Status!.Indicator).OrderByDescending(IndicatorRank).First();
    var (headColor, headDot) = IndicatorStyle(worst);

    return new Panel(new Rows(slots.Select(s => new Markup(BuildServiceStatusLine(s, now)))))
        .Header($" [{headColor}]{headDot}[/] [bold]Status Update[/] ", Justify.Left)
        .Border(BoxBorder.Rounded)
        .BorderColor(Color.Grey39)
        .Expand();
}

static string BuildServiceStatusLine(StatusSlot slot, DateTimeOffset now)
{
    var name = $"[bold]{Markup.Escape(slot.Source.Label)}[/]";
    if (slot.Status is not { } status)
        return $"[grey]○[/] {name}  [grey]no reading[/]  [grey]{Markup.Escape(slot.Source.Host)}[/]";

    var (color, dot) = IndicatorStyle(status.Indicator);
    var sb = new System.Text.StringBuilder();
    sb.Append($"[{color}]{dot}[/] {name}  [{color}]{Markup.Escape(status.Description)}[/]");

    var degraded = status.Components.Where(c => c.Status != "operational").ToArray();
    if (degraded.Length > 0)
    {
        sb.Append($"  [grey]·[/]  ");
        sb.Append(string.Join("  ", degraded.Select(c =>
        {
            var (cc, lbl) = c.Status switch
            {
                "degraded_performance" => ("yellow", "degraded"),
                "partial_outage"       => ("red",    "partial outage"),
                "major_outage"         => ("bold red", "major outage"),
                _                      => ("grey",   c.Status),
            };
            return $"[grey]{Markup.Escape(c.Name)}[/] [{cc}]{lbl}[/]";
        })));
    }

    if (status.ActiveIncident is { } inc)
    {
        var elapsed = FormatDuration(now - inc.StartedAt);
        sb.Append($"  [grey]·[/]  [bold]{Markup.Escape(inc.Name)}[/] [{color}]{inc.IncidentStatus}[/] [grey]{elapsed} ago[/]");
    }

    sb.Append(FormatMaintenance(status.NextMaintenance, now));
    return sb.ToString();
}

static (string Color, string Dot) IndicatorStyle(string indicator) => indicator switch
{
    "none"     => ("green",    "●"),
    "minor"    => ("yellow",   "●"),
    "major"    => ("red",      "●"),
    "critical" => ("bold red", "●"),
    _          => ("grey",     "○"),
};

static int IndicatorRank(string indicator) => indicator switch
{
    "critical" => 3,
    "major"    => 2,
    "minor"    => 1,
    "none"     => 0,
    _          => -1,
};

// `label` names the service; passed only on the collapsed all-green line, where the
// per-service line that would otherwise carry the name isn't rendered.
static string FormatMaintenance(StatusMaintenance? m, DateTimeOffset now, string? label = null)
{
    if (m is null) return "";
    string when;
    if (m.Status is "in_progress" or "verifying")
        when = m.ScheduledUntil is { } u && u > now ? $"in progress · ends in {FormatDuration(u - now)}" : "in progress";
    else if (m.ScheduledFor is { } f)
        when = f > now ? $"in {FormatDuration(f - now)}" : "imminent";
    else
        when = Markup.Escape(m.Status);
    var prefix = label is null ? "" : $"{Markup.Escape(label)}: ";
    return $"  [grey]·[/]  [blue]🔧 {prefix}{Markup.Escape(m.Name)}[/] [grey]{when}[/]";
}

static Panel BuildWindowPanel(string title, WindowStats? w, TimeSpan windowDuration, DateTimeOffset now, bool showPace, Pace? pace, Trend? trend)
{
    if (w is null)
        return Wrap(title, new Markup("[grey]no data[/]"));

    var elapsed = windowDuration - (w.ResetsAt - now);
    var elapsedPct = Math.Clamp(elapsed.TotalSeconds / windowDuration.TotalSeconds * 100, 0, 100);
    var delta = w.UtilPct - elapsedPct;
    var (color, icon, word) =
        delta >  5 ? ("red",    "●", "AHEAD")  :
        delta < -5 ? ("green",  "●", "BEHIND") :
                     ("yellow", "●", "ON PACE");

    var grid = new Grid()
        .AddColumn(new GridColumn().NoWrap())
        .AddColumn(new GridColumn().PadLeft(2));

    var trendStr = FormatTrend(trend);
    grid.AddRow("[bold]Used[/]",    Bar(w.UtilPct, color)    + $"  {w.UtilPct:F1}%{trendStr}" + SeverityBadge(w.Severity));
    grid.AddRow("[bold]Elapsed[/]", Bar(elapsedPct, "grey")  + $"  {elapsedPct:F1}%");
    grid.AddEmptyRow();
    grid.AddRow($"[{color}]{icon} {word}[/]", $"by [{color}]{Math.Abs(delta):F1}%[/]");
    grid.AddRow("Resets in", FormatDuration(w.ResetsAt - now));

    if (showPace && w.UtilPct < 100)
    {
        var remaining = 100 - w.UtilPct;
        var daysLeft = Math.Max((w.ResetsAt - now).TotalDays, 0);
        var target  = daysLeft > 0 ? remaining / daysLeft : 0;
        // Recent burn beats the since-window-open average for deciding whether to ease off
        // today. The basis tag distinguishes the two, and names the span when it's short.
        var (current, basis) = pace is { } p
            ? (p.PctPerDay, p.HoursObserved >= 23.5 ? "24h" : $"{p.HoursObserved:F0}h")
            : (elapsed.TotalDays > 0 ? w.UtilPct / elapsed.TotalDays : 0, "avg");
        var paceColor = current > target ? "red" : "green";
        grid.AddEmptyRow();
        grid.AddRow("Pace",   $"[{paceColor}]{current:F1}%/d[/]  [grey]({basis})[/]");
        grid.AddRow("Target", $"{target:F1}%/d");
    }

    return Wrap(title, grid);
}

static Panel BuildModelPanel(UsageSnapshot snap, TrendSet trends)
{
    var grid = new Grid()
        .AddColumn(new GridColumn().NoWrap())
        .AddColumn(new GridColumn().PadLeft(2));

    if (snap.Models.Count == 0)
    {
        grid.AddRow("[grey]no per-model limits[/]", "");
        return Wrap("Per-Model (7d)", grid);
    }

    // Scoped models (from `limits`, e.g. Fable) come first, then legacy Opus/Sonnet.
    // A null percent means the API returns no figure for that model right now → n/a.
    var palette = new[] { "magenta", "blue", "cyan", "green", "yellow" };
    var i = 0;
    foreach (var m in snap.Models)
    {
        var color = palette[i++ % palette.Length];
        if (m.Percent is not { } pct)
        {
            grid.AddRow($"[bold]{Markup.Escape(m.Model)}[/]", "[grey]n/a[/]");
            continue;
        }
        trends.Models.TryGetValue(m.Model, out var trend);
        grid.AddRow($"[bold]{Markup.Escape(m.Model)}[/]",
                    Bar(pct, color) + $"  {pct:F1}%" + FormatTrendShort(trend) + SeverityBadge(m.Severity));
    }
    return Wrap("Per-Model (7d)", grid);
}

static string FormatTrend(Trend? t)
{
    if (t is null) return "";
    return $"  {t.Arrow()} [grey]{t.ObservedRatePctPerHour.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)}%/h[/]";
}

static string FormatTrendShort(Trend? t) => t is null ? "" : $"  {t.Arrow()}";

// API severity → a compact ⚠ badge. "normal"/null renders nothing, so the signal
// only appears when the API flags a window/model/overage as nearing its cap.
static string SeverityBadge(string? sev)
{
    if (string.IsNullOrWhiteSpace(sev) || sev.Equals("normal", StringComparison.OrdinalIgnoreCase)) return "";
    var s = sev.ToLowerInvariant();
    var color = s.Contains("crit") || s.Contains("exceed") || s.Contains("reach") || s.Contains("major") ? "red" : "yellow";
    return $"  [bold {color}]⚠ {Markup.Escape(sev.ToUpperInvariant())}[/]";
}

static Panel BuildExtraPanel(ExtraUsage? extra)
{
    if (extra is null)
        return Wrap("Extra Usage", new Markup("[grey]not configured[/]"));

    var sym = extra.Currency switch
    {
        "EUR" => "€",
        "USD" => "$",
        "GBP" => "£",
        _ => extra.Currency + " ",
    };

    var grid = new Grid()
        .AddColumn(new GridColumn().NoWrap())
        .AddColumn(new GridColumn().PadLeft(2));

    var statusMarkup = extra.Enabled ? "[green]enabled[/]" : "[grey]disabled[/]";
    grid.AddRow("[bold]Status[/]", statusMarkup);
    grid.AddRow("[bold]Used[/]",   $"{sym}{extra.UsedCredits:F2}");
    grid.AddRow("[bold]Limit[/]",  $"{sym}{extra.MonthlyLimit:F0}");
    var pct = extra.Utilization ?? (extra.MonthlyLimit > 0 ? (double)(extra.UsedCredits / extra.MonthlyLimit) * 100 : 0);
    grid.AddRow("[bold]Util[/]",   Bar(pct, "yellow") + $"  {pct:F1}%" + SeverityBadge(extra.Severity));

    return Wrap("Extra Usage", grid);
}

static Panel Wrap(string title, IRenderable content) =>
    new Panel(content)
        .Header($" [bold]{title}[/] ", Justify.Left)
        .Border(BoxBorder.Rounded)
        .BorderColor(Color.Grey39)
        .Expand();

static string Bar(double pct, string color)
{
    const int width = 18;
    var clamped = Math.Clamp(pct, 0, 100);
    var filled = (int)Math.Round(width * clamped / 100);
    return $"[{color}]{new string('█', filled)}[/][grey]{new string('░', width - filled)}[/]";
}

static string FormatDuration(TimeSpan ts)
{
    if (ts.TotalSeconds <= 0) return "now";
    if (ts.TotalDays    >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h";
    if (ts.TotalHours   >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
    if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m";
    return $"{(int)ts.TotalSeconds}s";
}

record RateLimit(int? Remaining, int? Limit, DateTimeOffset? Reset)
{
    public static RateLimit? From(System.Net.Http.HttpResponseMessage resp)
    {
        var rem = TryGetInt(resp, "anthropic-ratelimit-requests-remaining");
        var lim = TryGetInt(resp, "anthropic-ratelimit-requests-limit");
        DateTimeOffset? reset = null;
        if (resp.Headers.TryGetValues("anthropic-ratelimit-requests-reset", out var v))
        {
            var s = v.FirstOrDefault();
            if (s is not null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dto))
                reset = dto;
        }
        if (rem is null && lim is null && reset is null) return null;
        return new RateLimit(rem, lim, reset);
    }

    static int? TryGetInt(System.Net.Http.HttpResponseMessage resp, string name)
    {
        if (!resp.Headers.TryGetValues(name, out var v)) return null;
        var s = v.FirstOrDefault();
        return int.TryParse(s, System.Globalization.NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    }
}

record ProfileInfo(string? Display, string? Plan)
{
    public static ProfileInfo From(JsonElement root)
    {
        var email = TryStr(root, "account", "email_address")
                 ?? TryStr(root, "account", "email")
                 ?? TryStr(root, "email")
                 ?? TryStr(root, "user", "email");
        var name  = TryStr(root, "account", "full_name")
                 ?? TryStr(root, "account", "name")
                 ?? TryStr(root, "name")
                 ?? TryStr(root, "display_name");
        var plan  = TryStr(root, "subscription", "type")
                 ?? TryStr(root, "subscription", "tier")
                 ?? TryStr(root, "subscriptionType")
                 ?? TryStr(root, "plan_tier")
                 // rate_limit_tier ("default_claude_max_20x") is the reliable multiplier
                 // source — the profile endpoint doesn't return has_claude_max_20x.
                 ?? PlanFromTier(TryStr(root, "organization", "rate_limit_tier"));

        if (plan is null)
        {
            if (TryBool(root, "account", "has_claude_max_20x") == true) plan = "Max 20x";
            else if (TryBool(root, "account", "has_claude_max_5x") == true) plan = "Max 5x";
            else if (TryBool(root, "account", "has_claude_max")    == true) plan = "Max";
            else if (TryBool(root, "account", "has_claude_pro")    == true) plan = "Pro";
        }

        return new ProfileInfo(name ?? email, plan);
    }

    // Maps rate_limit_tier ("default_claude_max_20x" / "…max_5x") to a friendly label,
    // pulling out the usage multiplier when present.
    static string? PlanFromTier(string? tier)
    {
        if (string.IsNullOrEmpty(tier)) return null;
        var t = tier.ToLowerInvariant();
        if (t.Contains("max"))
        {
            var m = System.Text.RegularExpressions.Regex.Match(t, @"max[_-]?(\d+)x");
            return m.Success ? $"Max {m.Groups[1].Value}x" : "Max";
        }
        if (t.Contains("team")) return "Team";
        if (t.Contains("pro"))  return "Pro";
        if (t.Contains("free")) return "Free";
        return null;
    }

    static string? TryStr(JsonElement root, params string[] path)
    {
        var cur = root;
        foreach (var seg in path)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(seg, out var next)) return null;
            cur = next;
        }
        return cur.ValueKind == JsonValueKind.String ? cur.GetString() : null;
    }

    static bool? TryBool(JsonElement root, params string[] path)
    {
        var cur = root;
        foreach (var seg in path)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(seg, out var next)) return null;
            cur = next;
        }
        return cur.ValueKind switch
        {
            JsonValueKind.True  => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}

record WindowStats(double UtilPct, DateTimeOffset ResetsAt)
{
    // API-reported proximity to the cap ("normal" until a window nears its limit).
    public string? Severity { get; init; }
}

// A per-model weekly figure for the "Per-Model" panel. `Percent` is null when the
// API reports no data for that model (the deprecated top-level seven_day_* keys now
// return null); the row still renders as n/a so the model stays listed. `ResetsAt`
// populates once usage accrues, at which point trends can be computed.
record ModelUsage(string Model, double? Percent, DateTimeOffset? ResetsAt, bool IsActive, string? Severity);

record ExtraUsage(bool Enabled, decimal MonthlyLimit, decimal UsedCredits, double? Utilization, string Currency, string? Severity);

// A Statuspage-hosted status page: `Label` is what the UI calls the service, `Host`
// is shown as the attribution, `Url` is the v2 summary endpoint.
record StatusSource(string Label, string Host, string Url);

// A service and its latest reading — null until the first successful poll.
record StatusSlot(StatusSource Source, ServiceStatus? Status);

record ServiceStatus(
    string Indicator,
    string Description,
    StatusComponent[] Components,
    StatusIncident? ActiveIncident,
    StatusMaintenance? NextMaintenance,
    DateTimeOffset PageUpdatedAt,
    DateTimeOffset FetchedAt);

record StatusComponent(string Name, string Status);

record StatusMaintenance(
    string Name,
    string Status,
    DateTimeOffset? ScheduledFor,
    DateTimeOffset? ScheduledUntil,
    string Impact);

record StatusIncident(
    string Name,
    string IncidentStatus,
    string Impact,
    DateTimeOffset StartedAt,
    string? LatestUpdate);

class HistoryStore
{
    public Dictionary<string, List<HistorySample>> Series { get; set; } = new();
}

class HistorySample
{
    public DateTimeOffset At { get; set; }
    public double Util { get; set; }
    public DateTimeOffset ResetsAt { get; set; }
}

record TrendSet(Trend? Session, Trend? Weekly, Pace? WeeklyPace, IReadOnlyDictionary<string, Trend?> Models);

// Burn rate over a trailing window, in %/day. HoursObserved is the real history behind it:
// below 24h the panel says so rather than passing a 3h reading off as a day's worth.
record Pace(double PctPerDay, double HoursObserved);

// Immutable snapshot of everything the view needs. The fetcher builds a new one per
// attempt and swaps it into ModelHolder; the renderer always sees a consistent set.
record ViewModel(
    UsageSnapshot Snap,
    ProfileInfo? Profile,
    RateLimit? RateLimit,
    DateTimeOffset NextAttemptAt,
    int Failures,
    TrendSet Trends);

// One-slot mailbox between the fetcher (writer) and the render loop (reader).
// Reference assignment is atomic; Volatile guarantees the reader sees the latest write.
sealed class ModelHolder
{
    ViewModel _vm = null!;
    public ViewModel Current => Volatile.Read(ref _vm);
    public void Set(ViewModel vm) => Volatile.Write(ref _vm, vm);
}

// Same one-slot mailbox pattern as ModelHolder: the status fetcher swaps in a whole
// new array, so the renderer never sees a half-updated set of services.
sealed class StatusHolder
{
    StatusSlot[] _s;
    public StatusHolder(IEnumerable<StatusSource> sources) =>
        _s = sources.Select(s => new StatusSlot(s, null)).ToArray();
    public StatusSlot[] Current => Volatile.Read(ref _s);
    public void Set(StatusSlot[] slots) => Volatile.Write(ref _s, slots);
}

record Trend(double ObservedRatePctPerHour, double TargetRatePctPerHour, int Bucket)
{
    public string Arrow() => Bucket switch
    {
        >=  2 => "[bold red]↑↑[/]",
            1 => "[red]↑[/]",
            0 => "[yellow]→[/]",
           -1 => "[green]↓[/]",
        _     => "[bold green]↓↓[/]",
    };
}

record UsageSnapshot(
    WindowStats? Session,
    WindowStats? Weekly,
    IReadOnlyList<ModelUsage> Models,
    ExtraUsage? Extra,
    DateTimeOffset FetchedAt,
    string? Error)
{
    public static UsageSnapshot From(JsonElement root) => new(
        ParseWindow(root, "five_hour", SeverityByKind(root, "session")),
        ParseWindow(root, "seven_day", SeverityByKind(root, "weekly_all")),
        ParseModels(root),
        ParseExtra(root),
        DateTimeOffset.UtcNow,
        null);

    static WindowStats? ParseWindow(JsonElement root, string name, string? severity)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object) return null;
        // Enterprise/Team accounts can return the window object with null numeric fields.
        if (!el.TryGetProperty("utilization", out var utilEl) || utilEl.ValueKind != JsonValueKind.Number) return null;
        if (!el.TryGetProperty("resets_at", out var resetsProp) || resetsProp.ValueKind != JsonValueKind.String) return null;
        return new WindowStats(utilEl.GetDouble(), DateTimeOffset.Parse(resetsProp.GetString()!, CultureInfo.InvariantCulture))
            { Severity = severity };
    }

    // A window's severity lives on its matching `limits` entry (session / weekly_all),
    // not on the top-level five_hour/seven_day objects.
    static string? SeverityByKind(JsonElement root, string kind)
    {
        if (!root.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array) return null;
        foreach (var lim in limits.EnumerateArray())
        {
            if (lim.ValueKind != JsonValueKind.Object) continue;
            if (!lim.TryGetProperty("kind", out var k) || k.GetString() != kind) continue;
            return lim.TryGetProperty("severity", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        }
        return null;
    }

    // Models shown in the "Per-Model" panel come from two sources, merged:
    //   1. Scoped weekly caps in the `limits` array (scope.model.display_name) — the
    //      newer mechanism; currently just Fable.
    //   2. The legacy top-level seven_day_{opus,sonnet} keys — now returning null,
    //      but kept so Opus/Sonnet stay listed (as n/a) and re-light if repopulated.
    static IReadOnlyList<ModelUsage> ParseModels(JsonElement root)
    {
        var list = new List<ModelUsage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
            foreach (var lim in limits.EnumerateArray())
            {
                if (lim.ValueKind != JsonValueKind.Object) continue;
                if (!lim.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object) continue;
                if (!scope.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Object) continue;
                if (!model.TryGetProperty("display_name", out var dn) || dn.ValueKind != JsonValueKind.String) continue;
                var name = dn.GetString();
                if (string.IsNullOrEmpty(name) || !seen.Add(name!)) continue;

                double? percent = lim.TryGetProperty("percent", out var p) && p.ValueKind == JsonValueKind.Number
                    ? p.GetDouble() : null;
                DateTimeOffset? resets = lim.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.Parse(r.GetString()!, CultureInfo.InvariantCulture) : null;
                var active = lim.TryGetProperty("is_active", out var ia) && ia.ValueKind == JsonValueKind.True;
                var severity = lim.TryGetProperty("severity", out var sev) && sev.ValueKind == JsonValueKind.String
                    ? sev.GetString() : null;

                list.Add(new ModelUsage(name!, percent, resets, active, severity));
            }

        AddTopLevel("seven_day_opus",   "Opus");
        AddTopLevel("seven_day_sonnet", "Sonnet");
        return list;

        void AddTopLevel(string key, string name)
        {
            if (!seen.Add(name)) return; // already present as a scoped entry
            double? percent = null;
            DateTimeOffset? resets = null;
            if (root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number)
                    percent = u.GetDouble();
                if (el.TryGetProperty("resets_at", out var rr) && rr.ValueKind == JsonValueKind.String)
                    resets = DateTimeOffset.Parse(rr.GetString()!, CultureInfo.InvariantCulture);
            }
            list.Add(new ModelUsage(name, percent, resets, false, null));
        }
    }

    static ExtraUsage? ParseExtra(JsonElement root)
    {
        if (!root.TryGetProperty("extra_usage", out var el) || el.ValueKind != JsonValueKind.Object) return null;
        // Overage severity is reported on the sibling `spend` object, not `extra_usage`.
        string? severity = null;
        if (root.TryGetProperty("spend", out var spend) && spend.ValueKind == JsonValueKind.Object
            && spend.TryGetProperty("severity", out var sev) && sev.ValueKind == JsonValueKind.String)
            severity = sev.GetString();
        return new ExtraUsage(
            el.TryGetProperty("is_enabled", out var en) && en.ValueKind == JsonValueKind.True,
            Cents(el, "monthly_limit"),   // API reports cents
            Cents(el, "used_credits"),    // API reports cents
            el.TryGetProperty("utilization", out var utilEl) && utilEl.ValueKind == JsonValueKind.Number
                ? utilEl.GetDouble() : null,
            el.TryGetProperty("currency", out var cur) && cur.ValueKind == JsonValueKind.String ? cur.GetString()! : "",
            severity);

        static decimal Cents(JsonElement obj, string prop)
            => obj.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDecimal() / 100m : 0m;
    }
}
