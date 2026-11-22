using System.Text;
using ZyxelRoot;
using ZyxelRoot.At;

namespace ZyxelRoot.Kick;

/// <summary>
/// Kick A/B capture runner (see README §5.7).
/// </summary>
/// <remarks>
/// 1. Connects to the root shell (bootstraps the daemon if 4444 is down).
/// 2. Writes a baseline (register status + CSQ), then starts the
///    <see cref="KickMonitor"/>.
/// 3. Streams every record live to the console and writes the full capture
///    (records + raw details) to a log file.
/// 4. <b>You</b> press ENTER the instant the phone (same subscription/IMSI)
///    leaves airplane mode → <c>KickMonitor.Mark</c>; a second ENTER (or
///    Ctrl+C) stops the capture.
/// 5. Prints a summary; the log file is the artifact to score (tap lines
///    near the mark, signal health before the camp-down, camp-up timing).
/// </remarks>
internal static class Program
{
    private static readonly System.Text.RegularExpressions.Regex TapLifecycle = new(
        @"^\S+\s+(?:START|STOP|mipc_init|register_ind|ERROR|AT-REQ|AT-RSP)\b",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static async Task<int> Main(string[] rawArgs)
    {
        // ------------------------------------------------------------- args
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

        int intervalSec = args.TryGetValue("interval", out string? iv) && int.TryParse(iv, out int n) && n >= 2
            ? n
            : 5;

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

        string? validation = settings.Validate();
        if (validation is not null)
        {
            Console.Error.WriteLine($"settings: {validation}");
            return 2;
        }

        // ------------------------------------------------------ capture file
        string? outFileArg = args.GetValueOrDefault("out");
        string outFile = outFileArg is null
            ? Path.Combine(Directory.GetCurrentDirectory(), "captures", $"kick-{DateTime.Now:yyyyMMdd-HHmmss}.log")
            : Path.GetFullPath(outFileArg);
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.Error.WriteLine();
            Console.Error.WriteLine("Ctrl+C — stopping cleanly (tap daemon included)...");
        };

        RootShell? shell = null;
        KickMonitor? kick = null;
        Task? reader = null;
        StreamWriter? writer = null;
        var all = new List<KickRecord>();

        int exit = 0;
        try
        {
            // --------------------------------------------------------- connect
            bool reachable = await RootShell.ProbeAsync(settings.Host, settings.RootPort, TimeSpan.FromSeconds(2), cts.Token);
            Console.WriteLine($"host={settings.Host} root-port={settings.RootPort} user={settings.Username}  port reachable: {reachable}");

            if (!reachable)
            {
                Console.WriteLine("root telnetd not running — bootstrapping (debug SSH primary, web fallback)...");
                var manager = new RootDaemonManager(settings, null);
                RootDaemonReport report = await manager.StartAsync(cts.Token);
                if (!report.Succeeded)
                {
                    Console.Error.WriteLine("could not start the root daemon");
                    return 3;
                }
            }

            shell = await RootShell.ConnectAsync(settings.Host, settings.RootPort, TimeSpan.FromSeconds(10), cts.Token);
            Console.WriteLine("connected.");

            writer = new StreamWriter(outFile, append: false, new UTF8Encoding(false));
            writer.WriteLine($"=== ZyxelRoot.Kick capture started {DateTime.Now:O} (local {DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===");
            writer.WriteLine($"host={settings.Host} root-port={settings.RootPort} poll-interval={intervalSec}s ind=0x4001:0x10");
            writer.Flush();

            // ------------------------------------------------- baseline (doc step 0)
            string reg = (await shell.RunAsync(
                "/usr/bin/mipc_wan_cli --show_register_status 2>&1 | head -n 3",
                TimeSpan.FromSeconds(15), cts.Token)).Output.Trim();
            string csq = (await shell.RunAsync(
                "/usr/bin/mipc_wan_cli --at_cmd AT+CSQ 2>&1 | grep -a CSQ",
                TimeSpan.FromSeconds(15), cts.Token)).Output.Trim();
            writer.WriteLine($"baseline register: {(reg.Length > 0 ? reg : "(no output)")}");
            writer.WriteLine($"baseline csq: {(csq.Length > 0 ? csq : "(no CSQ line)")}");
            writer.Flush();
            Console.WriteLine($"baseline: {reg} | {csq}");
            Console.WriteLine($"capture -> {outFile}");
            Console.WriteLine();

            // ------------------------------------------------------- monitor
            kick = new KickMonitor(shell, new KickMonitorOptions
            {
                PollInterval = TimeSpan.FromSeconds(intervalSec)
            });

            reader = Task.Run(async () =>
            {
                await foreach (KickRecord r in kick.ReadAsync(cts.Token))
                {
                    lock (all) all.Add(r);
                    lock (writer!)
                    {
                        WriteFileRecord(writer!, r);
                        writer!.Flush();
                    }
                    Console.WriteLine(FormatConsole(r));
                }
            }, CancellationToken.None);

            await kick.StartAsync(cts.Token);

            // -------------------------------------------------- the flip (you)
            Console.WriteLine();
            Console.Write(">>> Press ENTER the instant the phone leaves airplane mode: ");
            string? flip = await Console.In.ReadLineAsync(cts.Token);
            if (flip is null)
            {
                Console.WriteLine("stdin closed before the flip mark — stopping without a mark.");
            }
            else
            {
                kick.Mark($"flip @ {DateTime.Now:HH:mm:ss}");
                Console.WriteLine(">>> marked. Watching - press ENTER to stop (~15 min recommended, covers the 240 s ladder).");
                try
                {
                    await Console.In.ReadLineAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Ctrl+C
                }
            }
            // stop + cleanup happen in the finally
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C path: fall through to cleanup
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAILED: {ex.Message}");
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
                    // best-effort; the device state is reported in the stream/file
                }
            }

