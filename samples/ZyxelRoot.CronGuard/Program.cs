using ZyxelRoot;

// CronGuard — keeps a watchdog cron job registered on the Zyxel across
// reboots (the example job below; adjust it to your own script).
//
//   dotnet run --project samples/ZyxelRoot.CronGuard [--check] [--config PATH]
//
// Connects the persistent root shell (bootstrapping the telnetd daemon when
// the port is down), checks crontab at startup and installs the job when
// missing, then sits: on every detected router reboot
// (PersistentRootShell.RouterRebooted) it re-checks and re-installs.
// --check: read-only, report the crontab state and exit (0 = present).

internal static class Program
{
    // The example watchdog line (adjust to your own job).
    private const string CronLine = "*/5 * * * * /xdata/ra_keep.sh";
    private const string CronMarker = "ra_keep.sh";

    private static async Task<int> Main(string[] args)
    {
        bool checkOnly = false;
        string? configPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--check":
                    checkOnly = true;
                    break;
                case "--config" when i + 1 < args.Length:
                    configPath = args[++i];
                    break;
                default:
                    Console.Error.WriteLine("usage: CronGuard [--check] [--config PATH]");
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

        var persistent = new PersistentRootShell(settings, Log);
        try
        {
            await persistent.ConnectAsync();
            Console.WriteLine($"connected (root-port {settings.RootPort})");

            // Note, not fatal: until migration Phase 3.1 ran, the script is
            // absent and the cron line would no-op.
            CommandResult script = await persistent.RunAsync(
                "test -x /xdata/ra_keep.sh && echo script-ok", TimeSpan.FromSeconds(10));
            if (!script.Output.Contains("script-ok", StringComparison.Ordinal))
                Console.WriteLine("note: /xdata/ra_keep.sh missing or not executable (migration Phase 3.1 not applied yet)");

            if (!await EnsureCronJobAsync(persistent, checkOnly))
                return 3;

            if (checkOnly)
                return 0;

            // The handler runs on the maintain loop after a live shell is
            // available, so it may call RunAsync directly (no deadlock).
            persistent.RouterRebooted += async (_, e) =>
            {
                Console.WriteLine($"!!! RouterRebooted: uptime {e.ObservedUptimeSeconds:F0}s, down {e.DisconnectedFor.TotalSeconds:F0}s");
                try
                {
                    await EnsureCronJobAsync(persistent, checkOnly: false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"reboot handler failed: {ex.Message}");
                }
            };

            Console.WriteLine("cron job ensured; watching for reboots (Ctrl+C to stop)");
            await Task.Delay(Timeout.Infinite);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            await persistent.DisposeAsync();
        }
    }

    /// <summary>
    /// Checks crontab for the watchdog line; installs it when missing (and
    /// not check-only) with the migration guide's one-liner, then re-reads
    /// crontab to verify. Returns true when the job is (now) present.
    /// </summary>
    private static async Task<bool> EnsureCronJobAsync(PersistentRootShell shell, bool checkOnly)
    {
        CommandResult list = await shell.RunAsync("crontab -l 2>/dev/null", TimeSpan.FromSeconds(10));
        bool present = list.Output.Contains(CronMarker, StringComparison.Ordinal);
        if (present)
        {
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
        CommandResult r = await shell.RunAsync(install, TimeSpan.FromSeconds(15));
        bool ok = r.Success && r.Output.Contains(CronMarker, StringComparison.Ordinal);
        Console.WriteLine(ok
            ? "installed; crontab now: " + r.Output.Trim()
            : $"install FAILED (exit={r.ExitCode}): {r.Output.Trim()}");
        return ok;
    }
}
