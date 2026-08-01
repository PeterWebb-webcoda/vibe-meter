# Provider research findings

Investigation into usage / rate-limit data sources for each AI provider VibeMeter
monitors. Conducted 29/06/2026. Status reflects the codebase at the time of writing.

| Provider | Outcome | Source used |
|----------|---------|-------------|
| Codex (OpenAI) | ✅ Live gauges | `~/.codex/auth.json` → ChatGPT `wham/usage` API |
| Claude | ✅ Live gauges | `~/.claude/usage_cache.json`, else `%APPDATA%\Claude\plan-usage-history.json` |
| Z.ai GLM | ✅ Live gauges | `api.z.ai/api/monitor/usage/quota/limit` (via `ZAI_API_KEY`) |
| Google AI Pro / Antigravity | ✅ Live gauges | `cloudcode-pa.googleapis.com` Cloud Code backend (via `~/.gemini/oauth_creds.json`) |

---

## Codex (OpenAI)

Already working (ported from the standalone CodexMeter project). Reads the long-lived
access token from `%USERPROFILE%\.codex\auth.json` and calls the ChatGPT backend:

- `https://chatgpt.com/backend-api/wham/usage` — primary + secondary rate-limit windows.
- `https://chatgpt.com/backend-api/wham/rate-limit-reset-credits` — reset credits.

No changes were made. Reference implementation for the `IUsageProvider` contract.

---

## Claude — ✅ implemented (live gauges)

### Finding
Claude does **not** expose a documented public usage REST API for subscription plans.
Instead, each Claude surface persists a usage snapshot locally, mirroring what the CLI's
own `/usage` command displays.

Crucially, **which file exists depends on how the user runs Claude**, so the provider
must not assume the CLI cache. Two surfaces write usage, and a PC may have either, both,
or neither:

| Surface | File | Fidelity |
|---------|------|----------|
| Claude Code CLI | `usage_cache.json` under the config dir | Exact percentages, exact reset timestamps, model-scoped weekly limits |
| Claude desktop app | `%APPDATA%\Claude\plan-usage-history.json` | Percentages only; resets inferred, no scoped limits |

Path discovery and source selection live in `ClaudeUsageSources`. It reads every
available source, takes whichever observed the account most recently, and backfills any
reset time the winner lacks from the other — but only when that time is still in the
future. Percentages are never blended across sources.

`CLAUDE_CONFIG_DIR` is honoured for both `usage_cache.json` and `.claude.json`, since it
relocates the whole `~/.claude` tree.

### Source 1 — the CLI cache
`%USERPROFILE%\.claude\usage_cache.json`, written and refreshed by the Claude Code CLI
while it runs. Relevant shape:

```jsonc
{
  "timestamp": "2026-06-28T12:17:13+10:00",
  "data": {
    "five_hour":  { "utilization": 4.0,  "resets_at": "2026-06-28T07:10:00+00:00", ... },
    "seven_day":  { "utilization": 21.0, "resets_at": "2026-07-02T20:00:00+00:00", ... },
    "limits": [
      { "kind": "session",     "group": "session", "percent": 4,  "severity": "normal", "resets_at": "..." },
      { "kind": "weekly_all",  "group": "weekly",  "percent": 21, "severity": "normal", "resets_at": "..." },
      { "kind": "weekly_scoped","group": "weekly", "percent": 1,  "scope": { "model": { "display_name": "Sonnet" } } }
    ],
    "extra_usage": { "is_enabled": false, ... },
    "spend": { ... }
  }
}
```

`utilization` / `percent` is **used-%**; `PercentRemaining = 100 − used`.

Plan / identity metadata comes from the `oauthAccount` block in
`%USERPROFILE%\.claude.json` (non-secret):

```jsonc
{
  "oauthAccount": {
    "emailAddress": "peter@webcoda.com.au",
    "organizationName": "Webcoda",
    "organizationType": "claude_team",
    "userRateLimitTier": "default_claude_max_5x"   // -> friendly "Claude Max 5x"
  }
}
```

### Source 2 — the desktop app's sampled history
`%APPDATA%\Claude\plan-usage-history.json`, appended by the Claude desktop app roughly
every 5 minutes while it runs. On a PC where Claude Code is only ever used *through* the
desktop app, this is the **only** usage file that exists — no `usage_cache.json` is
written at all, which is why an implementation that assumed the CLI cache showed nothing
on such machines.

