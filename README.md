# ZyxelRoot — .NET 10 client for the Zyxel FWA505

A .NET 10 class library (plus console samples) that drives root access on a
Zyxel FWA505 and exposes the modem daemon logs as a live, parsed event
stream.

## What it supplies

* **Root shell** — a root shell over the router's `telnetd`
  (unauthenticated, LAN-only), with a marker protocol that keeps command
  output exact despite the pty echo.
* **Persistent root shell** — the same shell, kept alive across daemon
  restarts and router reboots, with a `RouterRebooted` event.
* **Daemon bootstrap** (`RootDaemonManager`) — starts the root `telnetd`
  daemon. Primary path: the configured shell-escape exploit plus the
  vendor's `debug` feature to gain a root login. Fallback: a configured
  web-UI exploit.
* **Live AT/modem log monitor** — tails the modem daemon logs over the root
  shell and emits parsed records.
* **On-demand raw AT** — one raw AT round trip, plus a parsed
  signal/registration/IP snapshot.
* **SMS** — send (single part, or long messages as multipart) and read the
  stored inbox.
* **Modem power & reset** — radio state read, soft reset, full power off/on.
* **Kick-detection monitor** — live A/B capture for same-IMSI detach
  detection.
* **Front-panel LEDs** — status / set / all on / all off / restore.
* **Data state monitor** — data up/down `StateChanged` events.

```
ZyxelRoot/
  ZyxelRoot.slnx
  src/ZyxelRoot/                 class library (net10.0)
    RootShell.cs                 root telnetd client + marker protocol
    PersistentRootShell.cs       keep-alive connection + reboot detection event
    RouterRebootEventArgs.cs     event args + the pure reboot rule
    RootDaemonManager.cs         starts the daemon: debug-SSH bootstrap, web fallback
    DebugBootstrap.cs            `debug`-account arm (DAAS seed) + root SSH login
    SshShell.cs                  shared SSH pty transport (marker protocol)
    SshAdminShell.cs             admin SSH shell + ZySH breakout
    WebDalClient.cs              ZCFG web login + configured DAL root injection
    MarkerShell.cs               marker protocol, rolling buffer, line stream
    DataStateMonitor.cs          data up/down poll + StateChanged event
    Sms/                         ZyxelSms (PDU send + inbox read) + GSM7 codec
    At/                          AT log monitor + on-demand AT client
  samples/ZyxelRoot.Smoke/       console smoke test
  samples/ZyxelRoot.Kick/        interactive kick A/B capture runner
  samples/ZyxelRoot.CronGuard/   keeps a watchdog cron line registered across
                                   reboots (example job; adjust to your own)
  samples/ZyxelRoot.Sample.Full/ combined long-running monitor: CronGuard +
                                   KickMonitor event -> 5-min APN back-off
                                   (cmctl OnHold/GoOn) + data up/down events
                                    + one-shot status-SMS send / inbox read
  README.md
```

---

## 1. Requirements

* **.NET SDK 10** targeting `net10.0`.
  Verified on this machine with `10.0.100-preview.4` and `11.0.100-rc.3`
  (the 11 SDK builds `net10.0` fine).
* Network access to the router (`192.168.1.1`) on SSH (22) and the root
  telnetd port (4444).
* NuGet package **SSH.NET 2026.0.0**.
  It negotiates this device's old dropbear: server offers only `ssh-rsa` host
  key, `aes128-ctr`/`aes256-ctr`, `hmac-sha1`/`hmac-sha2-256` — all accepted.
  No 3DES fallback is needed on this firmware.

## 2. Build and run

```powershell
cd ZyxelRoot
dotnet build

# connect (starts the daemon if needed), run id/uname, capture 30 s of AT events
dotnet run --project samples/ZyxelRoot.Smoke -- --seconds 30
```

Smoke-test options:

| Option | Meaning |
|---|---|
| `--seconds N` | length of the AT-monitor capture (default 30) |
| `--root-port N` | root telnetd port (default 4444) |
| `--ssh-port N` | SSH port (default 22) |
| `--host`, `--user`, `--password` | overrides |
| `--config PATH` | appsettings.json to read |
| `--trigger` / `--no-trigger` | run read-only `cmctl get moduleInfo` / `apnInfo 1` while capturing |
| `--verbose` | per-record output + exception stacks |
| `--watch N` | `PersistentRootShell` keep-alive: connect once, run `id`, then just wait N s — drops, re-bootstrap and (only on a real reboot) `RouterRebooted` are handled inside the library |
| `--selftest-reboot` | run the pure reboot rule on fixed cases and exit (no router needed) |

