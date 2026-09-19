# Vibe Meter

A system-tray widget that monitors **AI usage and rate limits across multiple
providers** — Codex (OpenAI) and Claude Code today, with Z.ai GLM detection — from a
single, always-on-top meter.

Each provider is a plugin implementing `IUsageProvider`; the UI binds identically no
matter the source.

![Vibe Meter](assets/vibemeter-screenshot.png)

## Provider status

| Provider | Status | How it reads usage |
|----------|--------|--------------------|
| **Codex** (OpenAI) | ✅ Live | `~/.codex/auth.json` → ChatGPT `wham/usage` API (5h + weekly windows) |
| **Claude** | ✅ Live | `~/.claude/usage_cache.json`, else `%APPDATA%\Claude\plan-usage-history.json` (5h + weekly, plan tier) |
| **Z.ai GLM** | ✅ Live | `api.z.ai/api/monitor/usage/quota/limit` via `ZAI_API_KEY` (5h, weekly, monthly) |
| **Google AI Pro** | ⏸ Parked | No public usage API; see [`docs/provider-research.md`](docs/provider-research.md) |

## Install

1. Download **`VibeMeter-win-x64.zip`** from the latest [Release](../../releases).
2. Extract it anywhere.
3. Run `VibeMeter.exe`.

Requirements: **Windows 10/11 x64**. The build is self-contained — no .NET runtime
install required. Sign in to [Codex](https://github.com/openai/codex) and/or
[Claude Code](https://claude.com/claude-code) on the PC, and/or set the
`ZAI_API_KEY` environment variable, for usage data to appear.

### Linux (beta)

A native Linux tray app built on [Avalonia](https://avaloniaui.net/) lives in
`VibeMeter.Avalonia`. There is no release artifact yet — build it from source:

```bash
dotnet run --project VibeMeter.Avalonia/VibeMeter.Avalonia.csproj -c Release
```

It starts minimised to the system tray. Requires a desktop that hosts a
StatusNotifierItem tray: **Cinnamon, KDE and XFCE work**; GNOME needs the
AppIndicator extension.

If no StatusNotifierItem tray is available, the app shows its window at startup and says
so, rather than running with no icon and no way to reach it.

**Known limitations in this beta:**

- Settings are applied on **Save & Close** only — changing a control has no live effect.
- The meter-style setting persists but has no visual effect; gauges always render as bars.
  The Circular and Battery styles are Windows-only so far.
- **"Launch at Login" does nothing on Linux.** It is disabled rather than implemented — an
  XDG autostart entry is not written yet.
- **Google accounts cannot be added on Linux.** Secret protection is implemented for Windows
  only (DPAPI), and a refresh token will not be stored unprotected, so the flow declines
  before opening a browser instead of failing after you have granted consent.
- **A `settings.json` carried over from Windows keeps working, but is not migrated.** An
  account whose token is still in the clear from a pre-protection build is used as found and
  left exactly as found. Nothing is destroyed, but the plaintext stays in that file until it
  is opened on a host that can protect it. An account already sealed with DPAPI on Windows
  cannot be opened here and is skipped.

## Privacy

VibeMeter is local-first:

- It reads only local provider files (`~/.codex`, `~/.claude`) and, for Codex, calls
  OpenAI's own usage endpoint using the token the Codex CLI already stored.
- It never sends your usage data anywhere, collects no telemetry, and has no accounts.
- Claude usage is read entirely from the local files Claude already maintains (the CLI's
  usage cache or the desktop app's usage history) — no token, no network.

Settings are stored in `%APPDATA%\VibeMeter\settings.json` on Windows and
`~/.config/VibeMeter/settings.json` on Linux. No secret is kept there — a Google refresh
token is stored only in protected form — but it does hold your Google account email list and,
if you enable publishing, the tenant and client ids. On Linux the file and its directory are
restricted to your user.

**On Linux, opt-in publishing caches its sign-in token unencrypted.** The Microsoft identity
library's encrypted Linux store needs libsecret and a keyring, which a headless or minimal
desktop may not have, so the cache is written as a plain file readable by your user account
(`~/.config/VibeMeter/`). It is a credential: treat it as one. Publishing is off by default,
and nothing is cached unless you turn it on and sign in.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build VibeMeter\VibeMeter.csproj
dotnet run --project VibeMeter\VibeMeter.csproj
```

To produce a self-contained single-file release build:

```powershell
dotnet publish VibeMeter\VibeMeter.csproj -c Release -r win-x64 -o publish
```

## Architecture

MVVM + provider plugins. Adding a provider touches only `Providers/<Name>/` plus one
line in `Services/ProviderRegistry.cs`. See `docs/provider-research.md` for the
data-source investigation behind each provider.

```
VibeMeter/
├── Core/        IUsageProvider contract + normalised ProviderUsage model
├── Models/      UI models (gauges, tint, meter style) + SettingsData
├── Providers/   One folder per provider (Codex, Claude, Zai, Google)
├── Services/    SettingsService, ProviderRegistry
├── ViewModels/  MainViewModel (aggregator), ProviderViewModel (one card), Settings
└── Views/       MainWindow (provider cards), SettingsWindow, meter controls
```

## License

[MIT](LICENSE).
