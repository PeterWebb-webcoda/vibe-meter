# VibeMeter.Agent — configuration and freshness

`VibeMeter.Agent` is the headless sibling of the WPF app: every interval it
collects usage from every provider, maps the reports onto the collection API's
snapshot contract, and POSTs them — queueing snapshots for later whenever the
API is unreachable. A cycle is not the same thing as a row: the publish policy
below decides which cycles are worth writing.

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

## Publish policy (why a cycle can write no row)

Collecting is cheap; a row in a DTU-constrained shared database is not. A host
that refreshes for the sake of its own UI — the tray app defaults to every 60
seconds — would otherwise write about **1,440 near-identical rows per user per
day**. `VibeMeter.Publishing.PublishPolicy` decides, per cycle, whether the
snapshot is worth sending:

- **Content fingerprint.** A SHA-256 over the *mapped document with
  `observedAt` excluded*, used only to answer "has anything actually changed".
  It is never sent anywhere. The `Idempotency-Key` header is unchanged — it
  still hashes the exact bytes, `observedAt` included, because a key that
  ignored the timestamp would let two genuinely different documents collide on
  one key and the API answers `CONFLICT` to a repeated key with a different
  body.
- **Minimum interval (4 min 50 s).** Never publish more often than this, *even
  when the figures changed* — otherwise a provider whose percentage ticks every
  cycle hands the cap straight back. Nothing is lost: the newer figures go out
  on the first cycle after it elapses. Worst case ≈ 298 rows per day.
- **Heartbeat (23 minutes).** Publish anyway when nothing has changed for this
  long. Without it the stored `observedAt` decays into "when the numbers last
  moved", and a machine that is switched off becomes indistinguishable from one
  whose quota simply has not budged. Idle floor ≈ 62 rows per day.
- **One startup publish (desktop host only).** The tray app may spend a single
  publish on having started, so a relaunch is not silent for up to a heartbeat.
  It is granted *below* the minimum interval, never above it: a host that starts
  inside the floor waits for the floor rather than buying a row, and the
  allowance is spent the first time anything is published. The agent does not
  get one — it is restarted by service managers and supervisors, and a crash
  loop must not become a write loop.

The decision is a pure function of (previous fingerprint, last publish time,
current fingerprint, now, startup publish unspent), so it can be read and
tested on its own. The two values it remembers live in a single
`publish-policy.state` file inside the host's offline-queue directory — the one
path the publishing library is already given — so a service restart, a
supervisor restarting a crash loop, a scheduled `--once`, or the tray app
rebuilding its publish host when its settings are saved does not publish afresh
each time. Losing that file costs one extra row, never data.

Both hosts persist that state. The tray app originally kept it in memory, on
the reasoning that a relaunch is a person and one row is cheap — but its host is
rebuilt at startup *and on every saved setting*, so each rebuild read back
"nothing published yet" and published regardless of the floor. Observed in
production as four rows inside five minutes against a 290-second minimum. The
startup publish above is that requirement done as a bounded rule instead.

`--once` treats a policy skip as success (exit `0`): the run did its job and
concluded the server already knows.

## Deployment

For building (`dotnet publish`), running unattended — systemd on Linux,
a Scheduled Task on Windows — the environment variable reference, and
troubleshooting, see [agent-deployment.md](agent-deployment.md).