## 3. Configuration

Precedence: explicit values > `ZYXEL_*` environment > the `Zyxel` section of
an appsettings.json.

```jsonc
{
  "Zyxel": {
    "Host": "192.168.1.1",
    "Port": 22,                 // SSH port
    "Username": "admin",
    "Password": "...",
    "Exploit": "<breakout payload>",              // required, no built-in default
    "ExploitConfirm": "<pty confirmation text>",  // required, no built-in default
    "DalOid": "<DAL object id>",                  // root-injection target
    "DalCommandField": "<command field>",         // carries the injected command
    "DalCommandWrap": "<wrapping, {0}=command>",  // applied to the command
    "DalPayload": { "...": "…" }                  // base payload template (JSON object)
  }
}
```

Default config file: `appsettings.json` in the current directory
(override with `ZYXEL_CONFIG` or the `--config` argument). Environment
variables: `ZYXEL_HOST`, `ZYXEL_PORT`, `ZYXEL_USER`, `ZYXEL_PASS`,
`ZYXEL_EXPLOIT`, `ZYXEL_ROOT_PORT`, `ZYXEL_CONFIG`.

The breakout and injection values are **secret by design and are loaded only
from the config file** — none of `Exploit`, `ExploitConfirm`, `DalOid`,
`DalCommandField`, `DalCommandWrap`, `DalPayload` has a built-in default, and
the source deliberately contains none of them, so the privilege-escalation
mechanics are not reconstructable from the code.

```csharp
var settings = ZyxelSettings.Load();   // config file + ZYXEL_* environment
// or fully explicit (must also set Exploit, ExploitConfirm, DalOid,
// DalCommandField and DalCommandWrap — none has a built-in default):
var settings = new ZyxelSettings { Host = "192.168.1.1", Password = "..." /*, ... */ };
```

## 4. API

```csharp
using ZyxelRoot;

// 1) root shell — connect, or start the daemon first
RootShell shell;
if (!await RootShell.ProbeAsync(settings.Host, settings.RootPort))
{
    var report = await new RootDaemonManager(settings, Console.Error.WriteLine).StartAsync();
    if (!report.Succeeded) throw new Exception(report.Message);
    // report.Path: "debug" (SSH bootstrap, primary) or "web" (DAL injection, fallback)
}
shell = await RootShell.ConnectAsync(settings.Host, settings.RootPort);

CommandResult r = await shell.RunAsync("id");
Console.WriteLine($"{r.ExitCode}: {r.Output}");   // 0: uid=0(root) gid=0(root)

// 1b) persistent root shell — connect once, survive daemon restarts + reboots
var persistent = new PersistentRootShell(settings);
persistent.RouterRebooted += (_, e) =>
{
    // fired exactly once per drop→reconnect that proves a reboot:
    // re-register everything the reboot wiped (cron jobs, …)
    _ = persistent.RunAsync("crontab /customer/xdata/mycron");
    Console.WriteLine($"router rebooted: up {e.ObservedUptimeSeconds:F0}s, was down {e.DisconnectedFor.TotalSeconds:F0}s");
};
await persistent.ConnectAsync();
await persistent.RunAsync("id");     // proxies the live shell; waits out reconnects

// 2) live AT/modem log stream
await using var monitor = new AtMonitor(shell);
await monitor.StartAsync();
await foreach (AtLogRecord record in monitor.ReadAllAsync(ct))
    if (record.IsAtTraffic)
        Console.WriteLine($"{record.Direction} {record.AtCommand}");
```

Other entry points:

* `ZyxelSms.SendAsync("1266", "…")` / `.GetStoredAsync()` — send (PDU/UCS-2)
  and read the stored inbox (see §5.5). Works over any `ICommandRunner`
  (incl. `PersistentRootShell`).
* `DataStateMonitor` — polls `apnConnStatus` (default 2 s) and fires
  `StateChanged` on every data up/down transition, any cause (kick, hold,
  signal, reboot); `ReadStateAsync()` for a one-shot read (see §5.10).
