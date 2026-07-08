# Claude Usage Monitor

A terminal dashboard for your **Claude** subscription usage. It reads the OAuth
token that `claude login` already stored on your machine, polls Anthropic's usage
endpoint, and paints a live TUI with your session/weekly windows, per-model
breakdown, extra-usage spend, burn-rate pace, and current Claude service status.

```sh
dotnet run ClaudeUsage.cs
```

That's it — no build step, no `.csproj`. This is a **.NET file-based app**: the
single `ClaudeUsage.cs` declares its own dependencies via `#:package` directives
and runs directly.

## Requirements

- **.NET SDK 10.0** or newer (file-based apps require .NET 10). Check with `dotnet --version`.
- An authenticated Claude CLI session — credentials are read from
  `~/.claude/.credentials.json`. If you don't have them, run `claude login` first.

Dependencies (`Spectre.Console`) are restored automatically on first run.

## Usage

```sh
dotnet run ClaudeUsage.cs                 # live dashboard, refreshes every 15 min
dotnet run ClaudeUsage.cs -- --once       # print one report and exit
dotnet run ClaudeUsage.cs -- --interval 5 # refresh every 5 minutes
dotnet run ClaudeUsage.cs -- --dump       # probe raw usage endpoints (debug)
dotnet run ClaudeUsage.cs -- --help
```

> The `--` separates `dotnet run` arguments from the app's own arguments.

### Flags

| Flag | Alias | Description |
| --- | --- | --- |
| `--once` | `-1` | Print the report once and exit (good for scripts/cron). |
| `--interval <minutes>` | | Live refresh interval. Default `15`. |
| `--dump` | | Probe candidate OAuth endpoints and dump raw JSON. |
| `--help` | `-h` | Show help. |

### Live-mode keys

- `r` — refresh now
- `q` — quit

## How it works

- **Auth** — reuses the OAuth access token from `~/.claude/.credentials.json`; the
  token is refreshed from disk as the Claude CLI rotates it. Nothing is sent
  anywhere except Anthropic's own API.
- **Rendering** — a background fetcher owns the network and publishes immutable
  view-models; the render loop only paints. The clock, countdown, and keypresses
  stay responsive even during an in-flight request or its timeout.
- **Resilience** — the last successful response is cached, so panels paint
  instantly on startup and survive transient network/API failures with backoff.
- **Trends** — a small local history is kept to show short-term deltas and
  burn-rate pace against your window limits.

### Local state

Cache and history are stored outside the repo, under your user profile:

- `%LOCALAPPDATA%\ClaudeUsage\cache.json`
- `%LOCALAPPDATA%\ClaudeUsage\history.json`

## Privacy

This tool talks only to `api.anthropic.com` (usage/profile) and
`status.claude.com` (service status). It does not transmit your token or usage
data to any third party. Your credentials never leave your machine except as the
`Authorization` header to Anthropic's own API.

## License

[MIT](LICENSE) © Loïc Baumann
