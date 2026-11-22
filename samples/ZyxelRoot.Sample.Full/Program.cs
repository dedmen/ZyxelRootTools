using System.Text.RegularExpressions;
using ZyxelRoot;
using ZyxelRoot.Sms;

// Sample.Full — the combined long-running monitor:
//
//   1. CronGuard   — keeps the sample watchdog cron line registered across
//                    reboots (same job as the ZyxelRoot.CronGuard sample).
//   2. KickMonitor — streams the kick-detection capture (mipc_tap + AT+CSQ poll
//                    + filtered vendor-log tails) over a PersistentRootShell,
//                    so it keeps working across daemon restarts and reboots.
//   3. APN back-off— when the kick signature fires (a second device on the
//                    same account purged the box's PDN),
//                    the sample sends `cmctl send OnHold` (cm stops dialing the
//                    APN, so the phone wins the account slot), waits the backoff
//                    (default 5 min), then `cmctl send GoOn` (cm re-dials).
//                    On start it sends GoOn, releasing a hold left behind by
//                    a previous crashed run (verified no-op when not held).
//
//   4. Data state — DataStateMonitor polls the APN's apnConnStatus (2 s) and
 //                    prints every data up/down transition (any cause: kick,
 //                    the back-off hold, signal loss, reboot). Console for
 //                    now; the C# event is what a later Home Assistant entity
 //                    binds to.
//
//   dotnet run --project samples/ZyxelRoot.Sample.Full [options]
//
//   --check              read-only crontab check, exit (0 = present)
//   --backoff <dur>      APN hold duration; bare number = seconds, s/m/h suffix
//                        (default: 5m)
//   --kick-threshold <n> teardown episodes within --kick-window required before
//                        holding (default: 3) — debounces the box's own GoOn
//                        reconnect and one-off hiccups
//   --kick-window <dur>  the window the --kick-threshold episodes must fall in
//                        (default: 120s)
//   --reconnect-grace <dur> ignore teardown lines this long after our own GoOn
//                        (the redial's reconnect prints the same lines; default: 15s)
//   --config PATH        appsettings.json with the Zyxel section
//   --no-tap             never start mipc_tap (log tails + AT poll only)
//   --selftest-kick      inject one synthetic kick right after start (exercises
//                        OnHold -> wait -> GoOn without a real second device)
//   --keep-hold          on exit, do NOT release an active hold (default: GoOn)
//   --max-uptime <dur>   exit cleanly after this long (smoke tests; same
//                        duration format as --backoff)
//   --send-status-sms    one-shot: send the status SMS 'NL2000 AAN' to 1266
//                        (exits after; 0 = modem confirmed)
//   --read-sms [N]       one-shot: print the N (default 3) most recent
//                        RECEIVED SMS from the router inbox (exits after)
//
// Exit codes (CronGuard convention): 0 ok / cron present, 1 fatal,
// 2 settings/usage, 3 cron missing (--check).

namespace ZyxelRoot.Sample.Full;

internal static class Program
{
    // The example watchdog line (adjust to your own job).
    private const string CronLine = "*/5 * * * * /xdata/ra_keep.sh";
    private const string CronMarker = "ra_keep.sh";
    private const string MmmLog = "/var/log/mmm.log";
    private const string CmLog = "/var/log/cm.log";

    // The live-verified kick signature: the account-level single-active
    // enforcement purges the PDN without a
    // camp-down; mmm.log carries both lines. The box would otherwise re-dial in
    // 3-4 s and win the account slot back from the phone.
    private static readonly Regex KickSignature = new(
        @"MIPC_DATA_GET_CALL_REQ FAIL|Reset APN\d*'s PDN info",
        RegexOptions.Compiled);

