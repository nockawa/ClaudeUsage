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
#:property PublishAot=false
#:package Spectre.Console@0.57.0

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Rendering;

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

// Status client: public endpoint, no auth headers, separate pool.
var statusClient = new HttpClient(new System.Net.Http.SocketsHttpHandler
{
    PooledConnectionLifetime    = TimeSpan.FromMinutes(5),
    ConnectTimeout              = TimeSpan.FromSeconds(10),
}) { Timeout = TimeSpan.FromSeconds(15) };
var statusHolder = new StatusHolder();

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
            : new UsageSnapshot(null, null, null, null, null, now, msg);
    }

    var profile = await TryFetchJson("https://api.anthropic.com/api/oauth/profile", ProfileInfo.From);
    var claudeStatus = await TryFetchStatus(statusClient);
    var vm = new ViewModel(snap, profile, rateLimit, nextAttemptAt, failures, ComputeTrends(history, snap, now));
    AnsiConsole.Write(BuildView(vm, claudeStatus, now, refreshInterval, liveMode: false));
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
    var seed = TryLoadCache() ?? new UsageSnapshot(null, null, null, null, null, now, null);
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

// Status fetcher: polls status.claude.com every 5 min, best-effort, no auth.
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
        var s = await TryFetchStatus(statusClient);
        if (s is not null)
        {
            var cur = statusHolder.Current;
            if (cur is null || s.PageUpdatedAt != cur.PageUpdatedAt)
                statusHolder.Set(s);
        }
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
    AppendHistory(s, "sonnet",  snap.Sonnet,  snap.FetchedAt);
    AppendHistory(s, "opus",    snap.Opus,    snap.FetchedAt);
    SaveHistory(s);
}

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
    if (list.Count > 96) list.RemoveRange(0, list.Count - 96);
}

static bool SameWindow(DateTimeOffset a, DateTimeOffset b) =>
    Math.Abs((a - b).TotalMinutes) < 10;

static TrendSet ComputeTrends(HistoryStore s, UsageSnapshot snap, DateTimeOffset now) => new(
    Session: ComputeTrend(s, "session", snap.Session, now, TimeSpan.FromMinutes(45)),
    Weekly:  ComputeTrend(s, "weekly",  snap.Weekly,  now, TimeSpan.FromHours(3)),
    Sonnet:  ComputeTrend(s, "sonnet",  snap.Sonnet,  now, TimeSpan.FromHours(3)),
    Opus:    ComputeTrend(s, "opus",    snap.Opus,    now, TimeSpan.FromHours(3)));

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

static async Task<ClaudeStatus?> TryFetchStatus(HttpClient httpClient)
{
    try
    {
        using var resp = await httpClient.GetAsync("https://status.claude.com/api/v2/summary.json");
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return ParseStatus(doc.RootElement);
    }
    catch { return null; }
}

static ClaudeStatus ParseStatus(JsonElement root)
{
    var statusEl    = root.GetProperty("status");
    var indicator   = statusEl.GetProperty("indicator").GetString()!;
    var description = statusEl.GetProperty("description").GetString()!;

    var components = root.GetProperty("components").EnumerateArray()
        .Where(c => !c.GetProperty("group").GetBoolean())
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

    var pageUpdatedAt = DateTimeOffset.Parse(
        root.GetProperty("page").GetProperty("updated_at").GetString()!,
        CultureInfo.InvariantCulture);

    return new ClaudeStatus(indicator, description, components, incident, pageUpdatedAt, DateTimeOffset.UtcNow);
}

static IRenderable BuildView(ViewModel vm, ClaudeStatus? claudeStatus, DateTimeOffset now, TimeSpan refreshInterval, bool liveMode)
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
        BuildWindowPanel("🕐 Session (5h)", snap.Session, TimeSpan.FromHours(5), now, showPace: false, trends.Session),
        BuildModelPanel(snap, trends.Sonnet, trends.Opus)
    );

    var rightCol = new Rows(
        BuildWindowPanel("📅 Weekly (7d)", snap.Weekly, TimeSpan.FromDays(7), now, showPace: true, trends.Weekly),
        BuildExtraPanel(snap.Extra)
    );

    var body = new Columns(leftCol, rightCol).Collapse();

    var statusRow = BuildStatusRow(claudeStatus, now);

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

static IRenderable BuildStatusRow(ClaudeStatus? status, DateTimeOffset now)
{
    if (status is null)
        return new Markup("[grey]  ○ status.claude.com …[/]");

    if (status.Indicator == "none")
        return new Markup("[green]  ● All systems operational[/]  [grey]status.claude.com[/]");

    var (color, dot) = status.Indicator switch
    {
        "minor"    => ("yellow",   "●"),
        "major"    => ("red",      "●"),
        "critical" => ("bold red", "●"),
        _          => ("grey",     "○"),
    };

    var degraded = status.Components.Where(c => c.Status != "operational").ToArray();
    var sb = new System.Text.StringBuilder();
    sb.Append($"[{color}]{dot} {Markup.Escape(status.Description)}[/]");

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

    return new Panel(new Markup(sb.ToString()))
        .Header($" [{color}]{dot}[/] [bold]Status Update[/] ", Justify.Left)
        .Border(BoxBorder.Rounded)
        .BorderColor(Color.Grey39)
        .Expand();
}

