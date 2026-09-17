# VibeMeter.Agent — deployment

Running the headless agent unattended on the two machines that report into the
collection API:

- **Rock** — Windows 11 workstation (Scheduled Task)
- **Gladux** — headless Linux box (systemd)

The agent must start at boot without anyone logged on, survive reboots, and be
diagnosable over a remote session. What the agent *does* — configuration
variables, freshness gating, the offline queue — is documented in
[`agent.md`](agent.md); this file covers builds, hosting, and troubleshooting
only. Everything below is taken from `VibeMeter.Agent`'s source.

## Authentication status: NOT YET IMPLEMENTED

> **The only credential mechanism that exists today is an environment
> variable.** `EnvironmentAccessTokenProvider` (the sole `IAccessTokenProvider`
> implementation) reads `VIBEMETER_AGENT_TOKEN` fresh from the process
> environment on every publish; the token is never cached, logged, or written
> to disk by the agent.
>
> There is **no** interactive sign-in, **no** device-code prompt, **no**
> MSAL/broker flow, and **no** token cache file. None of that exists yet. Real
> authentication is planned against the `IAccessTokenProvider` seam
> (`VibeMeter.Agent/AccessToken/IAccessTokenProvider.cs`); when it lands, this
> section should be rewritten. Until then, obtaining the token value is a
> manual, out-of-band step.

## 1. Publishing a build

The agent project is `VibeMeter.Agent\VibeMeter.Agent.csproj` (`net10.0`
console app, no WPF). Run from the repository root:

```powershell
# Framework-dependent (FDD): needs the .NET 10 runtime installed on the target
dotnet publish VibeMeter.Agent\VibeMeter.Agent.csproj -c Release -r win-x64   --self-contained false -o publish\agent-win-x64
dotnet publish VibeMeter.Agent\VibeMeter.Agent.csproj -c Release -r linux-x64 --self-contained false -o publish\agent-linux-x64

# Self-contained (SC): runtime bundled, nothing to install on the target
dotnet publish VibeMeter.Agent\VibeMeter.Agent.csproj -c Release -r win-x64   --self-contained true -o publish\agent-win-x64-sc
dotnet publish VibeMeter.Agent\VibeMeter.Agent.csproj -c Release -r linux-x64 --self-contained true -o publish\agent-linux-x64-sc
```

Cross-compiling from the Windows workstation to `linux-x64` works for either
variant — the agent is pure managed code with no native dependencies.

| | Framework-dependent | Self-contained |
|---|---|---|
| Output size | a few MB | ~70–90 MB |
| Needs .NET 10 runtime on target | yes (`dotnet --list-runtimes` should show `Microsoft.NETCore.App 10.x`) | no |
| Tracks runtime patches/updates on the target | yes | no — frozen at publish time |

Either is fine. If Gladux will not get runtime updates while you are away, ship
the self-contained build so a missing or upgraded runtime cannot break it.

The FDD and SC builds both produce a launcher executable — `VibeMeter.Agent.exe`
on Windows, `VibeMeter.Agent` on Linux — next to `VibeMeter.Agent.dll`.
(Launching the `.dll` directly with `dotnet VibeMeter.Agent.dll` works too.)

## 2. Gladux (Linux) — systemd

`deploy/vibemeter-agent.service` and `deploy/vibemeter-agent.env.example` are
the two supporting files. The unit runs the agent as a **non-root** user,
restarts it always (30 s pause), and logs to journald.

### Install sequence

```bash
# 1. Dedicated non-root service account. Its home IS the install directory,
#    which keeps provider credentials and the offline queue in one place.
sudo useradd --system --create-home --home-dir /opt/vibemeter --shell /usr/sbin/nologin vibemeter

# 2. Copy the published linux-x64 output into place.
sudo mkdir -p /opt/vibemeter
sudo cp -r publish/agent-linux-x64/* /opt/vibemeter/     # or the -sc variant
sudo chmod +x /opt/vibemeter/VibeMeter.Agent
sudo chown -R vibemeter:vibemeter /opt/vibemeter

# 3. Secrets file — created from the template, owned by root, mode 600.
sudo mkdir -p /etc/vibemeter
sudo install -o root -g root -m 600 deploy/vibemeter-agent.env.example /etc/vibemeter/agent.env
sudoedit /etc/vibemeter/agent.env        # fill in the two required variables

# 4. Unit file.
sudo cp deploy/vibemeter-agent.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now vibemeter-agent

# 5. Confirm.
systemctl status vibemeter-agent
```

### Reading the logs (journald)

The agent writes single-line UTC-stamped messages to stdout (`[info]`,
`[warn]`) and stderr (`[error]`); journald captures both.

