# VibeMeter.Agent — deployment

Running the headless agent unattended on the two machines that report into the
collection API:

- **the Windows workstation** — Windows 11 workstation (Scheduled Task)
- **the Linux box** — headless Linux box (systemd)

The agent must start at boot without anyone logged on, survive reboots, and be
diagnosable over a remote session. What the agent *does* — configuration
variables, freshness gating, the offline queue — is documented in
[`agent.md`](agent.md); this file covers builds, hosting, and troubleshooting
only. Everything below is taken from `VibeMeter.Agent`'s source.

## Authentication

The agent presents a bearer token to the collection API and obtains it one of
two ways. **Setting `VIBEMETER_AGENT_CLIENT_ID` opts into the device-code
flow; without it the token comes from the environment:**

| | Device-code flow (Entra) | Environment token |
|---|---|---|
| Chosen by | `VIBEMETER_AGENT_CLIENT_ID` set | `VIBEMETER_AGENT_CLIENT_ID` unset or empty |
| Required variables | `VIBEMETER_AGENT_CLIENT_ID` + `VIBEMETER_AGENT_TENANT_ID` + `VIBEMETER_AGENT_SCOPE` — all three; any missing ones are reported together at startup | `VIBEMETER_AGENT_TOKEN` |
| `VIBEMETER_AGENT_TOKEN` | **not required — never read** | required; re-read from the environment on every publish, never written to disk |
| Credential lives in | the MSAL token cache under the running user's profile (below) | nowhere — the variable is the only place it exists |

### `--login` — interactive sign-in, once per machine, per user