            if (writer is not null)
            {
                try
                {
                    lock (all)
                    {
                        var counts = all
                            .GroupBy(r => r.Kind)
                            .OrderBy(g => g.Key)
                            .Select(g => $"{g.Key}={g.Count()}")
                            .ToArray();
                        int tapData = all.Count(r => r.Kind == KickRecordKind.Tap && !TapLifecycle.IsMatch(r.Text));
                        writer.WriteLine();
                        writer.WriteLine($"=== capture ended {DateTime.Now:O} — {all.Count} records: {string.Join(" ", counts)} ===");
                        writer.WriteLine($"tap data lines (IND traffic, the jackpot axis): {tapData}");
                    }
                    writer.Flush();
                }
                catch { }
                await writer.DisposeAsync();
            }

            if (shell is not null)
            {
                try { await shell.DisposeAsync(); } catch { }
            }

            if (exit == 0)
            {
                int tapData = 0;
                var counts = new List<string>();
                lock (all)
                {
                    counts = all
                        .GroupBy(r => r.Kind)
                        .OrderBy(g => g.Key)
                        .Select(g => $"{g.Key}={g.Count()}")
                        .ToList();
                    tapData = all.Count(r => r.Kind == KickRecordKind.Tap && !TapLifecycle.IsMatch(r.Text));
                }
                Console.WriteLine();
                Console.WriteLine($"capture complete: {all.Count} records ({string.Join(" ", counts)}), tap data lines: {tapData}");
                Console.WriteLine($"capture saved to: {outFile}");
                Console.WriteLine("score it against the kick-detection scorecard (records + timing fingerprint)");
            }
        }

        cts.Dispose();
        return exit;
    }

    private static void WriteFileRecord(StreamWriter w, KickRecord r)
    {
        w.WriteLine($"{r.At:HH:mm:ss.fff} [{r.Kind}] {r.Label}: {r.Text}");
        if (!string.IsNullOrWhiteSpace(r.Detail))
        {
            w.WriteLine("    detail:");
            foreach (string line in r.Detail!.Replace("\r\n", "\n").Split('\n'))
                w.WriteLine("      " + line);
        }
    }

    private static string FormatConsole(KickRecord r)
    {
        string head = $"{r.At:HH:mm:ss.fff} {r.Kind.ToString().PadRight(7)}";
        return r.Kind switch
        {
            KickRecordKind.Mark => $"   *** {head} MARK: {r.Text} ***",
            KickRecordKind.Notice => $"   {head} note: {r.Text}",
            _ => $"{head} {r.Label}: {r.Text}"
        };
    }
}