```jsonc
{
  "version": 2,
  "samples": [
    { "t": 1785552896497, "org": "<uuid>", "u": { "fh": 0, "sd": 10 } }
  ]
}
```

`t` is epoch ms, `fh` is 5-hour used-%, `sd` is 7-day used-%. There are no reset
timestamps and no scoped limits.

**Deriving resets.** A window rollover shows up as the series falling — e.g. `fh`
99 → 0. The window therefore opened at that sample and closes one window length later.
Observed on real data, consecutive `fh` resets land 5h00–5h10 apart, the spread being
sampling granularity, so the projection is accurate to a few minutes. `DeriveReset` is
deliberately conservative and returns null — letting the UI say "current window" —
whenever it would otherwise be guessing:

- the window is idle (0% used), so no window is actually open;
- no fall of ≥5 percentage points is visible in the retained history;
- the projected reset has already passed, meaning the desktop app was closed across a
  rollover and the history has a hole in it.

That last case is common and is why an actively-idle machine shows a weekly countdown
but no 5-hour one. Guessing is avoided on purpose: Anthropic's 5-hour window may either
run as contiguous blocks or re-anchor to first use after a long idle, and the sampled
history cannot distinguish the two. Derived times are marked approximate — the weekly
reset note is prefixed `~` and gauge tooltips name the source.

Samples are filtered to the org of the most recent sample, so switching organisations
does not produce a reset derived from the wrong account's history.

### Implementation
`Providers/Claude/`: `ClaudeProvider` + `ClaudeAuth` + `ClaudeUsageSources` +
`ClaudeModels.cs`. `ClaudeUsageSources` normalises whichever file it finds into a
`ClaudeUsageSnapshot`; the provider turns that into two gauges (5h, Weekly) plus one per
scoped model, a plan label, and a weekly reset note. A staleness warning appears when the
figures are more than 6 hours old.

`ClaudeAuth.IsConfigured` is deliberately broader than "the CLI signed in" — a
desktop-only user never gets a `.claude.json`, which costs the plan label but nothing
else.

Failure isolation: a malformed or half-written source is skipped rather than failing the
whole provider, so a corrupt CLI cache still leaves the desktop history usable.

**Intentionally never read:** `%USERPROFILE%\.claude\.credentials.json` — that holds the
secret OAuth token and is not needed since the usage files are self-contained.

Verified on a CLI machine (5h → 96% remaining, Weekly → 79%, plan "Claude Max 5x") and on
a desktop-only machine (5h → 95%, Weekly → 89%, weekly reset derived as 7 Aug 06:01).

---

## Z.ai GLM — ✅ implemented (live gauges)

### Finding
Z.ai's coding plan (Lite / Pro / Max tiers, 5-hour rolling window + weekly cap) is
served by an undocumented but functional **monitor endpoint**:

```
GET https://api.z.ai/api/monitor/usage/quota/limit
Authorization: Bearer <ZAI_API_KEY>
```

It returns the subscription tier and each usage window. Verified response (this PC,
"lite" tier):

```jsonc
{
  "code": 200, "success": true,
  "data": {
    "level": "lite",
    "limits": [
      { "type": "TOKENS_LIMIT", "unit": 3, "percentage": 11, "nextResetTime": 1782705915362 }, // 5h
      { "type": "TIME_LIMIT",   "unit": 5, "percentage": 0,  "nextResetTime": 1782903488978, "usageDetails": [...] }  // monthly search/tools
      // "unit": 6 TOKENS_LIMIT observed on higher tiers -> weekly window
    ]
  }
}
```

`percentage` is **used-%**; `PercentRemaining = 100 − percentage`. `nextResetTime` is
Unix epoch **milliseconds**. The `(type, unit)` pairs map to gauges:
`TOKENS_LIMIT/3` → 5-hour, `TOKENS_LIMIT/6` → weekly, `TIME_LIMIT/5` → monthly
search/tools. `level` → plan label (e.g. "GLM Coding — Lite").

### Credentials
`ZAI_API_KEY` env var (canonical), `ANTHROPIC_AUTH_TOKEN` fallback, or
`~/.zai/user-settings.json` for the dedicated CLI. See `.env.example`.

### Why this is safe to call (unlike probing the inference endpoint)
This is a read-only `GET` against the monitor surface using the user's own API key —
not an inference call through an unsupported tool. Z.ai's risk-control warnings apply
to running inference from non-supported clients; a quota read does not trigger that.

