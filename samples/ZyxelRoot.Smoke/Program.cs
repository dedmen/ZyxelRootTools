using System.Diagnostics;
using System.Text;
using ZyxelRoot;
using ZyxelRoot.At;
using ZyxelRoot.Led;
using ZyxelRoot.Modem;
using ZyxelRoot.Sms;

namespace ZyxelRoot.Smoke;

/// <summary>
/// Live smoke test:
/// 1. connect to the root telnetd (bootstrap it through the debug-SSH account
///    if needed, web-DAL injection as fallback),
/// 2. run <c>id</c> and <c>uname -a</c> through the marker protocol,
/// 3. optionally (<c>--sms [number]</c>) send a test SMS and list the stored inbox,
/// 4. send <c>AT+CSQ</c> on demand (mipc_wan_cli) and poll a
///    CSQ/CREG/CGREG/CGPADDR status snapshot,
/// 5. capture AT-monitor events for a while, with read-only <c>cmctl get</c> traffic
///    (skipped when <c>--sms</c> is used without an explicit <c>--seconds</c>),
/// 6. on demand (<c>--kick [seconds]</c>): a <c>KickMonitor</c> A/B capture
///    (raw-URC tap + 5 s AT poll + filtered log tails) for the given seconds.
/// On demand: <c>--root-ssh</c> (direct root SSH login test),
/// <c>--watch [seconds]</c> (PersistentRootShell keep-alive) and
/// <c>--selftest-reboot</c> (pure reboot rule, no router).
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] rawArgs)
    {
        var args = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rawArgs.Length; i++)
        {
            string arg = rawArgs[i];
            if (!arg.StartsWith("--")) continue;

            string name = arg[2..];
            if (name.Contains('='))
            {
                int eq = name.IndexOf('=');
                args[name[..eq]] = name[(eq + 1)..];
            }
            else if (i + 1 < rawArgs.Length && !rawArgs[i + 1].StartsWith("--"))
            {
                args[name] = rawArgs[i + 1];
                i++;
            }
            else
            {
                args[name] = "true";
            }
        }

        bool flag(string name) => args.ContainsKey(name) && args[name] is not "false" and not "0";
        int number(string name, int fallback) => args.TryGetValue(name, out string? v) && int.TryParse(v, out int n) ? n : fallback;

        // --selftest-reboot: run the pure reboot rule on fixed cases and exit
        // (no router, no settings needed).
        if (args.ContainsKey("selftest-reboot"))
            return RunRebootRuleSelfTest();

        ZyxelSettings settings;
        try
        {
            settings = ZyxelSettings.Load(args.GetValueOrDefault("config"));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"settings: {ex.Message}");
            return 2;
        }

        if (args.TryGetValue("host", out string? host) && host is not null) settings.Host = host;
        if (args.TryGetValue("user", out string? user) && user is not null) settings.Username = user;
        if (args.TryGetValue("password", out string? pass) && pass is not null) settings.Password = pass;
        settings.SshPort = number("ssh-port", settings.SshPort);
        settings.RootPort = number("root-port", settings.RootPort);

        int seconds = number("seconds", 30);
        bool trigger = flag("trigger") || !args.ContainsKey("no-trigger");
        bool verbose = flag("verbose");

        string? smsNumber = null;
        if (args.TryGetValue("sms", out string? smsArg) && smsArg is not "false" and not "0")
            smsNumber = smsArg is "true" ? "1266" : smsArg;

        // --sms-long [number]: one multipart (SendLongAsync) test send with
        // the charset auto-picked (GSM 7-bit when the text allows).
        string? smsLongNumber = null;
        if (args.TryGetValue("sms-long", out string? smsLongArg) && smsLongArg is not "false" and not "0")
            smsLongNumber = smsLongArg is "true" ? "1266" : smsLongArg;

        // --modem <state|cycle|off|on>: one module power/reset operation and
        // exit (explicit; never run by default).
        string? modemOp = null;
        if (args.TryGetValue("modem", out string? modemArg) &&
            modemArg is not null and not "false" and not "0")
            modemOp = modemArg is "true" ? "state" : modemArg;

        // --kick [seconds]: KickMonitor A/B capture (tap + AT poll + log tails),
        // run for the given seconds and exit. Explicit; never run by default.
        int? kickSeconds = null;
        if (args.TryGetValue("kick", out string? kickArg) &&
            kickArg is not null and not "false" and not "0")
            kickSeconds = kickArg is "true" ? 25 : int.Parse(kickArg);

        // --led <status|off|off-kill|on|restore>: one front-panel LED
        // operation (ZyxelLed) and exit. Explicit; never run by default.
        string? ledOp = null;
        if (args.TryGetValue("led", out string? ledArg) &&
            ledArg is not null and not "false" and not "0")
            ledOp = ledArg;

        // --watch [seconds]: PersistentRootShell keep-alive test. Connects
        // once, runs `id`, then just waits: drops, re-bootstrap and (only on a
        // real reboot) RouterRebooted are handled inside the library.
        // Explicit; never run by default.
        int? watchSeconds = null;
        if (args.TryGetValue("watch", out string? watchArg) &&
            watchArg is not null and not "false" and not "0")
            watchSeconds = watchArg is "true" ? 60 : int.Parse(watchArg);

        // --root-ssh: direct root SSH login test (Zyxel:RootPassword) —
        // connect, verify uid=0, run id/uname, exit. Explicit; never run
        // by default.
        bool rootSsh = flag("root-ssh");
        if (args.TryGetValue("root-user", out string? rootUser) && rootUser is not null) settings.RootUsername = rootUser;
        if (args.TryGetValue("root-password", out string? rootPass) && rootPass is not null) settings.RootPassword = rootPass;

        // With --sms/--sms-long the point is the SMS round trip; only keep the
        // (slow) AT monitor when the caller asked for it explicitly.
        bool runMonitor = !(smsNumber is not null || smsLongNumber is not null) || args.ContainsKey("seconds");

        string? validation = settings.Validate();
        if (validation is not null)
        {
            Console.Error.WriteLine($"settings: {validation}");
            return 2;
        }

        void Log(string message) => Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");

        Console.WriteLine($"host={settings.Host} ssh={settings.SshPort} root-port={settings.RootPort} user={settings.Username}");
        Console.WriteLine($"config={settings.ConfigPath ?? "(none)"}");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        RootShell? shell = null;

        if (watchSeconds is not null)
            return await RunWatchAsync(settings, watchSeconds.Value, cts.Token, Log);

        if (rootSsh)
            return await RunRootSshAsync(settings, cts.Token);

        // ---------------------------------------------------------- connect
        var connectWatch = Stopwatch.StartNew();
        bool reachable = await RootShell.ProbeAsync(settings.Host, settings.RootPort, TimeSpan.FromSeconds(2), cts.Token);
        Console.WriteLine($"port {settings.RootPort} reachable: {reachable}");

        try
        {
            if (!reachable)
            {
                Log("root telnetd is not running; bootstrapping (debug SSH primary, web fallback)...");
                var manager = new RootDaemonManager(settings, verbose ? Log : null);
                RootDaemonReport report = await manager.StartAsync(cts.Token);
                Console.WriteLine($"rootd: {report}");
                if (!string.IsNullOrWhiteSpace(report.LaunchOutput))
                    Console.WriteLine("rootd launch output:\n" + Indent(report.LaunchOutput));
                if (!report.Succeeded)
                {
                    Console.Error.WriteLine("could not start the root daemon");
                    return 3;
                }
            }

            shell = await RootShell.ConnectAsync(settings.Host, settings.RootPort, TimeSpan.FromSeconds(10), cts.Token);
            Console.WriteLine($"connected in {connectWatch.ElapsedMilliseconds} ms");

            // Demonstrate the async line stream (bounded, drop-oldest).
            var firstLines = new List<string>();
            var lineReader = Task.Run(async () =>
            {
                try
                {
                    await foreach (string line in shell.Lines.ReadAllAsync(cts.Token))
                    {
                        firstLines.Add(line);
                        if (firstLines.Count >= 4) break;
                    }
                }
                catch { }
            }, cts.Token);
            try { await lineReader.WaitAsync(TimeSpan.FromSeconds(3), cts.Token); } catch { }
            Console.WriteLine("line stream: " +
                string.Join(" | ", firstLines.Select(l => l.Trim()).Where(l => l.Length > 0).Take(4)));

            // ------------------------------------------------------ commands
            await VerifyAsync(shell, "id");
            await VerifyAsync(shell, "uname -a");
            await VerifyAsync(shell, "cat /proc/version 2>/dev/null || true");

            CommandResult marker = await shell.RunAsync("echo mmm.log=$(wc -l < /var/log/mmm.log) cm=$(wc -l < /var/log/cm.log)", TimeSpan.FromSeconds(10), cts.Token);
            Console.WriteLine($"markers: exit={marker.ExitCode} output=\"{marker.Output.Trim()}\"");

            // --------------------------------------------------- kick monitor
            if (kickSeconds is not null)
            {
                Console.WriteLine();
                Console.WriteLine($"--- Kick monitor: {kickSeconds}s capture (no phone flip) ---");
                var kick = new KickMonitor(shell);
                var kickRecords = new List<KickRecord>();
                Task reader = Task.Run(async () =>
                {
                    await foreach (KickRecord r in kick.ReadAsync(cts.Token))
                        lock (kickRecords) kickRecords.Add(r);
                }, CancellationToken.None);

                await kick.StartAsync(cts.Token);

                int total = Math.Clamp(kickSeconds.Value, 15, 300);
                int markAt = Math.Clamp(total / 3, 5, 10);
                await Task.Delay(TimeSpan.FromSeconds(markAt), cts.Token);
                kick.Mark("smoke-test");
                await Task.Delay(TimeSpan.FromSeconds(total - markAt - 2), cts.Token);
                await kick.StopAsync(cts.Token);
                await reader; // channel completed, stream drained
                await kick.DisposeAsync();

                bool ok = true;
                var tapLifecycle = new System.Text.RegularExpressions.Regex(
                    @"^\S+\s+(?:START|STOP|mipc_init|register_ind|ERROR|AT-REQ|AT-RSP)\b",
                    System.Text.RegularExpressions.RegexOptions.Compiled);
                lock (kickRecords)
                {
                    Console.WriteLine("records: " + string.Join(" ",
                        kickRecords.GroupBy(r => r.Kind).OrderBy(g => g.Key)
                                   .Select(g => $"{g.Key}={g.Count()}")));

                    int tapData = kickRecords.Count(r => r.Kind == KickRecordKind.Tap &&
                        !tapLifecycle.IsMatch(r.Text));
                    Console.WriteLine($"tap data lines (IND traffic): {tapData}" +
                        (tapData == 0 ? "   <-- FINDING: no URC/IND traffic on 0x4001/0x4502 in this window" : ""));

                    KickRecord? mark = kickRecords.FirstOrDefault(r => r.Kind == KickRecordKind.Mark);
                    Console.WriteLine($"mark delivered: {(mark is null ? "NO" : "yes (" + mark.Text + ")")}");
                    ok &= mark is not null;

                    Console.WriteLine("tail sources:");
                    foreach (AtLogSourceStatus s in kick.GetStatus())
                        Console.WriteLine("  " + s);

                    List<KickRecord> csqs = kickRecords.Where(r => r.Kind == KickRecordKind.AtPoll && r.Label == "AT+CSQ").ToList();
                    Console.WriteLine(csqs.Count > 0
                        ? $"AT+CSQ: rounds={csqs.Count} first={csqs[0].Text} last={csqs[^1].Text}"
                        : "AT+CSQ: no rounds (unexpected)");
                    ok &= csqs.Count > 0;

                    List<KickRecord> taps = kickRecords.Where(r => r.Kind == KickRecordKind.Tap).Take(5).ToList();
                    foreach (KickRecord t in taps)
                        Console.WriteLine("  tap| " + t.Text);
                }

                CommandResult after = await shell.RunAsync(
                    "pidof mipc_tap 2>/dev/null; ls -l /tmp/mipc_tap.log 2>&1", TimeSpan.FromSeconds(10), cts.Token);
                string[] cl = after.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                string pidLine = cl.FirstOrDefault(l =>
                    l.Trim().Split(' ').All(t => t.Length > 0 && t.All(c => c >= '0' && c <= '9'))) ?? "";
                bool noPid = pidLine.Length == 0;
                bool noLog = cl.Any(l => l.Contains("No such file", StringComparison.Ordinal));
                Console.WriteLine($"device after stop: tap pid {(noPid ? "gone" : "STILL RUNNING: " + pidLine)}, " +
                                  $"tap log {(noLog ? "removed" : "STILL PRESENT")}");
                ok &= noPid && noLog;

                return ok ? 0 : 1;
            }

            // -------------------------------------------------------- modem
            if (modemOp is not null)
            {
                Console.WriteLine();
                Console.WriteLine($"--- Modem: {modemOp} (explicit; stopping after) ---");
                var modem = new ModemPower(shell);
                Task<ModemPowerResult> op = modemOp.ToLowerInvariant() switch
                {
                    "state" => modem.RadioStateAsync(cts.Token),
                    "cycle" => modem.RadioCycleAsync(ct: cts.Token),
                    "off" => modem.PowerOffAsync(cts.Token),
                    "on" => modem.PowerOnAsync(cts.Token),
                    _ => throw new ArgumentException($"unknown --modem op '{modemOp}' (state|cycle|off|on)")
                };
                ModemPowerResult r = await op;
                Console.WriteLine(r);
                if (!string.IsNullOrWhiteSpace(r.Output))
                    Console.WriteLine(Indent(r.Output));
                return r.Success ? 0 : 1;
            }

            // ---------------------------------------------------------- LED
            if (ledOp is not null)
            {
                Console.WriteLine();
                Console.WriteLine($"--- LED: {ledOp} (explicit; stopping after) ---");
                var led = new ZyxelLed(shell);

                if (ledOp.ToLowerInvariant() == "status")
                {
                    LedStatus st = await led.StatusAsync(cts.Token);
                    Console.WriteLine($"kernel knob : {(st.KernelModeAvailable ? st.KernelMode : "(absent on this build)")}");
                    Console.WriteLine($"writers     : zycmled={st.ZycmledPid?.ToString() ?? "-"} esmd={st.EsmdPid?.ToString() ?? "-"} zybtnchk={st.ZybtnchkPid?.ToString() ?? "-"}");
                    Console.WriteLine("panel       :");
                    foreach (LedDeviceState d in st.Devices)
                        Console.WriteLine($"  {d.Name,-30} bri={d.Brightness,-4} {(d.IsOn ? "on " : "off")}");
                    return 0;
                }

                LedActionResult lr = ledOp.ToLowerInvariant() switch
                {
                    "off" => await led.TurnOffAsync(stopWriters: false, cts.Token),
                    "off-kill" => await led.TurnOffAsync(stopWriters: true, cts.Token),
                    "on" => await led.AllOnAsync(cts.Token),
                    "restore" => await led.RestoreAsync(cts.Token),
                    _ => throw new ArgumentException($"unknown --led op '{ledOp}' (status|off|off-kill|on|restore)")
                };
                Console.WriteLine(lr);
                if (!string.IsNullOrWhiteSpace(lr.Output))
                    Console.WriteLine(Indent(lr.Output));
                return lr.Success ? 0 : 1;
            }

            // --------------------------------------------------------- SMS
            if (smsNumber is not null)
            {
                Console.WriteLine();
                Console.WriteLine($"--- SMS: send \"NL2000 AAN\" to {smsNumber}, then list the inbox ---");
                await RunSmsTestAsync(shell, smsNumber, cts.Token);
            }

            if (smsLongNumber is not null)
            {
                Console.WriteLine();
                Console.WriteLine($"--- SMS: multipart (SendLongAsync) to {smsLongNumber}, then list the inbox ---");
                await RunSmsLongTestAsync(shell, smsLongNumber, cts.Token);
            }

            // ------------------------------------------------- AT on demand
            Console.WriteLine();
            Console.WriteLine("--- AT on demand (mipc_wan_cli, request/response) ---");
            var at = new AtCommandClient(shell);

            AtCommandResult csq = await at.SendAtCommandAsync("AT+CSQ", 30, cts.Token);
            Console.WriteLine($"AT+CSQ: exit={csq.ExitCode?.ToString() ?? "?"} timedOut={csq.TimedOut}");
            Console.WriteLine(Indent(csq.Output));

            AtStatusSnapshot status = await at.PollStatusAsync(cts.Token);
            Console.WriteLine("status: " + status);

            if (!runMonitor)
                return 0;

            // ------------------------------------------------------ monitor
            Console.WriteLine();
            Console.WriteLine($"--- AT monitor: {seconds}s, sources mmm.log + wan.log + cm.log ---");

            var options = new AtMonitorOptions
            {
                HistoryLines = 10,
                PollInterval = TimeSpan.FromSeconds(1),
                MaxLinesPerPoll = 500
            };

            await using var monitor = new AtMonitor(shell, options);
            await monitor.StartAsync(cts.Token);

            var records = new List<AtLogRecord>();
            var consume = Task.Run(async () =>
            {
                await foreach (AtLogRecord record in monitor.ReadAllAsync(cts.Token))
                {
                    records.Add(record);
                    if (verbose)
                        Console.WriteLine($"  [{record.Source.Split('/')[^1]}] {record}");
                }
            }, cts.Token);

            if (trigger)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
                Log("read-only trigger: cmctl get moduleInfo");
                Console.WriteLine("cmctl get moduleInfo:\n" + Indent((await shell.RunAsync("cmctl get moduleInfo", TimeSpan.FromSeconds(10), cts.Token)).Output));

                await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
                Log("read-only trigger: cmctl get apnInfo 1");
                Console.WriteLine("cmctl get apnInfo 1:\n" + Indent((await shell.RunAsync("cmctl get apnInfo 1", TimeSpan.FromSeconds(10), cts.Token)).Output));
            }

            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(seconds))
                await Task.Delay(250, cts.Token);

            // Let the channel drain, then stop.
            await Task.Delay(800, cts.Token);
            await monitor.DisposeAsync();
            try { await consume; } catch { }

            Console.WriteLine();
            PrintSummary(records, monitor.GetStatus());
            Console.WriteLine();
            Console.WriteLine("--- AT-direction records ---");
            foreach (AtLogRecord record in records.Where(r => r.IsAtTraffic).Take(40))
                Console.WriteLine($"  {record.Source}: {record}");
            Console.WriteLine($"  ({records.Count(r => r.IsAtTraffic)} total)");

            return records.Count > 0 ? 0 : 4;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
            if (verbose) Console.Error.WriteLine(ex);
            return 5;
        }
        finally
        {
            shell?.Dispose();
        }
    }

    private static async Task<int> RunWatchAsync(
        ZyxelSettings settings, int seconds, CancellationToken ct, Action<string> log)
    {
        Console.WriteLine();
        Console.WriteLine($"--- Watch: PersistentRootShell for {seconds}s ---");
        Console.WriteLine("kill the 4444 daemon any time: the library re-bootstraps and reconnects.");
        Console.WriteLine("a real reboot would fire RouterRebooted; a daemon kill must NOT.");

        var persistent = new PersistentRootShell(settings, log);
        int fired = 0;
        persistent.RouterRebooted += (_, e) =>
        {
            fired++;
            Console.WriteLine($"!!! RouterRebooted: uptime={e.ObservedUptimeSeconds:F1}s " +
                              $"disconnected={e.DisconnectedFor.TotalSeconds:F0}s " +
                              $"elapsedSinceFirstConnect={e.ElapsedSinceFirstConnect.TotalSeconds:F0}s " +
                              $"daemonRecreated={e.DaemonRecreated} path={e.BootstrapPath}");
        };

        try
        {
            await persistent.ConnectAsync(ct);
            Console.WriteLine($"connected; reboots while attached will fire RouterRebooted");

            CommandResult id = await persistent.RunAsync("id", TimeSpan.FromSeconds(10), ct);
            Console.WriteLine($"id: {id.Output.Trim()} (exit={id.ExitCode})");

            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(seconds))
                await Task.Delay(1000, ct);

            Console.WriteLine($"reconnects: {persistent.ReconnectCount}   RouterRebooted fired: {fired}");

            CommandResult after = await persistent.RunAsync("id", TimeSpan.FromSeconds(10), ct);
            Console.WriteLine($"final id: {after.Output.Trim()} (exit={after.ExitCode})");

            // The negative-test expectation is caller-dependent (kill the daemon
            // or not); the hard requirement is only that the shell works at the end.
            return after.Success ? 0 : 1;
        }
        finally
        {
            await persistent.DisposeAsync();
        }
    }

    private static async Task<int> RunRootSshAsync(ZyxelSettings settings, CancellationToken ct)
    {
        Console.WriteLine($"--- Direct root SSH: {settings.RootUsername}@{settings.Host}:{settings.SshPort} ---");
        if (string.IsNullOrEmpty(settings.RootPassword))
        {
            Console.Error.WriteLine("settings: Zyxel:RootPassword is empty (direct root SSH not configured)");
            return 2;
        }

        var watch = Stopwatch.StartNew();
        using SshRootShell shell = await SshRootShell.OpenAsync(settings, Console.Error.WriteLine, TimeSpan.FromSeconds(20), ct);
        Console.WriteLine($"connected in {watch.ElapsedMilliseconds} ms (uid=0 verified)");

        await VerifyAsync(shell, "id");
        await VerifyAsync(shell, "uname -a");
        return 0;
    }

    private static int RunRebootRuleSelfTest()
    {
        Console.WriteLine("--- RebootDetection rule (pure; no router needed) ---");
        (bool actual, bool expected, string description)[] cases =
        {
            (RebootDetection.RebootDetected(TimeSpan.FromMinutes(5), 3 * 24 * 3600),
                false, "5 min gap, box up 3 days (LAN hiccup)"),
            (RebootDetection.RebootDetected(TimeSpan.FromMinutes(50), 120),
                true, "attached 50 min, box up 2 min (rebooted)"),
            (RebootDetection.RebootDetected(TimeSpan.FromSeconds(20), 16),
                false, "20 s gap, uptime 16 s (16 >= 20 - 5)"),
            (RebootDetection.RebootDetected(TimeSpan.FromSeconds(20), 14),
                true, "20 s gap, uptime 14 s (14 < 20 - 5)"),
            (RebootDetection.RebootDetected(TimeSpan.FromSeconds(10), 10),
                false, "boundary: 10 !< 10 - 5"),
        };

        bool ok = true;
        foreach ((bool actual, bool expected, string description) in cases)
        {
            bool pass = actual == expected;
            ok &= pass;
            Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}  fire={actual,-5} expected={expected,-5} {description}");
        }

        Console.WriteLine(ok ? "reboot rule: all cases pass" : "reboot rule: FAILURES");
        return ok ? 0 : 1;
    }

    private static async Task VerifyAsync(MarkerShell shell, string command)
    {
        var watch = Stopwatch.StartNew();
        CommandResult result = await shell.RunAsync(command, TimeSpan.FromSeconds(15));
        Console.WriteLine($"$ {command}");
        Console.WriteLine(Indent(result.Output.TrimEnd()));
        Console.WriteLine($"  (exit={result.ExitCode}, {watch.ElapsedMilliseconds} ms, {Encoding.UTF8.GetByteCount(result.Output)} bytes)");
    }

    private static async Task RunSmsTestAsync(RootShell shell, string number, CancellationToken ct)
    {
        var sms = new ZyxelSms(shell);

        var send = await sms.SendAsync(number, "NL2000 AAN", TimeSpan.FromSeconds(25), ct);
        Console.WriteLine($"send to {number}: {send}");
        if (!string.IsNullOrWhiteSpace(send.Output))
            Console.WriteLine(Indent(send.Output));
        Console.WriteLine($"  outbox file : {send.OutboxFile ?? "(none written)"}");
        Console.WriteLine($"  modem report: {(send.ModemReport is null ? "(no +CMGS update within timeout)" : Indent(send.ModemReport))}");

        IReadOnlyList<SmsMessage> stored = await sms.GetStoredAsync(20, includeSent: true, ct);
        Console.WriteLine($"stored inbox  : {stored.Count} message(s), newest first");
        foreach (SmsMessage message in stored.Take(8))
            Console.WriteLine("  " + message);
    }

    private static async Task RunSmsLongTestAsync(RootShell shell, string number, CancellationToken ct)
    {
        var sms = new ZyxelSms(shell);

        // The modem caps total user data at 14 octets per part (verified
        // 2026-11-20), so a multipart part holds 9 GSM 7-bit chars with the
        // 6-byte concat UDH. 27 chars = 3 parts; every character is in the
        // GSM alphabet, so auto-pick must land on GSM 7-bit.
        const string text =
            "A1 B2 C3: 3-part test. OK.";

        Console.WriteLine($"sending {text.Length} chars (multipart, 9/part) to {number} ...");
        SmsLongSendResult result = await sms.SendLongAsync(number, text, ct: ct);
        Console.WriteLine($"send: {result}");
        for (int i = 0; i < result.Parts.Count; i++)
        {
            SmsSendResult part = result.Parts[i];
            Console.WriteLine($"  part {i + 1}/{result.Parts.Count}: {part}");
            Console.WriteLine($"    outbox file: {part.OutboxFile ?? "(none written)"}");
        }

        IReadOnlyList<SmsMessage> stored = await sms.GetStoredAsync(20, includeSent: true, ct);
        Console.WriteLine($"stored inbox  : {stored.Count} message(s), newest first");
        foreach (SmsMessage message in stored.Take(8))
            Console.WriteLine("  " + message);
    }

    private static void PrintSummary(List<AtLogRecord> records, IReadOnlyList<AtLogSourceStatus> status)
    {
        Console.WriteLine("--- sources ---");
        foreach (AtLogSourceStatus s in status)
            Console.WriteLine("  " + s);

        Console.WriteLine($"--- {records.Count} records ---");
        foreach (var group in records.GroupBy(r => $"{r.Module}/{r.Level}").OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {group.Key,-14} {group.Count(),5}");

        Console.WriteLine("--- top functions ---");
        foreach (var group in records.Where(r => r.Function is not null)
                     .GroupBy(r => r.Function!)
                     .OrderByDescending(g => g.Count())
                     .Take(10))
            Console.WriteLine($"  {group.Key,-32} {group.Count(),5}");

        Console.WriteLine("--- AT direction ---");
        Console.WriteLine($"  outgoing: {records.Count(r => r.Direction == AtDirection.Outgoing)}");
        Console.WriteLine($"  incoming: {records.Count(r => r.Direction == AtDirection.Incoming)}");
        Console.WriteLine($"  distinct commands: {string.Join(", ", records.Where(r => r.AtCommand is not null).Select(r => r.AtCommand).Distinct())}");
    }

    private static string Indent(string text) =>
        string.Join('\n', text.Split('\n').Select(line => "    " + line.TrimEnd()));
}
