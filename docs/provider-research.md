# Provider research findings

Investigation into usage / rate-limit data sources for each AI provider VibeMeter
monitors. Conducted 29/06/2026. Status reflects the codebase at the time of writing.

| Provider | Outcome | Source used |
|----------|---------|-------------|
| Codex (OpenAI) | ✅ Live gauges | `~/.codex/auth.json` → ChatGPT `wham/usage` API |
| Claude Code | ✅ Live gauges | `~/.claude/usage_cache.json` (local cache) |
| Z.ai GLM | ✅ Live gauges | `api.z.ai/api/monitor/usage/quota/limit` (via `ZAI_API_KEY`) |
| Google AI Pro / Antigravity | ⏸ Parked | No public usage API; cookie-scraping blocked |

---

## Codex (OpenAI)

Already working (ported from the standalone CodexMeter project). Reads the long-lived
access token from `%USERPROFILE%\.codex\auth.json` and calls the ChatGPT backend:

- `https://chatgpt.com/backend-api/wham/usage` — primary + secondary rate-limit windows.
- `https://chatgpt.com/backend-api/wham/rate-limit-reset-credits` — reset credits.

No changes were made. Reference implementation for the `IUsageProvider` contract.

---

## Claude Code — ✅ implemented (live gauges)

### Finding
Claude Code does **not** expose a documented public usage REST API for subscription
plans. Instead, the CLI persists a usage snapshot locally that mirrors exactly what its
own `/usage` command displays.

### Source
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

### Implementation
`Providers/Claude/`: `ClaudeProvider` + `ClaudeAuth` + `ClaudeModels.cs`. Reads the cache
file and the account metadata, emits two gauges (5h, Weekly), a plan label, and a weekly
reset note. A staleness warning is surfaced if the cache timestamp is older than 6 hours
(Claude Code only refreshes it while running).

**Intentionally never read:** `%USERPROFILE%\.claude\.credentials.json` — that holds the
secret OAuth token and is not needed since the cache is self-contained.

Verified against the real cache on this PC: 5h → 96% remaining, Weekly → 79%, plan
"Claude Max 5x".

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

## Google AI Pro / Antigravity — ⏸ parked

### Finding
Google exposes **no public usage-meter API** for the AI Pro / AI Ultra subscriptions or
for Antigravity. Usage is enforced server-side (token-burn model over rolling 5-hour and
weekly windows) and surfaced only as in-app notifications inside Antigravity itself.

- The Google Cloud Console quota page covers Vertex AI / AI Studio (pay-per-use API
  projects) — a separate surface from the AI Pro subscription.
- No local quota file exists. The `~/.gemini/` tree holds conversations (opaque
  protobuf), onboarding state, and the signed-in account (`google_accounts.json`), but
  no remaining-quota figures.

### The only known route (unofficial)
The consumer Gemini web UI (`gemini.google.com/usage`) loads its numbers through
Google's private `batchexecute` RPC:

```
POST https://gemini.google.com/_/BardChatUi/data/batchexecute?rpcids=<ID>&...&bl=<build>
Content-Type: application/x-www-form-urlencoded
Authorization: SAPISIDHASH <unix_seconds>_<sha1(unix_seconds + " " + SAPISID + " " + origin)>
Cookie: <full google.com session cookies>

f.req=[[["<rpcid>","[<args as stringified JSON>]",null,"generic"]]]
```

The response is `)]}'`-prefixed, length-prefixed, with nested arrays and JSON-as-strings
that must be parsed manually. Auth requires the user's live Google session cookies
(`SAPISID` / `__Secure-1PAPISID` / `HSID` / `SSID` / `APISID` / `SID`, …).

### Blocker: Chrome 127+ app-bound cookie encryption
The user's machine runs **Chrome 149** with `os_crypt.app_bound_encrypted_key` present in
`Local State`. Chrome 127+ wraps the cookie-encryption key via an app-bound elevation
service (COM `IElevationManager::DecryptAppBoundKey`), so the cookie DB can no longer be
decrypted with the old DPAPI-only approach. Extracting cookies now requires invoking
that COM service (complex, version-coupled, actively hardened by Google).

### Risks of pursuing this
- **Fragility:** the `rpcids`, the `bl` build version, and the response shape are
  internal implementation details Google changes without notice.
- **Terms of Service:** automating around Google's session auth is a ToS grey area.
- **Cookie expiry:** session cookies expire; the integration would need periodic
  re-auth.

### Decision: parked
Google is **unregistered** for now (card removed). The `GoogleProvider` / `GoogleAuth`
files are retained for when we revisit. To resume:

1. Capture ground truth from `gemini.google.com/usage` via DevTools — a "Copy as cURL"
   of the `batchexecute` request that returns the usage numbers, plus its response body.
   This yields the exact `rpcids`, the `f.req` body, the `bl` param, and a working
   cookie to test with.
2. Build `SAPISIDHASH` + the `batchexecute` client + response parser against that
   capture; validate it returns live 5h/weekly figures.
3. Solve cookie acquisition — options: (a) Chrome app-bound decryption via the elevation
   COM service, (b) read Antigravity's Chromium profile cookies (`~/.gemini/
   antigravity-browser-profile/`) if the user logs in there (may not have app-bound
   encryption), or (c) manual cookie paste as an MVP.
4. Re-register `GoogleProvider` with a safe fallback to the detection-only state
   whenever the scrape fails (never show stale/wrong numbers).