```bash
journalctl -u vibemeter-agent -f              # follow live
journalctl -u vibemeter-agent -b              # since this boot
journalctl -u vibemeter-agent --since "2 hours ago"
journalctl -u vibemeter-agent -o cat          # raw lines (they are already UTC-stamped)
journalctl -u vibemeter-agent | grep '\[error\]'   # errors only
```

### Provider credentials on Gladux

Providers look for their local credentials under the **running user's profile**
(e.g. Codex `~/.codex/auth.json`, Claude `~/.claude` — honouring
`CLAUDE_CONFIG_DIR` — and Z.ai keys from environment variables or
`~/.zcode/config.json`). Under the unit, that profile is `/opt/vibemeter`. Copy
or symlink the credential directories you want Gladux to report on into
`/opt/vibemeter`, and add any provider key environment variables to
`/etc/vibemeter/agent.env`. A provider with no credentials reports
`not-configured` — see the warning in [§7](#7-a-warning-about-not-configured)
before deciding that is acceptable.

## 3. Rock (Windows) — Scheduled Task

The recommended unattended option is a **Scheduled Task with a boot trigger,
running whether or not the user is logged on**. (Not a Windows service: the
agent is a plain console app that handles SIGTERM/Ctrl+C, not the Service
Control Manager — `sc create` would start it and kill it ~30 s later for never
reporting a status.)

`deploy/vibemeter-agent-task.xml` defines the task: boot trigger with a 30 s
delay, **no execution-time limit** (Task Scheduler's default 72-hour limit
would kill the agent mid-flight — the plain `schtasks` command below cannot
disable it, which is why the XML is the recommended install), restart-on-
failure after 1 minute, and the action wrapped in `cmd.exe` purely to append
console output to `C:\ProgramData\VibeMeter\Agent\agent.log` — a scheduled
task's console is otherwise discarded, and that log is what you will read over
a remote session.

### Install sequence

```powershell
# 1. Copy the published win-x64 output into place (paths assumed by the XML).
New-Item -ItemType Directory -Force "C:\Program Files\VibeMeter\Agent"
Copy-Item publish\agent-win-x64\* "C:\Program Files\VibeMeter\Agent\"   # or the -sc variant

# 2. Log file directory (the cmd.exe wrapper appends here).
New-Item -ItemType Directory -Force "C:\ProgramData\VibeMeter\Agent"
icacls "C:\ProgramData\VibeMeter\Agent" /grant "ROCK\vibemeter-agent:(OI)(CI)M"

# 3. Edit deploy\vibemeter-agent-task.xml: set UserId to the task account and
#    fix the install path if it differs. Then import — it prompts for the
#    account's password, which never appears on the command line.
schtasks /Create /F /TN "VibeMeter Agent" /XML deploy\vibemeter-agent-task.xml

# 4. Confirm.
schtasks /Query /TN "VibeMeter Agent" /V /FO LIST
```

A minimal one-liner also works if you accept the default settings:

```powershell
schtasks /Create /F /TN "VibeMeter Agent" /SC ONSTART /TR "\"C:\Program Files\VibeMeter\Agent\VibeMeter.Agent.exe\"" /RU ROCK\vibemeter-agent /RP <password>
```

… but it carries the 72-hour execution limit, no restart-on-failure, and no
log capture. Prefer the XML.

### Task account, and how the task sees environment variables

Use a **dedicated local account** (e.g. `ROCK\vibemeter-agent`) rather than
`SYSTEM`: providers read credentials from the running user's profile
(`%USERPROFILE%\.codex`, `%USERPROFILE%\.claude`, `%USERPROFILE%\.zcode`), and
under `SYSTEM` all four providers report `not-configured` — see
[§7](#7-a-warning-about-not-configured).

Environment variables reach the task through normal Windows environment
scopes, read once when the task's process starts:

- **Machine scope (simplest)** — from an elevated prompt:
  `setx /M VIBEMETER_API_BASE_URL "https://api.example.com"` and
  `setx /M VIBEMETER_AGENT_TOKEN "<token>"`. Visible to every account,
  including the task account.
- **User scope** — log on once as the task account and run the same `setx`
  commands without `/M`. They land in that account's profile and are applied
  when the task logs the account on.

Two `setx` caveats: values longer than 1024 characters are truncated, and a
change is only picked up by processes **started afterwards** — stop and restart
the task (`schtasks /End` then `schtasks /Run /TN "VibeMeter Agent"`) to
re-read them.

## 4. Environment variable reference

Every variable the agent reads, from `AgentConfig.cs` (the token constant also
appears in `EnvironmentAccessTokenProvider.cs`). All four are validated at
startup; every problem found is aggregated into **one** message on stderr of
the form `The VibeMeter agent is not configured correctly:` followed by a
bullet per problem, and the process exits **1**.

| Variable | Required | Default | If missing or wrong |
|---|---|---|---|
| `VIBEMETER_API_BASE_URL` | yes | — | Startup abort, exit 1. Must be an **absolute http(s) URL**; anything else (relative, `ftp:`, garbage) is rejected with the offending value echoed back. A trailing `/` is tolerated (trimmed). |
| `VIBEMETER_AGENT_TOKEN` | yes | — | **Empty at startup**: abort, exit 1 (`set it to the agent's bearer token before starting`). Presence-only check at startup — a *wrong* value surfaces later per publish as an auth failure (see §6.1); the process stays up and queues snapshots. |
| `VIBEMETER_AGENT_INTERVAL_SECONDS` | no | `300` | Non-integer, or less than `10`, aborts at startup, exit 1. |
| `VIBEMETER_AGENT_STALENESS_MINUTES` | no | `20` | Non-integer, or less than `1`, aborts at startup, exit 1. |

Nothing else is read. In particular the queue directory is **not**
configurable: it is always `<user-data>\VibeMeter\VibeMeter.Agent\offline-queue`
(see §5).

Under systemd, `Restart=always` turns a startup abort into a restart every
30 s; the journal shows the full message each attempt. On Windows the task's
RestartOnFailure does the same at a 1-minute cadence, with the message in
`agent.log`. A repeating "not configured correctly" line is therefore the
signature of a bad environment, and the fix is in the env file / `setx`, not in
the service.

## 5. Verification

### Expected first-run log lines

The first cycle starts immediately; the exact wording comes from
`AgentHost.RunAsync`:

```text
2026-09-17 03:24:05 Z [info] Agent starting - publishing to https://api.example.com every 300s.
2026-09-17 03:24:05 Z [info] Providers: claude, codex, zai, google. Offline queue: /opt/vibemeter/.local/share/VibeMeter/VibeMeter.Agent/offline-queue (capacity 500).
2026-09-17 03:29:07 Z [info] Published snapshot for 4 provider(s) in 812 ms.
```

That third line, repeating once per interval, is the **only** positive proof
that publishing works. Its absence for more than one interval while the process
is active means something is failing — the matching `[warn]`/`[error]` line in
the same window says which failure. A misconfigured agent instead prints the
aggregated `not configured correctly` message (stderr) and exits 1.

### Where the offline queue lives

One UTF-8 JSON file per queued snapshot, named
`{utcTicks:D19}-{idempotencyKey}.json`, capacity 500, oldest dropped when full.
Entries hold only the mapped snapshot (ids, states, percentages) — never
credentials.

| OS | Path (for the account running the agent) |
|---|---|
| Linux (Gladux, as installed) | `/opt/vibemeter/.local/share/VibeMeter/VibeMeter.Agent/offline-queue` (`$XDG_DATA_HOME` if set, else `$HOME/.local/share` — the unit pins `HOME=/opt/vibemeter`) |
| Windows (Rock) | `%APPDATA%\VibeMeter\VibeMeter.Agent\offline-queue` → e.g. `C:\Users\vibemeter-agent\AppData\Roaming\VibeMeter\VibeMeter.Agent\offline-queue` |

The queue is normally empty (each publish deletes its entry). A **growing**
count — visible as `Snapshot queued for later upload (N pending)` lines with
increasing N — is the reliable "failing to publish" signal.

### "Not running" vs "running but failing to publish"

| Question | Gladux | Rock |
|---|---|---|
| Is the process alive? | `systemctl is-active vibemeter-agent` | `Get-Process VibeMeter.Agent`; task shows State `Running` / LastTaskResult `0x41301` |
| When did it last publish? | `journalctl -u vibemeter-agent -o cat \| grep -F 'Published snapshot' \| tail -1` | `Select-String -Path "C:\ProgramData\VibeMeter\Agent\agent.log" -Pattern 'Published snapshot' \| Select-Object -Last 1` |
| What did it last say? | `journalctl -u vibemeter-agent -o cat -n 20` | `Get-Content "C:\ProgramData\VibeMeter\Agent\agent.log" -Tail 20` |
| Restart loop (config error)? | Same `Agent starting` line repeating every ~30 s with no `Published` lines | Same line repeating every ~1 min in `agent.log` |

"Running but failing" looks like: process alive, no `Published snapshot` line
for ≥ 1 interval, and instead `API unreachable (…)` (network/server),
`Authentication failed (…)` (token), or `API rejected the snapshot permanently`
(bad request / clock skew) every cycle, plus `Snapshot queued … (N pending)`.

## 6. Troubleshooting

### 6.1 Token missing or expired

- **Missing at startup** → abort, exit 1, message names the variable:
  `VIBEMETER_AGENT_TOKEN is required - set it to the agent's bearer token before starting.`
  (Restart loop per §4 until fixed.)
- **Wrong or expired value** → every publish fails with
  `Authentication failed (HTTP 401/403 …) Snapshot queued; it will flush once VIBEMETER_AGENT_TOKEN is accepted.`
  The snapshot is queued each cycle (bounded at 500; the oldest is dropped when
  full), so no data is lost while you fix it. After correcting the value and
  restarting, the next cycle drains oldest-first: `Flushed N queued snapshot(s).`
- **Rotation mechanics**: the provider re-reads the variable on every publish,
  but a process's environment is fixed at start — so rotation is: update
  `/etc/vibemeter/agent.env` + `sudo systemctl restart vibemeter-agent`
  (Gladux), or re-run `setx` + `schtasks /End` and `/Run` (Rock).
- **NOT YET IMPLEMENTED**: there is no automatic renewal, refresh, or
  re-issue of this token anywhere in the codebase. When the token expires you
  must supply a new value by hand. Real authentication is planned against
  `IAccessTokenProvider` — see the notice at the top of this file.

### 6.2 API unreachable

Network, DNS, TLS and 5xx responses are transient: the publisher retries in
place (3 attempts, 30 s HTTP timeout each, ~2 s/4 s jittered backoff), then the
snapshot is queued and every later cycle flushes oldest-first. Recovery is
visible as `Flushed N queued snapshot(s)`. Entries the API will *never* accept —
e.g. older than the server's accepted observation window — are discarded with
`Discarding a queued snapshot the API will never accept (HTTP 400 …)`; this is
expected after a long outage and is not data you can recover by retrying.
Sanity-check reachability from the box itself
(`curl -s -o /dev/null -w "%{http_code}" https://api.example.com/api/v1/ai-usage/snapshots`
returns *some* HTTP status — even 401 — if the endpoint is reachable; a
connection error is a network/DNS problem).

### 6.3 All providers stale → cycle skipped

```text
2026-09-17 08:14:05 Z [warn] All 4 provider report(s) were omitted as stale; publishing nothing this cycle so a fresher reading from another machine can win the server's newest-first merge.
```

Not a fault — the agent is protecting the merge (see
[`agent.md`](agent.md)): file-derived readings (Claude) only refresh while the
corresponding CLI runs on that machine, and publishing days-old data stamped
"now" would mask a fresh reading from the other machine. Live-HTTP providers
(Codex usage API, Z.ai) are never stale, so this line for *all* providers means
the machine genuinely has no fresh data — expected on an idle box. The process
is healthy; the cycle simply publishes nothing.

### 6.4 Clock skew

`observedAt` is always `DateTimeOffset.UtcNow` — UTC with zero offset, which
the API requires (it rejects any other offset, and rejects far-future
timestamps). A wrong system clock therefore shows up as:

```text
2026-09-17 08:14:07 Z [error] API rejected the snapshot permanently (HTTP 400 …); it will not be retried.
```

That snapshot is *not* queued — retrying can never succeed — and it repeats
every cycle until the clock is fixed. Note the asymmetry: a future-skewed
timestamp is treated as *fresh* by the staleness gate (negative age), so skew
never causes omission — it surfaces only as the 400 above. Fix:
`w32tm /resync` (Rock, elevated) or `timedatectl set-ntp true` (Gladux), then
confirm with `w32tm /query /status` / `timedatectl`.

### 7. A warning about `not-configured`

A provider without local credentials reports `not-configured`, which — unlike
stale data — **is still published** (freshness gating only omits present-but-
stale readings). Because the server merges per provider newest-first by publish
time and ignores the state field, an agent running under an account with no
provider credentials will publish `not-configured` every cycle and **mask the
real readings from the other machine**. Signature: this machine publishes
happily every interval (`Published snapshot for 4 provider(s)`) while the
dashboard shows `not-configured` everywhere. Fix: run the agent under an
account whose profile holds the provider credentials (§2/§3), or stop the
agent on that machine.

## Files in `deploy/`

| File | Target | Purpose |
|---|---|---|
| `vibemeter-agent.service` | Gladux | systemd unit (non-root, `Restart=always`, journald) |
| `vibemeter-agent.env.example` | Gladux | template for `/etc/vibemeter/agent.env` (mode 600) |
| `vibemeter-agent-task.xml` | Rock | Scheduled Task definition (boot trigger, no time limit, log capture) |