* `ModemPower.RadioStateAsync()` / `RadioCycleAsync()` / `PowerOffAsync()` /
  `PowerOnAsync()` — module state read, soft reset (CFUN cycle), full power
  off/on (see §5.6).
* `KickMonitor.StartAsync()` / `ReadAsync()` / `Mark()` / `StopAsync()` — live
  A/B capture for same-IMSI "kick" detection (see §5.7).
* `ZyxelLed.StatusAsync()` / `SetLedAsync(name, action)` / `AllOnAsync()` /
  `AllOffAsync()` / `TurnOffAsync(stopWriters)` / `RestoreAsync()` — front
  panel control through the vendor's `zyledctl` (see §5.8).
* `AtCommandClient.SendAtCommandAsync("AT+CSQ")` — one raw AT round trip
  through the vendor's `mipc_wan_cli` (30 s timeout; a timeout means the
  module hung — see §5.4).
* `AtCommandClient.PollStatusAsync()` — parsed CSQ/CREG/CGREG/CGPADDR snapshot
  (signal, registration, PDN IP).
* `RootShell.Lines` — bounded `ChannelReader<string>` of every pty line
  (echoes, prompts, command output). For a human-facing interactive console.
* `RootShell.WriteRawAsync` / `WriteLineAsync` — raw pty writes.
* `AtMonitor.PollOnceAsync()` — one poll round without the channel (tests/ETL).
* `AtMonitor.GetStatus()` — per-file health (`lines`, `next`, `records`, error).
* `WebDalClient.InjectAsync(cmd)` — a single root injection, needs no SSH
  (logs in, injects, logs out). Useful when you already have a script on the
  device.

## 5. How it works

### 5.1 Root shell and the marker protocol

The router runs `telnetd -F -p 4444 -l /bin/sh` as root (no authentication,
LAN-only). `RootShell` speaks minimal telnet over a raw TCP socket:
3-byte `IAC WILL/WONT/DO/DONT` and `IAC SB … IAC SE` are stripped by a
**streaming** filter (it keeps partial sequences across reads), everything else
is passed through.

The pty echoes input, wraps the echo at ~78 columns and converts every LF to
CRLF. Therefore **output is never parsed by eye** — every command is delimited:

```
echo ZYX''RBS<token>; <command>; echo ZYX''RES<token>=$?
```

The echo of that line contains `ZYX''RBS` (with quotes), which can never match
the bare marker `ZYXRBS<token>`. `RunAsync` returns the exact bytes between the
markers (`CommandResult.RawBytes`), a normalised `Output` string, and the exit
status. This is a small improvement over the original Python harness, which
delimited only the end; the mechanics (`''` trick, one line per command, no
echo parsing) are the same.

`RunAsync` rejects multi-line commands; multi-line scripts go through the SSH
heredoc writer in `SshAdminShell`.

### 5.2 Starting the root daemon (`RootDaemonManager`)

`StartAsync` brings up the root `telnetd` daemon on the configured port
(default 4444), trying two paths in order:

* **Primary — debug-SSH bootstrap** (no web session consumed): SSH login as
  `admin`, the configured shell-escape (`Exploit` / `ExploitConfirm`) to get
  past the restricted CLI, then the vendor's `debug` feature — a uid-0
  `debug` account the firmware ships, armed by the firmware's setuid helpers —
  for a one-shot root SSH login. The daemon and its launch scripts are written
  over that root channel and started there.
* **Fallback — web-UI injection**: the same daemon, launched by a single
  configured web-UI injection that runs as root. The injection reply reports
  an internal error (the operation it pretends to perform fails) — the
  injected line still runs; success is decided by the TCP probe, never by the
  reply.

`RootDaemonReport.Path` says which one ran (`"debug"` / `"web"`).

The daemon lives in `/tmp` (tmpfs): a reboot wipes it and the next
`StartAsync` rebuilds it. Call `StartAsync` only when the port is not
accepting — a still-listening older daemon keeps answering and the new one
cannot replace it.

### 5.3 Web DAL client

* `GET /getRSAPublickKey` → RSA public key (2048-bit here); fetched live on
  every login.
* Payload key = base64 of a **client-chosen** 32-byte AES key (the protocol
  accepts any value — it is not a secret, so the code just uses a `0x42`
  filler), encrypted with RSA PKCS#1 v1.5 and sent to the router with the
  login request.