### Implementation
`Providers/Zai/`: `ZaiProvider` + `ZaiAuth`. Calls the monitor endpoint, normalises
`data.limits[]` into gauges keyed by `(type, unit)`, derives the plan label from
`data.level`, and falls back to `NotConfigured` (no key) or `Error` (non-200 / bad
shape) without throwing.

---

## Google AI Pro / Antigravity — ✅ implemented (live gauges)

### Finding
The earlier "parked" conclusion (no public usage API; the only route being cookie
scraping of `gemini.google.com`, blocked by Chrome app-bound encryption) was **wrong**.
There is a clean, official OAuth API — the same one Antigravity and the Antigravity
Cockpit VS Code extension (`jlcodes.antigravity-cockpit`) call. The mechanism was
confirmed by reverse-engineering the cockpit extension's bundled JS and verified
end-to-end against a live account.

### The API — Google Cloud Code backend
Antigravity and the cockpit both read quota from `cloudcode-pa.googleapis.com`, Google's
internal Cloud Code service, using the user's own Google OAuth credentials:

```
POST https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist
POST https://cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels
Authorization: Bearer <OAuth2 access token>
Content-Type: application/json        # responses are gzip-compressed
```

`fetchAvailableModels` is the one that carries the gauges. Each model in the response has:

```jsonc
{
  "displayName": "Gemini 3.5 Flash (Low)",
  "quotaInfo": {
    "remainingFraction": 1,                 // 0..1  → PercentRemaining = ×100
    "resetTime": "2026-07-03T04:56:03Z"     // ISO-8601 UTC
  },
  // …many other capability fields, all ignored
}
```

Models are grouped into quota pools by the top-level `tieredModelIds` map
(`flashLite` / `flash` / `pro` → list of model ids). VibeMeter aggregates each pool into
one gauge = average `remainingFraction` across the pool's members, with the pool's
earliest `resetTime` — matching the cockpit's "grouping" view. `loadCodeAssist` returns
the subscription tier (`currentTier.name` for free users, e.g. "Antigravity";
`paidTier.name` / `paidTier.availableAICredits` for paid), used for the plan label and
cached for 10 minutes.

### Credentials
The Gemini CLI / Antigravity store a long-lived **refresh token** (and a short-lived
access token) as plain JSON at `%USERPROFILE%\.gemini\oauth_creds.json`:

```jsonc
{
  "access_token": "ya29.a0…",      // ~1h lifetime, refreshed on demand
  "refresh_token": "1//0g…",       // long-lived — the reusable credential
  "scope": "https://www.googleapis.com/auth/cloud-platform …",
  "token_type": "Bearer",
  "expiry_date": 1781002694488     // epoch ms
}
```

The refresh token is exchanged at `https://oauth2.googleapis.com/token` for a fresh
access token using the public OAuth client credentials shipped in the Antigravity
Cockpit extension (client ID `1071006060591-…apps.googleusercontent.com`, secret
`GOCSPX-…`). These are not secrets — they are distributed in the extension's bundled
JavaScript, the same model the Gemini CLI itself uses. Account identity (non-secret)
still comes from `~/.gemini/google_accounts.json`.

### Why this is safe
This is a read-only call against the user's own subscription, authed with the user's own
refresh token, using the same client identity as the official Antigravity Cockpit
extension. No cookies are scraped, no scraping of `gemini.google.com` occurs, and
Chrome's app-bound cookie encryption is no longer relevant.

### Implementation
`Providers/Google/`: `GoogleProvider` + `GoogleAuth` + `GoogleApiClient` + `GoogleModels`.
`GoogleAuth` reads the refresh token from `oauth_creds.json` and handles token
exchange/caching. `GoogleApiClient` POSTs the two `/v1internal:` methods with gzip
handling. `GoogleProvider` builds per-pool gauges, derives the plan label from the tier
name + account email, and falls back to `NotConfigured` (no token) or `Error` (API/auth
failure) without throwing — `loadCodeAssist` failure is non-fatal (partial `Ok`).

### Deferred follow-ups (out of scope for the initial build)
- **Standalone browser-OAuth login** — a full PKCE flow so VibeMeter works without the
  Gemini CLI/Antigravity installed. `GoogleAuth` is structured to slot this in.
- **Antigravity `state.vscdb` fallback** — the IDE's own token copy lives in
  `%APPDATA%\Antigravity\User\globalStorage\state.vscdb` as base64-protobuf. Adding it
  would require a SQLite + lightweight protobuf dependency; deferred because the
  plain-JSON `oauth_creds.json` covers the common case.