static Panel BuildWindowPanel(string title, WindowStats? w, TimeSpan windowDuration, DateTimeOffset now, bool showPace, Trend? trend)
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
    grid.AddRow("[bold]Used[/]",    Bar(w.UtilPct, color)    + $"  {w.UtilPct:F1}%{trendStr}");
    grid.AddRow("[bold]Elapsed[/]", Bar(elapsedPct, "grey")  + $"  {elapsedPct:F1}%");
    grid.AddEmptyRow();
    grid.AddRow($"[{color}]{icon} {word}[/]", $"by [{color}]{Math.Abs(delta):F1}%[/]");
    grid.AddRow("Resets in", FormatDuration(w.ResetsAt - now));

    if (showPace && w.UtilPct < 100)
    {
        var remaining = 100 - w.UtilPct;
        var daysLeft = Math.Max((w.ResetsAt - now).TotalDays, 0);
        var target  = daysLeft > 0 ? remaining / daysLeft : 0;
        var current = elapsed.TotalDays > 0 ? w.UtilPct / elapsed.TotalDays : 0;
        var paceColor = current > target ? "red" : "green";
        grid.AddEmptyRow();
        grid.AddRow("Pace",   $"[{paceColor}]{current:F1}%/d[/]");
        grid.AddRow("Target", $"{target:F1}%/d");
    }

    return Wrap(title, grid);
}

static Panel BuildModelPanel(UsageSnapshot snap, Trend? sonnet, Trend? opus)
{
    var grid = new Grid()
        .AddColumn(new GridColumn().NoWrap())
        .AddColumn(new GridColumn().PadLeft(2));
    AddModelRow(grid, "Sonnet", snap.Sonnet, "blue",    sonnet);
    AddModelRow(grid, "Opus",   snap.Opus,   "magenta", opus);
    return Wrap("Per-Model (7d)", grid);

    static void AddModelRow(Grid g, string name, WindowStats? w, string color, Trend? trend)
    {
        if (w is null) g.AddRow($"[bold]{name}[/]", "[grey]n/a[/]");
        else g.AddRow($"[bold]{name}[/]", Bar(w.UtilPct, color) + $"  {w.UtilPct:F1}%" + FormatTrendShort(trend));
    }
}

static string FormatTrend(Trend? t)
{
    if (t is null) return "";
    return $"  {t.Arrow()} [grey]{t.ObservedRatePctPerHour.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)}%/h[/]";
}

static string FormatTrendShort(Trend? t) => t is null ? "" : $"  {t.Arrow()}";

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
    grid.AddRow("[bold]Util[/]",   Bar(pct, "yellow") + $"  {pct:F1}%");

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
                 ?? TryStr(root, "plan_tier");

        if (plan is null)
        {
            if (TryBool(root, "account", "has_claude_max_20x") == true) plan = "Max 20x";
            else if (TryBool(root, "account", "has_claude_max_5x") == true) plan = "Max 5x";
            else if (TryBool(root, "account", "has_claude_max")    == true) plan = "Max";
            else if (TryBool(root, "account", "has_claude_pro")    == true) plan = "Pro";
        }

        return new ProfileInfo(name ?? email, plan);
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

record WindowStats(double UtilPct, DateTimeOffset ResetsAt);

record ExtraUsage(bool Enabled, decimal MonthlyLimit, decimal UsedCredits, double? Utilization, string Currency);

record ClaudeStatus(
    string Indicator,
    string Description,
    StatusComponent[] Components,
    StatusIncident? ActiveIncident,
    DateTimeOffset PageUpdatedAt,
    DateTimeOffset FetchedAt);

record StatusComponent(string Name, string Status);

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

record TrendSet(Trend? Session, Trend? Weekly, Trend? Sonnet, Trend? Opus);

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

sealed class StatusHolder
{
    ClaudeStatus? _s;
    public ClaudeStatus? Current => Volatile.Read(ref _s);
    public void Set(ClaudeStatus s) => Volatile.Write(ref _s, s);
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
    WindowStats? Sonnet,
    WindowStats? Opus,
    ExtraUsage? Extra,
    DateTimeOffset FetchedAt,
    string? Error)
{
    public static UsageSnapshot From(JsonElement root) => new(
        ParseWindow(root, "five_hour"),
        ParseWindow(root, "seven_day"),
        ParseWindow(root, "seven_day_sonnet"),
        ParseWindow(root, "seven_day_opus"),
        ParseExtra(root),
        DateTimeOffset.UtcNow,
        null);

    static WindowStats? ParseWindow(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null) return null;
        var util = el.GetProperty("utilization").GetDouble();
        var resetsProp = el.GetProperty("resets_at");
        if (resetsProp.ValueKind == JsonValueKind.Null) return null;
        return new WindowStats(util, DateTimeOffset.Parse(resetsProp.GetString()!, CultureInfo.InvariantCulture));
    }

    static ExtraUsage? ParseExtra(JsonElement root)
    {
        if (!root.TryGetProperty("extra_usage", out var el) || el.ValueKind == JsonValueKind.Null) return null;
        var utilEl = el.GetProperty("utilization");
        return new ExtraUsage(
            el.GetProperty("is_enabled").GetBoolean(),
            el.GetProperty("monthly_limit").GetDecimal() / 100m,   // API reports cents
            el.GetProperty("used_credits").GetDecimal() / 100m,    // API reports cents
            utilEl.ValueKind == JsonValueKind.Null ? null : utilEl.GetDouble(),
            el.GetProperty("currency").GetString() ?? "");
    }
}