* Requests: JSON → AES-256-CBC (that key, a likewise client-chosen 16-byte
  `0x42`-filler IV, PKCS#7) → base64 in `{content, iv, key}`; the response IV
  comes from the router in each reply.
* `POST /UserLogin` (account + base64 password). Retries up to 10 times; the
  device's "Maxium number of login account has reached" reply waits 30 s,
  other failures back off 2 + 2·attempt seconds.
* `POST /cgi-bin/DAL?oid=<DalOid>&sessionkey=<sk>` — the command is wrapped
  per `DalCommandWrap` (`{0}` = the command) and placed into
  `DalCommandField`. The line is sanitized, so the command must be a single
  metacharacter-free word sequence (`sh /tmp/script.sh`). All three values
  come from the configuration.
* `POST /cgi-bin/UserLogout` in `finally` — the device caps concurrent logins.

Firmware quirk: `sessionkey` is a bare **JSON number**
(`{"sessionkey":324936641373915,...}`), not a string. The client accepts both
and fails fast if a success reply has no usable key (silently retrying would
leak sessions into the capped pool).

### 5.4 On-demand raw AT (`AtCommandClient`)

`SendAtCommandAsync("AT+CSQ")` runs `/usr/bin/mipc_wan_cli --at_cmd <cmd>`
through the root shell (marker protocol) and returns the exit code plus the
full printed reply. The AT string is passed byte-exact: single-quoted for the
device ash (only `'` is escaped), so vendor forms like
`AT+ECFGSET="sms_over_ip","1"` survive; it must be single-line printable
ASCII.

`PollStatusAsync()` runs `AT+CSQ`, `AT+CREG`, `AT+CGREG` and `AT+CGPADDR=1`
and returns an `AtStatusSnapshot` (rssi/ber, CREG/CGREG registration state,
PDN-1 IP, plus the raw outputs).

**A timeout means a hang.** A healthy module answers in milliseconds; the
vendor client only blocks this long (its per-command block is 300 s) when the
module has stopped answering. The 30 s timeout is therefore the freeze
detector, and a timed-out round trip triggers an automatic shell recovery
(Ctrl-C, then a drain through the pty) so the shell is not left stuck behind
the hung client. `PollStatusAsync` stops issuing commands after the first
timeout for the same reason.

**Request/response only — by architecture, not a gap.** Each call opens a
fresh MIPC session on the vendor client's own port (CMIPC6; it does not touch
cm's session) and there is **no persistent session and no unsolicited (URC)
stream** through it. The modem's raw URCs never reach the AP in raw form —
status *updates* keep arriving via the log tail (§6, `AtMonitor`), which is
how a "live" view works on this platform.

State-changing AT (`AT+CFUN=0`, …) works through this client and is the
caller's responsibility; the library's monitor itself stays read-only.

### 5.5 SMS (`ZyxelSms`)

Send SMS and read the device's stored inbox, over any `ICommandRunner`:

* `SendAsync(to, message)` — one SMS part.
* `SendLongAsync(to, message)` — a long message as a concatenated multipart
  (the phone reassembles it).
* `GetStoredAsync(maxMessages, includeSent)` — the stored inbox (received
  and/or sent), newest first.

```csharp
var sms = new ZyxelSms(shell);

SmsSendResult sent = await sms.SendAsync("1266", "NL2000 AAN");
if (sent.Success)
    Console.WriteLine($"sent, modem: {sent.ModemReport}");

foreach (SmsMessage m in await sms.GetStoredAsync(10, includeSent: false))
    Console.WriteLine($"{m.TimeStamp}  {m.Sender}: {m.Body}");
```

Notes for this modem: the send is always PDU mode (the text path is rejected
with `+CME ERROR: 100`); per-part user data is capped at 14 octets, so
`SendLongAsync` splits into several small parts; success is judged by the
modem's own `+CMGS:` confirmation, not by the dialer's result code (its
`ResultCode` is a known false negative on this firmware).

### 5.6 Modem power & reset (`ModemPower`)

All operations run one `/usr/bin/mipc_wan_cli` process through the root shell
(marker protocol); all are **untested** (a reset drops the user's
production connection — the user runs them).

* `RadioStateAsync()` — read-only. `--nw_radio_state_get` (MIPC msg 1025)
  prints `Get SW radio state:MIPC_NW_RADIO_STATE_ON|OFF|UNKNOWN`, which the
  result parses into `RadioState`.
* `RadioCycleAsync()` — the **soft reset**, the vendor's own recovery
  sequence from `/etc/auto_recovery.sh`: `--at_cmd AT+CFUN=0` → 2 s pause →
  `--at_cmd AT+CFUN=1` → `ubus send modem_ee` (failure tolerated). The module
  stays powered; cm's FSM re-camps and re-dials (tens of seconds of no WAN).
  This is the reset to use when the module still answers.
* `PowerOffAsync()` — `--modem_poweroff` (MIPC msg 1027, the client's
  decompiled send: TLV 256 = 0x00, TLV 257 = 0x80) powers the module fully
  down; success = the client prints `Power off MD done`.
* `PowerOnAsync()` — `--nw_radio_state_set 1` (MIPC 1027, TLV 256 = 1), then
  a verification state read. **Best-effort by architecture:** the request
  only reaches a *live* module (the MIPC peer is the module firmware behind
  the CCCI line). A fully powered-down module cannot answer it, so the call
  times out and reports `TimedOut`. The host-side power-on that works on a
  dead module — `libtrm_power_on_md` (kernel ioctl `0x437C` on a ccmni fd,
  wrapped by `mipc_power_on` in `libmipc_msg`) — is exposed by no on-device
  CLI, and the vendor's own wrapper is unusable on a dead module too:
  `mipc_power_on` needs an established session, and `mipc_init` polls
  `/sys/kernel/ccci/boot` in an endless loop until the module reports
  `md1:4`. So recovery from a full power-off on this firmware = **router
  reboot** (or a small cross-compiled ioctl helper).

Smoke: `--modem state|cycle|off|on` runs exactly that operation and exits
(never implicit).

### 5.7 Kick-detection monitor (`KickMonitor`)

Live A/B capture for detecting when the module gets **detached by the network
because a second device with the same IMSI attached** (the user's phone
re-attaching). The complete stream between `StartAsync()` and `StopAsync()` is
the capture; score it against the records and their timing.

`KickMonitor(shell)` takes any `ICommandRunner` — a live `RootShell` **or a
`PersistentRootShell`**, which keeps the monitor working across daemon
restarts and router reboots without a rebuild (the command proxy
transparently re-targets the live connection). One background loop over the
one command channel (serialized by its lock):

* **Tap** — the passive `/tmp/mipc_tap` daemon (push the binary to the
  device first) on its default port (netagent → CMIPC3, no production
  holder) with `--ind 0x4001:0x10` (raw-URC mirror, the "jackpot" axis) plus
  its built-in 0x4502 SIM IND. Lines from `/tmp/mipc_tap.log` → `Tap` records.
  Startup: refuses a second tap instance, verifies the binary (md5 mismatch →
  Notice), truncates the log, starts the daemon and **throws unless both
  `register_ind` lines come back `rc=0`** — a failed registration means a
  blind capture.
* **AT poll** — every 5 s `AT+CSQ`, `AT+CREG`, `AT+CGREG` through
  `AtCommandClient`; one `AtPoll` record each, parsed to a one-line summary
  (timeouts and `CME 100` included — CREG/CGREG read CME 100 on this modem,
  which is itself a data point).
* **Log tails** — `mmm.log` / `cm.log` / `wan.log` with the kick-signature
  patterns (CampOnStatus, ps_state, the cm recovery ladder) → `Log` records.
* **`Mark("note")`** — never blocks; call it the instant the phone leaves
  airplane mode. `Mark` records sit in the stream so the capture can be
  scored around them.

Record types: `KickRecordKind.Tap | AtPoll | Log | Mark | Notice | ModemHang`
(the last: an AT poll timed out — the module/AT path is hung; the rest of the
AT round is skipped). Lifecycle
notes: tap started, registrations, tap exit, repeated poll failures). The
channel is unbounded — a stalled consumer grows memory, nothing is dropped.

