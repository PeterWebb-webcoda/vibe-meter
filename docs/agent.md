# VibeMeter.Agent — configuration and freshness

`VibeMeter.Agent` is the headless sibling of the WPF app: every interval it
collects usage from every provider, maps the reports onto the collection API's
snapshot contract, and POSTs them — queueing snapshots for later whenever the
API is unreachable.

## Configuration (environment variables)

| Variable | Required | Default | Meaning |
|----------|----------|---------|---------|
| `VIBEMETER_API_BASE_URL` | yes | — | Collection API root, e.g. `https://api.example.com`. Absolute http(s) only. |
| `VIBEMETER_AGENT_TOKEN` | yes | — | Bearer token for the collection API. Read fresh for every publish; never logged or written to disk. |
| `VIBEMETER_AGENT_INTERVAL_SECONDS` | no | `300` | Seconds between publish cycles. Minimum 10. |
| `VIBEMETER_AGENT_STALENESS_MINUTES` | no | `20` | How old a provider's **underlying data** may be before the agent omits it from the snapshot. Minimum 1. See below. |

Invalid values fail fast at startup with one aggregated message naming every
problem.

## Freshness gating (why a provider can be missing from a snapshot)

**The trap.** The collection API composes each provider's latest view by
taking, per `providerId`, the **first occurrence across snapshots ordered
newest-first by `observedAt`** — and `observedAt` is the *publish* time, not
the time the data was observed. Some providers are read from local files that
only refresh while the corresponding CLI runs on *that* machine — Claude reads
`~/.claude/usage_cache.json` (or the desktop app's
`plan-usage-history.json`). An idle machine can hold a days-old Claude reading
yet stamp its snapshot `observedAt=now`, win the merge, and mask the fresh
reading from the machine actually being used.

**Why omission, not `state="stale"`.** The merge is time-ordered and ignores
the state field, so a stale-but-newer entry still masks a fresh-but-older one.
The only correct fix is for the agent to say nothing about that provider for
the cycle; the other machine's fresh reading then wins.

**How freshness is derived, per provider:**

- **File-derived (Claude)** — the source's own observation time: the timestamp
  inside the CLI's `usage_cache.json`, the latest sample in the desktop
  history, or the file's last-write time when neither is present.
- **Fetched live over HTTP each cycle (Codex usage API, Z.ai)** — inherently
  current; never omitted for staleness.
- **Cannot be determined** — the report is **kept** (an inability to measure
  age must never silently erase data) and a note logged once per provider.

**Behaviour:**

- A successful (`Ok`) report whose underlying data is older than the threshold
  is **omitted** and the omission logged with its age and reason. "Older" is
  strict: an observation exactly at the threshold is still fresh.
- If **every** provider would be omitted, the cycle is skipped entirely — the
  API requires 1..16 providers, and an empty document would be a guaranteed
  400.
- `not-configured`, `disabled`, and `error` reports keep their existing
  behaviour; gating only concerns data that is present but stale.