`VibeMeter.Agent --login` performs the interactive OAuth device-code flow: it
prints a verification URL and a user code (MSAL's own message, on stdout and
without the agent's log prefixes — it contains no token), you complete the
sign-in in a browser, and the agent caches the resulting credential and
exits. The acquired token itself is deliberately not echoed, stored or
returned beyond the cache. On success it prints:

```text
[info] Signed in. The credential is cached for this user on this machine; the service can now start.
```

`--login` needs only the three device-code variables — no API base URL and no
token. The cache is **per-user**, so sign in as *the account the service runs
as*, on the machine the service runs on: see §2.1 and §3.1. A credential
cached for the wrong user is the classic failure mode (§6.1).

### The daemon never prompts

The daemon runs the same flow with interactivity disabled: it can only renew
silently from the cached credential. When that is impossible — never signed
in on this machine, the cache belongs to another user, or the cached refresh
token is no longer accepted (a password change or a sign-in-frequency policy
does this) — it fails rather than prompting, and every publish attempt logs:

> No usable cached credential, and interactive sign-in is disabled for the
> daemon. Run the agent once with --login on this machine to sign in, then
> start the service again.

This is deliberate. A device code printed into a journal or log that nobody
is reading looks exactly like a hang on an unattended machine; sign-in is a
human, one-off act (`--login`), and the daemon only ever reuses its result.

### The token cache

MSAL writes the credential to a single file, `agent-token-cache.bin`, under
the running user's per-user application-data directory plus `VibeMeter`:

| Machine | Path (for the account running the agent) | Protection |
|---|---|---|
| Windows | `%APPDATA%\VibeMeter\agent-token-cache.bin` → e.g. `C:\Users\vibemeter-agent\AppData\Roaming\VibeMeter\agent-token-cache.bin` | MSAL's encrypted store |
| Linux | `$XDG_CONFIG_HOME` if set, else `$HOME/.config`, plus `VibeMeter\agent-token-cache.bin` — as installed (the unit pins `HOME=/opt/vibemeter`): `/opt/vibemeter/.config/VibeMeter/agent-token-cache.bin` | **none — an unprotected file** |
| macOS (not a deployment target) | MSAL keychain storage | keychain |

The unprotected Linux file is deliberate: MSAL's default Linux cache needs
libsecret and a running keyring, neither of which a headless server has, and
the failure would only surface obscurely at first refresh. **On Linux that
file IS a credential.** The service user normally creates it, so it is
already owned correctly — but check, and pin it:

```bash
sudo chown vibemeter:vibemeter /opt/vibemeter/.config/VibeMeter/agent-token-cache.bin
sudo chmod 600 /opt/vibemeter/.config/VibeMeter/agent-token-cache.bin
```

If it shows `root:root`, somebody ran `--login` as root — see §6.1.

### Status: implemented, unverified end to end

Both token providers, the `--login` mode, the cache plumbing and the daemon's
no-prompt behaviour exist in the source (`VibeMeter.Publishing/AccessToken/`
for the providers, `VibeMeter.Agent/AccessToken/` for the environment they are
configured from, `Program.cs`, `CliOptions.cs`; `--help` lists the variables). What has never
happened is an actual device-code sign-in against the identity provider: the
flow is **unverified end to end**, and the first `--login` on each machine is
its real test. Note also that the client, tenant and scope identifiers are
deliberately not defaulted anywhere in the source — this document uses
placeholders only, and no real ids belong in the repository or these files.

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

Either is fine. If the Linux box will not get runtime updates while you are away, ship
the self-contained build so a missing or upgraded runtime cannot break it.

The FDD and SC builds both produce a launcher executable — `VibeMeter.Agent.exe`
on Windows, `VibeMeter.Agent` on Linux — next to `VibeMeter.Agent.dll`.
(Launching the `.dll` directly with `dotnet VibeMeter.Agent.dll` works too.)

## 2. Linux — systemd

### Choose the unit first: personal machine or shared host

There are two units in `deploy/`, and picking the wrong one wastes an install.

| | `vibemeter-agent-user.service` | `vibemeter-agent.service` |
|---|---|---|
| Runs as | you, via `systemd --user` | a dedicated `vibemeter` service account |
| Installs to | `~/vibemeter`, env at `~/.config/vibemeter/agent.env` | `/opt/vibemeter`, env at `/etc/vibemeter/agent.env` |
| Enable with | `systemctl --user enable --now` + `loginctl enable-linger $USER` | `sudo systemctl enable --now` |
| Sees your provider credentials | **yes**, directly | no — they must be copied in |
| Use it for | a personal workstation or dev box | a shared or unattended host |

**On a personal machine, use the user unit.** The providers read credentials
the AI tools wrote into the signed-in user's own home — `~/.claude`,
`~/.codex`, `~/.zcode`. A service account has its own empty home, so every
provider reports `not-configured` while the service looks perfectly healthy.
That failure is silent, which is what makes it worth choosing deliberately.

The rest of §2 documents the **service-account** shape. For the user unit the
sequence is the same minus the `useradd` and the `sudo`: publish into
`~/vibemeter`, copy `deploy/vibemeter-agent.env.example` to
`~/.config/vibemeter/agent.env` (mode 600), copy
`deploy/vibemeter-agent-user.service` to
`~/.config/systemd/user/vibemeter-agent.service`, run `--login` as yourself,
then `systemctl --user daemon-reload && systemctl --user enable --now
vibemeter-agent` and `loginctl enable-linger $USER` so it survives logout.

### Service-account install (shared host)

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
sudoedit /etc/vibemeter/agent.env        # fill in the variables for your chosen authentication mode

# 4. Device-code mode only: sign in ONCE, as the service user, before
#    starting the service. See §2.1 — and why it must run as vibemeter.
sudo bash -c '
  set -a; . /etc/vibemeter/agent.env; set +a
  runuser -u vibemeter -- env -u XDG_CONFIG_HOME HOME=/opt/vibemeter /opt/vibemeter/VibeMeter.Agent --login
'

# 5. Unit file.
sudo cp deploy/vibemeter-agent.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now vibemeter-agent

# 6. Confirm.
systemctl status vibemeter-agent
```

### 2.1 Signing in on Linux (`--login`)

Run `--login` **as the `vibemeter` user**, with the same environment the
service will see. A credential cached into root's or your own account's
profile is invisible to the service, which then fails with the daemon's
"no usable cached credential" error (§6.1). Step 4 of the install sequence
sources `/etc/vibemeter/agent.env` (only root can read it), drops to the
service user with `runuser`, and pins `HOME=/opt/vibemeter` to match the
unit — so the cache lands exactly where the daemon will look for it:
`/opt/vibemeter/.config/VibeMeter/agent-token-cache.bin`.

The verification URL and user code appear on the terminal; complete the
sign-in in a browser on any device. Afterwards, check the cache file and its
ownership as shown in [Authentication](#the-token-cache) above. If the
service is already running, `sudo systemctl stop vibemeter-agent` before
signing in and `sudo systemctl start vibemeter-agent` afterwards.

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

### Provider credentials under the service account

Providers look for their local credentials under the **running user's profile**
(e.g. Codex `~/.codex/auth.json`, Claude `~/.claude` — honouring
`CLAUDE_CONFIG_DIR` — and Z.ai keys from environment variables or
`~/.zcode/config.json`). Under the unit, that profile is `/opt/vibemeter`. Copy
or symlink the credential directories you want the Linux box to report on into
`/opt/vibemeter`, and add any provider key environment variables to
`/etc/vibemeter/agent.env`. A provider with no credentials reports
`not-configured` — see the warning in [§7](#7-a-warning-about-not-configured)
before deciding that is acceptable.

## 3. Windows — Scheduled Task

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
icacls "C:\ProgramData\VibeMeter\Agent" /grant "<MACHINE>\vibemeter-agent:(OI)(CI)M"

# 3. Edit deploy\vibemeter-agent-task.xml: set UserId to the task account and
#    fix the install path if it differs. Then import — it prompts for the
#    account's password, which never appears on the command line.
schtasks /Create /F /TN "VibeMeter Agent" /XML deploy\vibemeter-agent-task.xml

# 4. Device-code mode only: sign in ONCE, as the task account, once the
#    device-code variables are in place — see §3.1 below.

# 5. Confirm.
schtasks /Query /TN "VibeMeter Agent" /V /FO LIST
```

A minimal one-liner also works if you accept the default settings:

```powershell
schtasks /Create /F /TN "VibeMeter Agent" /SC ONSTART /TR "\"C:\Program Files\VibeMeter\Agent\VibeMeter.Agent.exe\"" /RU <MACHINE>\vibemeter-agent /RP <password>
```

… but it carries the 72-hour execution limit, no restart-on-failure, and no
log capture. Prefer the XML.

### Task account, and how the task sees environment variables

Use a **dedicated local account** (e.g. `<MACHINE>\vibemeter-agent`) rather than
`SYSTEM`: providers read credentials from the running user's profile
(`%USERPROFILE%\.codex`, `%USERPROFILE%\.claude`, `%USERPROFILE%\.zcode`), and
under `SYSTEM` all four providers report `not-configured` — see
[§7](#7-a-warning-about-not-configured).

Environment variables reach the task through normal Windows environment
scopes, read once when the task's process starts:

- **Machine scope (simplest)** — from an elevated prompt:
  `setx /M VIBEMETER_API_BASE_URL "https://api.example.com"`, plus — for
  environment-token mode — `setx /M VIBEMETER_AGENT_TOKEN "<token>"`, or —
  for device-code mode — the `VIBEMETER_AGENT_CLIENT_ID`,
  `VIBEMETER_AGENT_TENANT_ID` and `VIBEMETER_AGENT_SCOPE` trio (these three
  are not secrets). Visible to every account, including the task account.
- **User scope** — log on once as the task account and run the same `setx`
  commands without `/M`. They land in that account's profile and are applied
  when the task logs the account on.

Two `setx` caveats: values longer than 1024 characters are truncated, and a
change is only picked up by processes **started afterwards** — stop and restart
the task (`schtasks /End` then `schtasks /Run /TN "VibeMeter Agent"`) to
re-read them.

### 3.1 Signing in on Windows (`--login`)

Run `--login` **as the task account**, not as yourself — the cache is
per-user, and a credential cached into your own `%APPDATA%` is invisible to
the task. From an interactive console:

```powershell
runas /user:<MACHINE>\vibemeter-agent "C:\Program Files\VibeMeter\Agent\VibeMeter.Agent.exe --login"
```

`runas` prompts for the task account's password and opens a new console
window as that account, showing the verification URL and user code; complete
the sign-in in a browser. Machine-scope variables are visible to that
session; user-scope variables apply because `runas` loads the account's
profile. The cache lands in the task account's
`%APPDATA%\VibeMeter\agent-token-cache.bin` — exactly where the task will
look for it. Do **not** run `--login` through the Scheduled Task itself: the
task's console output is discarded, so the code you must type would never be
seen.

## 4. Environment variable reference

Every variable the agent reads, from `AgentConfig.cs` (the token constant
also appears in `VibeMeter.Publishing\AccessToken\EnvironmentAccessTokenProvider.cs`;
the device-code trio in `VibeMeter.Agent\AccessToken\DeviceCodeAuthEnvironment.cs`). `--help` prints
the same list. Variables are validated at startup; each validator aggregates
its own problems into **one** message on stderr — `The VibeMeter agent is not
configured correctly:` (base URL, token, interval, staleness) or `Device-code
authentication is not configured:` (client id, tenant id, scope) — with a
bullet per problem, and the process exits **1**.

| Variable | Required | Default | If missing or wrong |
|---|---|---|---|
| `VIBEMETER_API_BASE_URL` | yes | — | Startup abort, exit 1. Must be an **absolute http(s) URL**; anything else (relative, `ftp:`, garbage) is rejected with the offending value echoed back. A trailing `/` is tolerated (trimmed). Not needed by `--dry-run` or `--login`. |
| `VIBEMETER_AGENT_TOKEN` | only when `VIBEMETER_AGENT_CLIENT_ID` is unset | — | When required, **empty at startup**: abort, exit 1 (`VIBEMETER_AGENT_TOKEN is required - set it to the agent's bearer token before starting.`). Presence-only check at startup — a *wrong* value surfaces later per publish as an auth failure (see §6.1); the process stays up and queues snapshots. In device-code mode it is never read. |
| `VIBEMETER_AGENT_INTERVAL_SECONDS` | no | `300` | Non-integer, or less than `10`, aborts at startup, exit 1. |
| `VIBEMETER_AGENT_STALENESS_MINUTES` | no | `20` | Non-integer, or less than `1`, aborts at startup, exit 1. |
| `VIBEMETER_AGENT_CLIENT_ID` | no — its presence selects device-code authentication | — | If set (non-empty), `VIBEMETER_AGENT_TENANT_ID` and `VIBEMETER_AGENT_SCOPE` become required and `VIBEMETER_AGENT_TOKEN` stops being so. |
| `VIBEMETER_AGENT_TENANT_ID` | with `VIBEMETER_AGENT_CLIENT_ID` | — | Startup abort, exit 1: `VIBEMETER_AGENT_TENANT_ID is required - set it to the Entra directory (tenant) id.` |
| `VIBEMETER_AGENT_SCOPE` | with `VIBEMETER_AGENT_CLIENT_ID` | — | Startup abort, exit 1: `VIBEMETER_AGENT_SCOPE is required - set it to the delegated scope to request, e.g. api://<api-app-id>/Usage.Write.` |

The queue directory is **not** configurable: it is always
`<user-data>\VibeMeter\VibeMeter.Agent\offline-queue` (see §5).

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
2026-09-17 03:24:05 Z [info] Providers: claude, codex, zai, google. Offline queue: /opt/vibemeter/.config/VibeMeter/VibeMeter.Agent/offline-queue (capacity 500).
2026-09-17 03:29:07 Z [info] Published snapshot for 4 provider(s) in 812 ms.
```

That third line, repeating once per interval, is the **only** positive proof
that publishing works. Its absence for more than one interval while the process
is active means something is failing — the matching `[warn]`/`[error]` line in
the same window says which failure. A misconfigured agent instead prints the
aggregated `not configured correctly` message (stderr) and exits 1. The
second line prints the *actual* queue directory, which is the authoritative
answer if anything in this document disagrees with it.

### Where the offline queue lives

One UTF-8 JSON file per queued snapshot, named
`{utcTicks:D19}-{idempotencyKey}.json`, capacity 500, oldest dropped when full.
An entry the API has refused outright carries its tally of refusals in the name
instead — `{utcTicks:D19}-{idempotencyKey}.rejected{n}.json` — so the count is
as durable as the document itself and survives a restart. Entries hold only the
mapped snapshot (ids, states, percentages) — never credentials.

| OS | Path (for the account running the agent) |
|---|---|
| Linux (the Linux box, as installed) | `/opt/vibemeter/.config/VibeMeter/VibeMeter.Agent/offline-queue` (`$XDG_CONFIG_HOME` if set, else `$HOME/.config` — .NET maps `SpecialFolder.ApplicationData` there on Linux; the unit pins `HOME=/opt/vibemeter`) |
| Windows (the Windows workstation) | `%APPDATA%\VibeMeter\VibeMeter.Agent\offline-queue` → e.g. `C:\Users\vibemeter-agent\AppData\Roaming\VibeMeter\VibeMeter.Agent\offline-queue` |

The queue is normally empty (each publish deletes its entry). A **growing**
count — visible as `Snapshot queued for later upload (N pending)` lines with
increasing N — is the reliable "failing to publish" signal.

### "Not running" vs "running but failing to publish"

| Question | the Linux box | the Windows workstation |
|---|---|---|
| Is the process alive? | `systemctl is-active vibemeter-agent` | `Get-Process VibeMeter.Agent`; task shows State `Running` / LastTaskResult `0x41301` |
| When did it last publish? | `journalctl -u vibemeter-agent -o cat \| grep -F 'Published snapshot' \| tail -1` | `Select-String -Path "C:\ProgramData\VibeMeter\Agent\agent.log" -Pattern 'Published snapshot' \| Select-Object -Last 1` |
| What did it last say? | `journalctl -u vibemeter-agent -o cat -n 20` | `Get-Content "C:\ProgramData\VibeMeter\Agent\agent.log" -Tail 20` |
| Restart loop (config error)? | Same `Agent starting` line repeating every ~30 s with no `Published` lines | Same line repeating every ~1 min in `agent.log` |

"Running but failing" looks like: process alive, no `Published snapshot` line
for ≥ 1 interval, and instead `API unreachable (…)` (network/server/rate
limit), `Authentication failed (…)` (token/credential), or `API rejected the
snapshot (…)` (bad request / clock skew / an API rolled back behind this
client) every cycle, plus `Snapshot queued … (N pending)`.

## 6. Troubleshooting

### 6.1 Authentication

Which mode is in play is decided by `VIBEMETER_AGENT_CLIENT_ID`
(see [Authentication](#authentication)).

**Environment-token mode** (`VIBEMETER_AGENT_CLIENT_ID` unset):

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
  (the Linux box), or re-run `setx` + `schtasks /End` and `/Run` (the Windows workstation).

**Device-code mode** (`VIBEMETER_AGENT_CLIENT_ID` set):

- **Not signed in, cache for the wrong user, or cached credential no longer
  accepted** — three causes, one signature. A password change or a
  sign-in-frequency policy invalidates the cached refresh token just like a
  missing cache does; silent renewal fails, the daemon refuses to prompt, and
  every publish logs:

  ```text
  [error] Authentication failed (No usable cached credential, and interactive sign-in is disabled for the daemon. Run the agent once with --login on this machine to sign in, then start the service again.). Snapshot queued; it will flush once VIBEMETER_AGENT_TOKEN is accepted.
  ```

  (The trailing variable name is literal in that message even in device-code
  mode.) The snapshot is queued each cycle, so nothing is lost, and the
  process stays up — this is the "running but failing to publish" signature
  of §5, not a restart loop.
- **Telling the three causes apart** (the Linux box):
  `sudo ls -l /opt/vibemeter/.config/VibeMeter/agent-token-cache.bin`.
  *File missing* → never signed in as the service user. *File present but
  owned by someone else* (typically `root:root`) → `--login` was run as the
  wrong account; run it as `vibemeter` (§2.1). *File present, correct owner,
  still failing* → the cached credential was invalidated by a password change
  or a sign-in-frequency policy: run `--login` again as the service user and
  restart the service.
- **Missing or incomplete device-code configuration** → with
  `VIBEMETER_AGENT_CLIENT_ID` set but the tenant id or scope absent, startup
  aborts with exit 1 and one bullet per missing variable:

  ```text
  Device-code authentication is not configured:
    - VIBEMETER_AGENT_TENANT_ID is required - set it to the Entra directory (tenant) id.
  ```

  Under systemd this is a restart loop per §4. All three variables are
  reported together when several are missing.
- **Signed in as the wrong user** deserves its own warning: `--login` run as
  root (the Linux box) or as your own account (the Windows workstation) writes the cache into *that*
  account's profile, and the daemon — running as the service account — sees
  none of it. It is the most likely reason a sign-in that worked at a desk
  fails once deployed: always run `--login` as the account the service runs
  as (§2.1, §3.1).
- **Renewal** is automatic while the cached refresh token is accepted; there
  is nothing to rotate by hand. Re-run `--login` only when it stops being
  accepted (§6.1 above).
- **`--login` itself fails** → it prints `Sign-in failed: <message>` (or
  `Sign-in cancelled; nothing was cached.` if interrupted) and exits 1. The
  device-code flow is unverified end to end (see
  [Authentication](#authentication)) — if it fails, check the three
  configured values first (client id, tenant id, scope), then the
  registration in the Entra portal.

### 6.2 API unreachable

Network, DNS, TLS, 5xx and 429 responses are transient: the publisher retries
in place (3 attempts, 30 s HTTP timeout each, ~2 s/4 s jittered backoff), then
the snapshot is queued and every later cycle flushes oldest-first. Recovery is
visible as `Flushed N queued snapshot(s)`. A 429 additionally honours the
server's `Retry-After` header (delta-seconds or HTTP-date) as a *floor* on the
wait, clamped to 30 s so one header cannot stall the agent or its shutdown;
past that the attempts are spent and the snapshot is queued as usual.

A snapshot the API refuses outright (a non-auth 4xx) is **also kept**: the
agent cannot tell "this payload is wrong" from "this API is behind the client"
— a rolled-back API validating a newer payload against an older schema refuses
it in exactly the same way — so the entry is re-offered once per cycle for up
to 24 refusals (about two hours at the default interval). While that is
happening you will see `N queued snapshot(s) were refused again and kept for a
later attempt`. Only when the budget is spent is the entry dropped, with
`Discarded a queued snapshot the API has now refused 24 times (HTTP 400 …)`;
that is the one line that means data was deliberately lost, and it is expected
for a snapshot older than the server's accepted observation window, which never
does become valid.
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
2026-09-17 08:14:07 Z [error] API rejected the snapshot (HTTP 400 …); queueing it in case the API, not the payload, is what is wrong - it will be re-offered up to 24 times before being discarded.
```

That snapshot *is* queued (the agent cannot tell a bad clock from a rolled-back
API), so a burst of refused entries accumulates and ages out of the queue on
its own once the clock is fixed; the line repeats every cycle until then. Note
the asymmetry: a future-skewed
timestamp is treated as *fresh* by the staleness gate (negative age), so skew
never causes omission — it surfaces only as the 400 above. Fix:
`w32tm /resync` (the Windows workstation, elevated) or `timedatectl set-ntp true` (the Linux box), then
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
| `vibemeter-agent.service` | the Linux box | systemd unit (non-root, `Restart=always`, journald) |
| `vibemeter-agent.env.example` | the Linux box | template for `/etc/vibemeter/agent.env` (mode 600) |
| `vibemeter-agent-task.xml` | the Windows workstation | Scheduled Task definition (boot trigger, no time limit, log capture) |