`StopAsync()` SIGTERMs the tap (SIGKILL fallback), removes the tap log (the
capture is in the stream) and completes the channel; the device is left clean
(it only touches a tap *this instance started* — with the tap disabled a
pre-existing one is left alone). `KickMonitor.StopTapOnDeviceAsync(shell)` is
the standalone cleanup for a tap left over from a crashed run.

**`KickMonitorOptions.TapEnabled`** (default `true`) switches the tap
on/off. `false` runs log tails + AT poll only — for long-lived monitors where
the tap binary (a `/tmp` push, wiped by reboots) is absent or not wanted; the
kick signature is in the vendor logs either way. `true` keeps the strict
start-up (missing binary / failed registration → throw).

Smoke: `--kick [seconds]` (default 25) in `ZyxelRoot.Smoke` runs a capture
with one `Mark("smoke-test")` mid-run and verifies tap registration, ≥1 AT
round, tail availability, mark delivery and a clean device after stop. On a
quiet box with no flip the tap emits only its lifecycle lines — the smoke
prints `no URC/IND traffic in this window`, which is a *finding to report*,
not a failure.

**Interactive runner: the `ZyxelRoot.Kick` sample** (next to the smoke) is
the phone A/B tool:

```
dotnet ZyxelRoot\samples\ZyxelRoot.Kick\bin\Release\net10.0\ZyxelRoot.Kick.dll
```