    private static async Task<int> Main(string[] args)
    {
        bool checkOnly = false;
        bool selftestKick = false;
        bool noTap = false;
        bool keepHoldOnExit = false;
        bool sendStatusSms = false;
        int? readSmsCount = null;
        string? configPath = null;
        TimeSpan backoff = TimeSpan.FromMinutes(5);
        TimeSpan maxUptime = Timeout.InfiniteTimeSpan;
        int kickThreshold = 3;
        TimeSpan kickWindow = TimeSpan.FromMinutes(1);
        TimeSpan reconnectGrace = TimeSpan.FromSeconds(15);

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--check": checkOnly = true; break;
                case "--selftest-kick": selftestKick = true; break;
                case "--no-tap": noTap = true; break;
                case "--keep-hold": keepHoldOnExit = true; break;
                case "--backoff" when i + 1 < args.Length:
                    if (!TryParseDuration(args[++i], out backoff))
                    {
                        Console.Error.WriteLine($"--backoff: cannot parse '{args[i]}' (e.g. 300, 300s, 5m, 1h)");
                        return 2;
                    }
                    break;
                case "--kick-threshold" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out kickThreshold) || kickThreshold < 1)
                    {
                        Console.Error.WriteLine($"--kick-threshold: need a positive integer, got '{args[i]}'");
                        return 2;
                    }
                    break;
                case "--kick-window" when i + 1 < args.Length:
                    if (!TryParseDuration(args[++i], out kickWindow))
                    {
                        Console.Error.WriteLine($"--kick-window: cannot parse '{args[i]}' (e.g. 60, 60s, 1m)");
                        return 2;
                    }
                    break;
                case "--reconnect-grace" when i + 1 < args.Length:
                    if (!TryParseDuration(args[++i], out reconnectGrace))
                    {
                        Console.Error.WriteLine($"--reconnect-grace: cannot parse '{args[i]}' (e.g. 15, 15s, 1m)");
                        return 2;
                    }
                    break;
                case "--max-uptime" when i + 1 < args.Length:
                    if (!TryParseDuration(args[++i], out maxUptime))
                    {
                        Console.Error.WriteLine($"--max-uptime: cannot parse '{args[i]}'");
                        return 2;
                    }
                    break;
                case "--config" when i + 1 < args.Length:
                    configPath = args[++i];
                    break;
                case "--send-status-sms":
                    sendStatusSms = true;
                    break;
                case "--read-sms" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out int n) || n is < 1 or > 100)
                    {
                        Console.Error.WriteLine("--read-sms: count must be 1..100");
                        return 2;
                    }
                    readSmsCount = n;
                    break;
                case "--read-sms":
                    readSmsCount = 3;
                    break;
                default:
                    Console.Error.WriteLine("usage: Sample.Full [--check] [--backoff DUR] [--kick-threshold N] [--kick-window DUR] [--reconnect-grace DUR] [--config PATH] [--no-tap] [--selftest-kick] [--keep-hold] [--max-uptime DUR] [--send-status-sms] [--read-sms [N]]");
                    return 2;
            }
        }

        ZyxelSettings settings;
        try
        {
            settings = ZyxelSettings.Load(configPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"settings: {ex.Message}");
            return 2;
        }

        string? validation = settings.Validate();
        if (validation is not null)
        {
            Console.Error.WriteLine($"settings: {validation}");
            return 2;
        }

        void Log(string message) => Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.Error.WriteLine();
            Console.Error.WriteLine("Ctrl+C — stopping...");
        };

        var persistent = new PersistentRootShell(settings, Log);
        KickMonitor? kick = null;
        ApnBackoff? backoffState = null;
        DataStateMonitor? dataState = null;
        Task? reader = null;
        int exit = 0;

        try
        {
            await persistent.ConnectAsync(cts.Token);
            Console.WriteLine($"connected (root-port {settings.RootPort})");

            // One-shot SMS operations: run and exit (the outer finally
            // disposes the shell).
            if (sendStatusSms || readSmsCount is not null)
            {
                int rc = 0;
                if (sendStatusSms)
                    rc = await SendStatusSmsAsync(persistent, cts.Token);
                if (readSmsCount is not null)
                    rc |= await ReadRecentSmsAsync(persistent, readSmsCount.Value, cts.Token);
                return rc;
            }

            CommandResult script = await persistent.RunAsync(
                "test -x /xdata/ra_keep.sh && echo script-ok", TimeSpan.FromSeconds(10), cts.Token);
            if (!script.Output.Contains("script-ok", StringComparison.Ordinal))
                Console.WriteLine("note: the watchdog script is missing or not executable (the cron line will no-op)");

            if (!await EnsureCronJobAsync(persistent, checkOnly, cts.Token))
                return 3;
            if (checkOnly)
                return 0;

            // ------------------------------------------------- kick monitor
            bool tapOk = !noTap && (await persistent.RunAsync(
                "test -x /tmp/mipc_tap && echo tap-ok", TimeSpan.FromSeconds(10), cts.Token))
                .Output.Contains("tap-ok", StringComparison.Ordinal);
            if (!noTap && !tapOk)
                Console.WriteLine("note: /tmp/mipc_tap absent (a /tmp push; wiped by reboots) — log-tails-only mode");

            kick = new KickMonitor(persistent, new KickMonitorOptions
            {
                PollInterval = TimeSpan.FromSeconds(2),
                AtCommands = new[] { "AT+CSQ" },   // CREG/CGREG read CME 100 on this modem
                TapEnabled = tapOk,
                LogSources = new KickLogSource[]
                {
                    // Kick detector: every matching line IS a kick (see KickSignature).
                    new KickLogSource(MmmLog, KickSignature.ToString()),
                    // Context: cm dial-state changes (verify the re-dial after GoOn).
                    new KickLogSource(CmLog, @"CHANGE STATE"),
                },
            });

            backoffState = new ApnBackoff(persistent, backoff, keepHoldOnExit, Log)
            {
                KickThreshold = kickThreshold,
                KickWindow = kickWindow,
                ReconnectGrace = reconnectGrace,
            };

            try
            {
                // A previous crashed run may have left the box in OnHold —
                // make sure it is dialing before we start monitoring.
                await backoffState.StartupGoOnAsync(cts.Token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log($"startup safety check failed: {ex.Message} (if the box is still held: 'cmctl send GoOn')");
            }

            // --------------------------------------------- data up/down
            // Console for now; the StateChanged event is what a later
            // Home Assistant entity (or anything else) binds to.
            dataState = new DataStateMonitor(persistent);
            dataState.StateChanged += (_, e) =>
            {
                string word = e.State == DataState.Connected ? "CONNECTED" : "DISCONNECTED";
                string was = e.Previous == DataState.Unknown
                    ? "(initial)"
                    : $"was {e.Previous.ToString().ToUpperInvariant()}";
                Console.WriteLine($"{e.At:HH:mm:ss} data connection: {word}" +
                    (e.Interface is null ? "" : $" on {e.Interface}") +
                    $" — {was} — {e.Detail}");
            };
            await dataState.StartAsync(cts.Token);

            reader = Task.Run(async () =>
            {
                string lastAtPoll = "";
                try
                {
                    await foreach (KickRecord r in kick.ReadAsync(cts.Token))
                    {
                        switch (r.Kind)
                        {
                            case KickRecordKind.Log when r.Label == MmmLog:
                                // A teardown-signature line (the filter only passes
                                // these). Not itself proof of a kick — the box's own
                                // GoOn→redial prints the same lines — so route it
                                // through the debounce; the hold fires only after
                                // KickThreshold episodes within KickWindow.
                                try
                                {
                                    await backoffState.OnTeardownSignatureAsync(r.Detail ?? r.Text);
                                }
                                catch (Exception ex)
                                {
                                    Log($"back-off failed: {ex.Message}");
                                }
                                break;

                            case KickRecordKind.AtPoll when r.Text != lastAtPoll:
                                lastAtPoll = r.Text;
                                Console.WriteLine($"{r.At:HH:mm:ss} csq: {r.Text}");
                                break;

                            case KickRecordKind.ModemHang:
                                Console.WriteLine($"{r.At:HH:mm:ss} *** MODEM HANG ({r.Label}): {r.Text} ***");
                                break;

                            case KickRecordKind.Notice:
                                Console.WriteLine($"{r.At:HH:mm:ss} note: {r.Text}");
                                break;

                            case KickRecordKind.Log:
                                Console.WriteLine($"{r.At:HH:mm:ss} {r.Label}: {FirstLine(r.Detail ?? r.Text)}");
                                break;

                            case KickRecordKind.Tap:
                                Console.WriteLine($"{r.At:HH:mm:ss} tap: {FirstLine(r.Text)}");
                                break;
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }, CancellationToken.None);

            await kick.StartAsync(cts.Token);

            // The handler runs on the maintain loop after a live shell is
            // available, so it may call RunAsync directly (no deadlock).
            persistent.RouterRebooted += async (_, e) =>
            {
                Console.WriteLine($"!!! RouterRebooted: uptime {e.ObservedUptimeSeconds:F0}s, down {e.DisconnectedFor.TotalSeconds:F0}s");
                try
                {
                    await EnsureCronJobAsync(persistent, checkOnly: false, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Log($"reboot: cron re-ensure failed: {ex.Message}");
                }

                try
                {
                    // cm (and its hold state) restarted with the box: if the
                    // back-off is still active, re-arm OnHold immediately, or
                    // the box would re-dial and win the account back early.
                    await backoffState!.OnRouterRebootedAsync();
                }
                catch (Exception ex)
                {
                    Log($"reboot: hold re-arm failed: {ex.Message}");
                }
            };

            Console.WriteLine($"ready — cron ensured, kick monitor running, data-state poll running" +
                $" (currently {dataState.Current}{(dataState.CurrentInterface is not null ? $"/{dataState.CurrentInterface}" : "")})" +
                $", APN back-off {backoff:hh\\:mm\\:ss} (Ctrl+C to stop)");

            if (selftestKick)
            {
                Console.WriteLine("selftest: injecting one synthetic kick");
                await backoffState.OnKickDetectedAsync();
            }

            try
            {
                await Task.Delay(maxUptime, cts.Token);
                Log("max-uptime reached — exiting cleanly");
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C / exit path
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C path
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
            exit = 1;
        }
        finally
        {
            if (kick is not null)
            {
                try
                {
                    if (kick.IsRunning)
                        await kick.StopAsync(CancellationToken.None);
                    if (reader is not null && !reader.IsCompleted)
                        await reader.WaitAsync(TimeSpan.FromSeconds(3));
                    await kick.DisposeAsync();
                }
                catch
                {
                    // best-effort; the device state is in the console stream
                }
            }

            if (backoffState is not null)
            {
                try
                {
                    // Default: leave the box dialing (a hold outliving the
                    // monitor would silence the WAN until a manual GoOn).
                    await backoffState.ShutdownAsync(keepHoldOnExit);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"exit: hold release failed: {ex.Message} (send 'cmctl send GoOn' on the router)");
                }
            }

            if (dataState is not null)
            {
                try
                {
                    await dataState.DisposeAsync();
                }
                catch
                {
                    // best-effort
                }
            }

            await persistent.DisposeAsync();
        }

        cts.Dispose();
        return exit;
    }

    /// <summary>
    /// Checks crontab for the watchdog line; installs it when missing (and not
    /// check-only) with the migration guide's one-liner, then re-reads crontab
    /// to verify. Returns true when the job is (now) present.
    /// </summary>
    private static async Task<bool> EnsureCronJobAsync(PersistentRootShell shell, bool checkOnly, CancellationToken ct)
    {
        CommandResult list = await shell.RunAsync("crontab -l 2>/dev/null", TimeSpan.FromSeconds(10), ct);
        bool present = list.Output.Contains(CronMarker, StringComparison.Ordinal);
        if (present)
        {
            if (!checkOnly)
                Console.WriteLine("cron job present: " +
                    list.Output.Split('\n').First(l => l.Contains(CronMarker, StringComparison.Ordinal)).Trim());
            return true;
        }

        Console.WriteLine("cron job missing" + (checkOnly ? " (--check: not installing)" : "; installing"));
        if (checkOnly)
            return false;

        string install =
            "crontab -l 2>/dev/null | grep -v ra_keep.sh > /tmp/.cron_guard; " +
            $"echo '{CronLine}' >> /tmp/.cron_guard; " +
            "crontab /tmp/.cron_guard; rm -f /tmp/.cron_guard; " +
            "crontab -l 2>/dev/null";
        CommandResult r = await shell.RunAsync(install, TimeSpan.FromSeconds(15), ct);
        bool ok = r.Success && r.Output.Contains(CronMarker, StringComparison.Ordinal);
        Console.WriteLine(ok
            ? "installed; crontab now: " + r.Output.Trim()
            : $"install FAILED (exit={r.ExitCode}): {r.Output.Trim()}");
        return ok;
    }

    /// <summary>
    /// The status SMS the carrier short number expects (a query the carrier
    /// answers by SMS — see <see cref="ReadRecentSmsAsync"/>).
    /// </summary>
    private const string StatusSmsNumber = "1266";
    private const string StatusSmsText = "NL2000 AAN";

    /// <summary>
    /// One-shot (<c>--send-status-sms</c>): sends the status SMS and exits.
    /// Exit 0 only when the modem confirmed with a fresh <c>+CMGS:</c>.
    /// </summary>
    private static async Task<int> SendStatusSmsAsync(PersistentRootShell shell, CancellationToken ct)
    {
        var sms = new ZyxelSms(shell);
        Console.WriteLine($"sending '{StatusSmsText}' to {StatusSmsNumber} …");
        SmsSendResult r = await sms.SendAsync(StatusSmsNumber, StatusSmsText, ct: ct);
        Console.WriteLine(r.Success ? $"sent OK — {r}" : $"send FAILED — {r}");
        return r.Success ? 0 : 1;
    }

    /// <summary>
    /// One-shot (<c>--read-sms [N]</c>): prints the N (default 3) most recent
    /// RECEIVED messages from the router's inbox (newest first) and exits.
    /// The inbox mixes sent and received records; the sent ones are filtered
    /// out, and more files than N are fetched so the result is really the
    /// newest N *received*.
    /// </summary>
    private static async Task<int> ReadRecentSmsAsync(PersistentRootShell shell, int count, CancellationToken ct)
    {
        var sms = new ZyxelSms(shell);
        IReadOnlyList<SmsMessage> received =
            await sms.GetStoredAsync(maxMessages: Math.Max(30, count), includeSent: false, ct: ct);

        if (received.Count == 0)
        {
            Console.WriteLine("(no received SMS stored in the router inbox)");
            return 0;
        }

        foreach (SmsMessage m in received.Take(count))
            Console.WriteLine(m);
        return 0;
    }

    private static bool TryParseDuration(string text, out TimeSpan value)
    {
        value = default;
        if (text.Length == 0) return false;

        string number = text;
        char unit = '\0';
        if (text[^1] is 's' or 'm' or 'h')
        {
            unit = text[^1];
            number = text[..^1];
        }

        if (!double.TryParse(number, out double v) || v < 0) return false;
        value = unit switch
        {
            'h' => TimeSpan.FromHours(v),
            'm' => TimeSpan.FromMinutes(v),
            's' or _ => TimeSpan.FromSeconds(v),   // bare number = seconds
        };
        return true;
    }

    private static string FirstLine(string text)
    {
        int nl = text.IndexOf('\n');
        string line = (nl < 0 ? text : text[..nl]).Trim();
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}

/// <summary>
/// The APN back-off state machine: on a kick, <c>cmctl send OnHold</c> stops cm
/// from (re-)dialing so the other device on the same account keeps the
/// connection; after the backoff, <c>cmctl send GoOn</c> lets cm re-dial.
/// </summary>
/// <remarks>
/// A kick while already holding extends the hold to the full duration. A router
/// reboot resets cm's hold state, so <see cref="OnRouterRebootedAsync"/> re-arms
/// <c>OnHold</c> when the back-off is still active. On process exit the hold is
/// released by default (<c>--keep-hold</c> opts out) so the monitor dying never
/// silences the box's WAN.
///
/// <see cref="StartupGoOnAsync"/> covers the inverse: a PREVIOUS crashed run
/// may have left the device held. GoOn on a box that is NOT held is a verified
/// no-op (live 2026-11-20: ack + log lines only, the active session survives,
/// no re-dial), so a blind GoOn at start is the whole safety check — no
/// cm.log hold-state detection needed.
/// </remarks>
internal sealed class ApnBackoff
{
    private static readonly TimeSpan CmdTimeout = TimeSpan.FromSeconds(15);

    private readonly PersistentRootShell _shell;
    private readonly TimeSpan _duration;
    private readonly bool _keepHoldOnExit;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DateTime _holdUntilUtc = DateTime.MinValue;
    private CancellationTokenSource? _timerToken;
    /// <summary>
    /// Device-side truth: OnHold was sent and no GoOn has been sent since.
    /// Kept separate from <see cref="IsHolding"/> so a process exit always
    /// releases a hold the device still has (even if the local timer misfired).
    /// </summary>
    private bool _deviceHolding;

    // ---------------------------------------------------------------- kick trigger
    // The vendor prints the "APN disconnected / Reset PDN" teardown lines on EVERY
    // (re)connect — including the box's own GoOn→redial — so a single line is NOT
    // proof of a kick (live 2026-11-21: a GoOn re-triggered a "kick" instantly,
    // re-holding the box and blocking its reconnect in a loop). A real same-account
    // fight shows repeated teardowns; a reconnect or one-off hiccup shows at most
    // one. So: require KickThreshold distinct episodes within KickWindow, and
    // ignore the burst right after our own GoOn (ReconnectGrace).
    public int KickThreshold { get; set; } = 3;
    public TimeSpan KickWindow { get; set; } = TimeSpan.FromSeconds(120);
    public TimeSpan EventDedupe { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan ReconnectGrace { get; set; } = TimeSpan.FromSeconds(15);

    private readonly List<DateTime> _teardownEvents = new();
    private DateTime _lastTeardownEventUtc = DateTime.MinValue;
    private DateTime _reconnectGraceUntilUtc = DateTime.MinValue;

    public ApnBackoff(PersistentRootShell shell, TimeSpan duration, bool keepHoldOnExit, Action<string> log)
    {
        _shell = shell;
        _duration = duration;
        _keepHoldOnExit = keepHoldOnExit;
        _log = log;
    }

    /// <summary>
    /// Startup safety: always send GoOn. A previous (crashed) run may have
    /// left the device in <c>OnHold</c> state; GoOn clears that. GoOn on a box
    /// that is not held is a verified no-op (live 2026-11-20: ack + log lines
    /// only, the active data session survives, no re-dial), so no hold-state
    /// detection is needed — just send it and verify the APN status.
    /// </summary>
    public async Task StartupGoOnAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _log("startup safety: sending GoOn (releases a stale hold from a previous run; no-op when not held)");
            await SendGoOnAsync("startup safety").ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>True while the back-off is active.</summary>
    public bool IsHolding => DateTime.UtcNow < _holdUntilUtc;

    /// <summary>True while the device still has OnHold armed (needs a GoOn).</summary>
    public bool DeviceHolding => _deviceHolding;

    /// <summary>
    /// Immediate hold, bypassing the debounce — used by <c>--selftest-kick</c>.
    /// Live mmm.log teardown lines must go through
    /// <see cref="OnTeardownSignatureAsync"/> instead.
    /// </summary>
    public async Task OnKickDetectedAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StartHoldAsync("kick detected").ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Live entry point for one mmm.log teardown-signature line. The vendor prints
    /// these lines on every (re)connect — including the box's own GoOn→redial — so
    /// a single line is not proof of a kick. Distinct episodes (bursts within
    /// <see cref="EventDedupe"/> collapse to one) are counted, and the hold starts
    /// only once <see cref="KickThreshold"/> land within <see cref="KickWindow"/>.
    /// Lines inside the post-GoOn <see cref="ReconnectGrace"/> are ignored (they
    /// are our own reconnect, not a kick).
    /// </summary>
    public async Task OnTeardownSignatureAsync(string line, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            DateTime now = DateTime.UtcNow;

            if (now < _reconnectGraceUntilUtc)
            {
                _log($"teardown line in post-GoOn grace (own reconnect) — ignored: {First(line)}");
                return;
            }

            if (now - _lastTeardownEventUtc < EventDedupe)
                return;   // same teardown episode, already counted

            _lastTeardownEventUtc = now;
            _teardownEvents.Add(now);
            while (_teardownEvents.Count > 0 && now - _teardownEvents[0] > KickWindow)
                _teardownEvents.RemoveAt(0);

            if (_teardownEvents.Count < KickThreshold)
            {
                _log($"teardown episode {_teardownEvents.Count}/{KickThreshold} within {KickWindow.TotalSeconds:F0}s — watching");
                return;
            }

            _teardownEvents.Clear();
            await StartHoldAsync("kick threshold met").ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartHoldAsync(string reason)
    {
        bool wasHolding = IsHolding;
        _holdUntilUtc = DateTime.UtcNow.Add(_duration);

        if (wasHolding)
        {
            _log($"kick again during hold — extending to {_holdUntilUtc.ToLocalTime():HH:mm:ss}");
        }
        else
        {
            await SendHoldAsync(reason).ConfigureAwait(false);
        }

        RestartTimer();
    }

    /// <summary>
    /// Rebooted while holding: cm restarted and lost its hold state, so the box
    /// would re-dial (and win the account back) immediately — re-arm OnHold.
    /// </summary>
    public async Task OnRouterRebootedAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsHolding)
            {
                _log($"reboot during active hold (until {_holdUntilUtc.ToLocalTime():HH:mm:ss}) — cm state reset, re-arming OnHold");
                await SendHoldAsync("re-arm after reboot").ConfigureAwait(false);
            }
            // The PC-side timer keeps running either way.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Process exit: release a hold the DEVICE still has unless
    /// <c>--keep-hold</c>. Judged on <see cref="DeviceHolding"/> (the last
    /// OnHold/GoOn actually sent), not the local timer, so a misfired timer can
    /// never leave the box silently held.
    /// </summary>
    public async Task ShutdownAsync(bool keepHold, CancellationToken ct = default)
    {
        if (!DeviceHolding) return;

        if (keepHold)
        {
            _log($"--keep-hold: leaving OnHold active until {_holdUntilUtc.ToLocalTime():HH:mm:ss} (manual 'cmctl send GoOn' to release)");
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _holdUntilUtc = DateTime.MinValue;
            CancelTimer();
            await SendGoOnAsync("process exit").ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SendHoldAsync(string reason)
    {
        CommandResult r = await _shell.RunAsync("cmctl send OnHold", CmdTimeout, CancellationToken.None).ConfigureAwait(false);
        _deviceHolding = true;
        string reply = First(r.Output);
        _log($"APN hold STARTED ({reason}): exit={r.ExitCode} {reply} — re-enable at {_holdUntilUtc.ToLocalTime():HH:mm:ss}");
    }

    private async Task SendGoOnAsync(string reason)
    {
        CommandResult r = await _shell.RunAsync("cmctl send GoOn", CmdTimeout, CancellationToken.None).ConfigureAwait(false);
        _deviceHolding = false;
        CommandResult v = await _shell.RunAsync(
            "cmctl get apnInfo 1 | grep -a -E '^(apnConnStatus|ifName)'", CmdTimeout, CancellationToken.None).ConfigureAwait(false);
        _log($"APN hold released ({reason}): GoOn exit={r.ExitCode}; {First(r.Output)}; now: {First(v.Output)}");

        // The redial this GoOn triggers prints the SAME teardown lines on reconnect
        // (live-verified 2026-11-21: it re-triggered a "kick" instantly). Ignore
        // teardown lines for the grace window so our own reconnect is never read
        // as a new kick.
        _reconnectGraceUntilUtc = DateTime.UtcNow + ReconnectGrace;
    }

    private void RestartTimer()
    {
        CancelTimer();
        var token = new CancellationTokenSource();
        _timerToken = token;

        // Capture the deadline THIS timer is accountable for. On fire we
        // compare it against the current _holdUntilUtc: if a newer kick moved
        // the deadline, a newer timer owns the release and we back off. This
        // avoids the wall-clock race where the delay fires a tick after
        // _holdUntilUtc and IsHolding has already flipped to false.
        DateTime deadlineUtc = _holdUntilUtc;
        double ms = Math.Max(0, (deadlineUtc - DateTime.UtcNow).TotalMilliseconds);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(ms), token.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;   // hold extended or shutdown
            }

            try
            {
                await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (deadlineUtc != _holdUntilUtc) return;   // extended; newer timer releases
                    _holdUntilUtc = DateTime.MinValue;
                    await SendGoOnAsync("back-off elapsed").ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (Exception ex)
            {
                _log($"back-off release FAILED: {ex.Message} (send 'cmctl send GoOn' on the router)");
            }
        }, CancellationToken.None);
    }

    private void CancelTimer()
    {
        try { _timerToken?.Cancel(); } catch (ObjectDisposedException) { }
        _timerToken?.Dispose();
        _timerToken = null;
    }

    private static string First(string text)
    {
        string t = text.Replace("\r", " ").Replace("\n", " | ").Trim();
        return t.Length > 120 ? t[..120] + "…" : t;
    }
}