It connects (bootstrapping if needed), writes a baseline (register state +
CSQ) into the capture file, starts the monitor, streams every record live to
the console, and **you press ENTER the instant the phone leaves airplane
mode** (→ `Mark`), then ENTER again (or Ctrl+C) to stop. The full capture —
records plus raw details (raw AT output, raw log lines) — is written to
`captures\kick-<timestamp>.log` in the current directory (`--out FILE` to
override, `--interval N` for the poll cadence). The ending summary counts the
records per kind and the tap data lines. Note: the register baseline prints
the client's enum form (`MIPC_NW_REGISTER_STATE_HOME`), not a `ps_state=1`
number — that's how `--show_register_status` renders on this firmware.

**Running the phone A/B with it.**

1. Baseline sanity: module attached, `AT+CSQ` answers, no other `mipc_tap`
   running (or push the tap binary to `/tmp/mipc_tap` first, mode 0755).
2. `var kick = new KickMonitor(shell);` — start a consumer on
   `kick.ReadAsync()` and **append every record to a file** (that file is
   the capture).
3. `await kick.StartAsync();`
4. Take the phone (same subscription/IMSI) out of airplane mode and call
   `kick.Mark("flip")` **the instant it does** — the record sits in the
   stream at the right place.
5. Leave the box alone (no other MIPC/AT tooling — cm's ladder runs on its
   own), for ~15 min (covers the 240 s recovery ladder + margin).
6. `await kick.StopAsync();` — the device is left clean.
7. Score the captured stream: a `Tap` line with detach/registration payload
   near the mark is direct cause evidence; then check signal health before
   the camp-down and the camp-up timing vs. the radio-reopen ticks.

### 5.8 Front-panel LEDs (`ZyxelLed`)

All operations run `/bin/zyledctl` (or read sysfs) through the root shell.
The kernel kill-switch `/sys/kernel/zyinetled/mode` does not exist on the
running system, there is
no `service` applet and no `led_ctrld` init script — `zycmled`/`esmd` are
plain `&`-children of `zcmd` with no respawn. `zyledctl` is the only control
surface (its usage lists the logical names; the live sysfs has 14 nodes,
some without a logical name and some named in the tool but absent in sysfs,
which prints harmless `trigger/open` noise while still exiting 0).

* `StatusAsync()` — read-only snapshot: every `/sys/class/leds/*` node
  (trigger + brightness; the panel is brightness-driven, triggers sit at
  `none`) plus the pids of `zycmled`/`esmd`/`zybtnchk` and whether the
  kernel knob exists (it doesn't on this build).
* `SetLedAsync("INTERNET_G", LedAction.Off)` — one logical LED
  (`ZyxelLed.KnownNames`: POWER/SIG/INTERNET/SYS `_R/_B/_G`, WPS, WIFI,
  VOICE_G, SMS_G, FW_UPGRADE, RTD, all; actions on/off/slow/medium/fast/
  superfast/upgrade). A color name forces its two siblings off first.
* `AllOffAsync()` — `zyledctl RTD off`: every mapped node goes to 0
  (`lcd-backlight` and `led9515/9516/9517` are outside the tool's table and
  stay put).
* `AllOnAsync()` — `zyledctl all on`: the "return to normal" command; the
  writer daemons re-assert the correct per-state colors within ~10 s.
* `TurnOffAsync(stopWriters)` — `AllOffAsync` plus (when asked) killing
  `zycmled`/`esmd`. With the writers left running the off state holds as
  long as the underlying states don't change; `zcmd`
  (unkillable) re-lights INTERNET_G on a WAN reconnect and there is no
  kernel kill-switch to absorb it, so the kill is belt-and-braces — and it
  suspends WAN link-state tracking until `RestoreAsync`.
* `RestoreAsync()` — restarts dead `zycmled`/`esmd` (a killed instance
  stays dead until reboot), then `AllOnAsync`.

Smoke: `--led status|off|off-kill|on|restore` runs that operation and exits
(never implicit).

### 5.9 Persistent connection & reboot detection (`PersistentRootShell`)

For long-lived monitoring processes: connect once, keep calling `RunAsync` —
daemon restarts and router reboots are handled inside the library. When the
connection drops, the shell reconnects (re-bootstrapping the daemon when the
box is up but the daemon is gone) and `RunAsync` callers simply wait.

After every reconnect it compares the box's `/proc/uptime` against the time
elapsed since the first connect; when the uptime proves the box restarted in
between, the **`RouterRebooted`** event fires exactly once for that cycle —
plain hiccups and daemon kills never fire it. The handler runs with a live
shell, so it may call `RunAsync` (re-registering the cron jobs a reboot
wiped is the typical case).

Smoke: `--watch [seconds]` runs the keep-alive mode (kill the daemon any
time to see the re-bootstrap), `--selftest-reboot` checks the detection rule
offline.

### 5.10 The combined monitor (`ZyxelRoot.Sample.Full`)

The long-running production sample: **CronGuard + KickMonitor → APN back-off
+ data up/down events + one-shot SMS ops**, one process over one
`PersistentRootShell`:

```
dotnet run --project samples/ZyxelRoot.Sample.Full [--check] [--backoff 5m]
    [--config PATH] [--no-tap] [--selftest-kick] [--keep-hold]
    [--max-uptime 60m] [--send-status-sms] [--read-sms [N]]
```

1. **CronGuard** — ensures the sample watchdog cron line (the CronGuard
   sample's job; adjust to your own) and re-ensures it on every
   `RouterRebooted`.
2. **KickMonitor** — over the persistent shell (§5.7); the tap runs when
   `/tmp/mipc_tap` is present (md5-checked), log-tails-only otherwise
   (`--no-tap` forces it off). The monitor survives daemon kills *and*
   reboots with no rebuild.
3. **APN back-off** — the kick signature on `mmm.log` fires `cmctl send
   OnHold` (cm stops (re-)dialing, so the other device on the same account
   keeps the connection instead of losing it to the box's 3–4 s re-dial);
   after the back-off (default 5 min) `cmctl send GoOn` resumes dialing. A
   reboot during an active hold re-arms `OnHold`; on process exit an active
   hold is released by default so a dead monitor can never silence the box's
   WAN (`--keep-hold` opts out). At every start the sample first sends a
   blind `GoOn` and verifies `apnInfo` — a `GoOn` on a box that is *not*
   held is a no-op, so a stale hold from a crashed run is cleared without
   hold-state detection.
4. **Data up/down events** — a `DataStateMonitor` polls `apnConnStatus`
   every 2 s and fires `StateChanged` on every transition, whatever the
   cause (kick, hold, signal loss, reboot). A failed poll never changes the
   state, so a dropped shell can never look like a data disconnect.
5. **One-shot SMS ops** (run and exit, no monitor needed):
   `--send-status-sms` sends the status SMS `NL2000 AAN` to the KPN short
   number `1266` (exit 0 only on a fresh modem `+CMGS:` confirmation);
   `--read-sms [N]` prints the N (default 3) most recent **received**
   messages from the router inbox (sent records filtered out, newest first).

`--check` is the read-only crontab check (exit 0 present / 3 missing /
2 settings). `--selftest-kick` injects one synthetic kick right after start
(it exercises OnHold → wait → GoOn; harmless on an attached box — OnHold
does not drop the active session, it only stops re-dialing).

## 6. AT monitor

### 6.1 Mechanism

No raw AT tap is used. On this platform the runtime AT path is

```
cm → libmmm.so dispatcher → libvdr_mtk_module.so handler
   → MIPC message 3 (MIPC_SYS_AT_REQ, TLV 0xFFFF8100) → libmipc_msg.so → modem
```

there is no AT character device in that chain, and the mipc transport
(`mipc_init("wwan")`) is owned by `/usr/lib/libmipc_msg.so` (38 952 B; note the
path — the docs say `/lib`, the device has it in `/usr/lib`); the wire protocol
is 16 B header + TLV (per-client tty ports, 300 s default timeout) — a raw
MIPC tap/injection is
feasible from the root shell but not yet implemented here. The channel plugin's `SendAtCmdFull` is a dead stub, and the
`open-at-console` handshake is a *console* path, not the one the vendor plugin
uses. Opening that channel blindly would mean talking to the production
cellular manager with an unknown protocol, so it is deliberately **not**
implemented.

The monitor therefore tails the daemon logs — the only supported observability
surface — over the root shell:

```
wc -l < <file>; tail -n +<next> <file> | head -n <max>
```

* **Line offsets, not byte offsets.** The pty applies ONLCR (LF→CRLF), so byte
  counts drift; line numbers don't.
* Only fully terminated lines are emitted, and `next` advances only past them,
  so a line cut by `head` is re-read in full next round.
* Truncation/rotation: when `wc -l + 1 < next`, the source restarts at line 1.
* One poll round per second, 500 lines max per source per round.
* Records go into a bounded channel (10 000, drop-oldest); a stalled consumer
  loses the oldest events instead of growing memory.
* The monitor stops after 10 consecutive shell failures (its channel completes
  with an error) instead of spinning forever.

Default sources:

| File | Content |
|---|---|
| `/var/log/mmm.log` | every MMM op + every vendor-plugin line (primary) |
| `/var/log/wan.log` | `[WAN]` lines (SIM changes) — empty during testing |
| `/var/log/cm.log` | cm FSM events, incl. event 26 (SendSMS) / 27 (Atcmd) |

Parsed shape: standalone `[MM/dd HH:mm:ss]` lines (mmm/wan) apply to the
following message lines; cm.log puts the timestamp inline. Both are handled by
one stateful parser, which also splits `[MMM][INFO]fn(123) message`.

### 6.2 AT visibility limits

Observed on the device:

| What | Visible? | Where |
|---|---|---|
| Every MMM op invocation (52 handlers) | **yes** | `mmm.log` `[MMM][INFO]mtk*(…)` |
| Outgoing AT text announced by handlers | **only where logged** | `mtkGetAllSms`: `ATCMD:AT+CMGL=4` (every ~10 s); `mtkSetSmsSendMsg`: `Send SMS atcmd=…` |
| Raw AT replies | **no** | `+CMGL:` payload is written to `/xdata/SMS/sms_new.txt`; the log only shows parsed fields (`DEBUG: rilitem->…:value`) |
| Raw `Atcmd` operation (`cmctl send Atcmd …`) | **no** | `mtkDoAtCmd` has no log call (static analysis) |
| SMS send/receive | partial | send announces the command; inbox PDUs land in `/xdata/SMS/*.txt` |
| cm FSM events (incl. `event:26` SendSMS, `event:27` Atcmd) | **yes** | `cm.log` `Processing event, apnNum:…, event:26, type:…` |
| SIM status changes | **yes** (format untested) | `wan.log` `[WAN]` |
| The MIPC wire itself | **no** | private transport, no tap implemented |

Direction inference is therefore conservative: `Outgoing` only when a handler
announces a command (`ATCMD:` / `atcmd=`), `Incoming` only for verbatim reply
tokens (`+CMGL`, `OK`, `ERROR`, `+CME ERROR`, …). Everything else is `None`.
Raw replies simply do not appear at this log level, so `Incoming` is rare by
construction.

**The log level was not changed.** `/var/log/mmm.log` runs at level 3 (default),
which is already enough for the op lines above. `cmctl set fileDebugLevel 4`
would be an SHM write on a production router, and the raw-AT handler has no log
call at all, so it cannot reveal the missing traffic anyway.
