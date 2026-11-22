using System.Text.RegularExpressions;

namespace ZyxelRoot.At;

/// <summary>
/// One-shot raw-AT command execution through the vendor's
/// <c>/usr/bin/mipc_wan_cli</c> client.
/// </summary>
/// <remarks>
/// <b>Request/response only.</b> Every call runs
/// <c>mipc_wan_cli --at_cmd "&lt;cmd&gt;"</c>, which opens a fresh MIPC session
/// on its own port (CMIPC6, it does not touch cm's session) and prints the
/// modem's reply. There is <b>no persistent session and no unsolicited (URC)
/// stream</b> through it: the modem's raw URCs never reach the AP in raw form —
/// status updates still come via the log tail (<see cref="AtMonitor"/>), which
/// is by architecture, not a gap.
///
/// The AT string is passed byte-exact: it is single-quoted for the device's
/// ash (only <c>'</c> is escaped), so values like
/// <c>AT+ECFGSET="sms_over_ip","1"</c> survive.
///
/// State-changing AT (<c>AT+CFUN=0</c>, …) works through this client and is
/// the caller's responsibility; the log monitor itself stays read-only.
///
/// <b>A timeout means a hang.</b> A healthy module answers in milliseconds;
/// the vendor client only blocks this long (its per-command block is 300 s)
/// when the module has stopped answering. The 30 s default timeout is
/// therefore the freeze detector, and a timed-out round trip triggers an
/// automatic shell recovery (Ctrl-C, then a drain) so the pty is not left
/// stuck behind the hung client.
/// </remarks>
public sealed class AtCommandClient
{
    private const string Cli = "/usr/bin/mipc_wan_cli";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    internal static readonly Regex CsqRegex = new(@"^\+CSQ:\s*(\d+)\s*,\s*(\d+)", RegexOptions.Compiled | RegexOptions.Multiline);
    internal static readonly Regex CregRegex = new(@"^\+CREG:\s*\d+\s*,\s*(\d+)", RegexOptions.Compiled | RegexOptions.Multiline);
    internal static readonly Regex CgregRegex = new(@"^\+CGREG:\s*\d+\s*,\s*(\d+)", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex CgpaddrRegex = new(@"^\+CGPADDR:\s*\d+\s*,\s*""([^""]*)""", RegexOptions.Compiled | RegexOptions.Multiline);
    internal static readonly Regex CmeRegex = new(@"^\+CME ERROR:\s*(\d+)", RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly ICommandRunner _shell;

    public AtCommandClient(ICommandRunner shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
    }

    /// <summary>
    /// Sends one AT command through <c>mipc_wan_cli</c> and returns the exit
    /// code and the full printed output (framing lines included).
    /// </summary>
    public async Task<AtCommandResult> SendAtCommandAsync(string atCommand, int timeoutSeconds = 30, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(atCommand))
            throw new ArgumentException("AT command is empty", nameof(atCommand));
        if (atCommand.Any(c => c is < ' ' or > '~'))
            throw new ArgumentException("AT command must be single-line printable ASCII", nameof(atCommand));

        // Single-quoted for the device ash; the marker protocol keeps the rest.
        string command = $"{Cli} --at_cmd '{atCommand.Replace("'", "'\\''")}'";

        CommandResult result = await _shell.RunAsync(command, TimeSpan.FromSeconds(timeoutSeconds), ct)
            .ConfigureAwait(false);
        if (result.TimedOut)
            ShellHangRecovery.Schedule(_shell);
        return new AtCommandResult(result.ExitCode, result.Output, result.TimedOut);
    }

    /// <summary>
    /// A practical live-status snapshot: <c>AT+CSQ</c>, <c>AT+CREG</c>,
    /// <c>AT+CGREG</c> and <c>AT+CGPADDR=1</c> (one fresh MIPC session each).
    /// Stops issuing commands after the first timeout — the AT path is hung,
    /// and the rest would only re-hang the vendor client for its 300 s block.
    /// </summary>
    public async Task<AtStatusSnapshot> PollStatusAsync(CancellationToken ct = default)
    {
        CommandResult csq = await RunAsync("AT+CSQ", ct).ConfigureAwait(false);
        CommandResult creg = csq.TimedOut ? HungStub : await RunAsync("AT+CREG", ct).ConfigureAwait(false);
        CommandResult cgreg = creg.TimedOut ? HungStub : await RunAsync("AT+CGREG", ct).ConfigureAwait(false);
        CommandResult paddr = cgreg.TimedOut ? HungStub : await RunAsync("AT+CGPADDR=1", ct).ConfigureAwait(false);

        Match csqMatch = CsqRegex.Match(csq.Output);
        Match cregMatch = CregRegex.Match(creg.Output);
        Match cgregMatch = CgregRegex.Match(cgreg.Output);
        Match ipMatch = CgpaddrRegex.Match(paddr.Output);

        return new AtStatusSnapshot
        {
            Rssi = csqMatch.Success ? int.Parse(csqMatch.Groups[1].Value) : null,
            Ber = csqMatch.Success ? int.Parse(csqMatch.Groups[2].Value) : null,
            CregStat = cregMatch.Success ? int.Parse(cregMatch.Groups[1].Value) : null,
            CgregStat = cgregMatch.Success ? int.Parse(cgregMatch.Groups[1].Value) : null,
            PdpIp = ipMatch.Success ? ipMatch.Groups[1].Value : null,
            Raw = string.Join("\n---\n", csq.Output, creg.Output, cgreg.Output, paddr.Output)
        };
    }

    /// <summary>A stub for the commands a hung AT path never reaches.</summary>
    private static readonly CommandResult HungStub = new(null, "", Array.Empty<byte>(), timedOut: true);

    private async Task<CommandResult> RunAsync(string atCommand, CancellationToken ct)
    {
        CommandResult result = await _shell.RunAsync(
            $"{Cli} --at_cmd '{atCommand}'", DefaultTimeout, ct).ConfigureAwait(false);
        if (result.TimedOut)
            ShellHangRecovery.Schedule(_shell);
        return result;
    }
}

/// <summary>Result of one <c>mipc_wan_cli</c> round trip.</summary>
public sealed class AtCommandResult
{
    public AtCommandResult(int? exitCode, string output, bool timedOut)
    {
        ExitCode = exitCode;
        Output = output;
        TimedOut = timedOut;
    }

    /// <summary>Exit status reported by the shell, or <c>null</c> on timeout.</summary>
    public int? ExitCode { get; }

    /// <summary>
    /// Full printed output (CRLF normalised), including the client's framing
    /// (<c>stop log print.</c> / <c>AT response:</c> / the <c>+URC</c> lines /
    /// <c>OK</c> or <c>ERROR</c>).
    /// </summary>
    public string Output { get; }

    /// <summary>
    /// True when the reply did not arrive within the timeout — for an AT
    /// command that means the module/AT path hung (a healthy module answers
    /// in milliseconds); the client recovers the shell automatically.
    /// </summary>
    public bool TimedOut { get; }

    /// <summary>True when the client exited 0 within the timeout.</summary>
    public bool Success => ExitCode == 0 && !TimedOut;

    public override string ToString() =>
        TimedOut ? $"(timeout) {Output}" : $"(exit {ExitCode?.ToString() ?? "?"}) {Output}";
}

/// <summary>Parsed snapshot from <see cref="AtCommandClient.PollStatusAsync"/>.</summary>
public sealed class AtStatusSnapshot
{
    /// <summary>Signal strength 0..31 (99 = unknown).</summary>
    public int? Rssi { get; init; }

    /// <summary>Bit error rate 0..7 (99 = unknown).</summary>
    public int? Ber { get; init; }

    /// <summary><c>+CREG</c> registration: 0 not reg, 1 home, 2 searching, 3 denied, 5 unknown.</summary>
    public int? CregStat { get; init; }

    /// <summary><c>+CGREG</c> EPS registration: same codes as <see cref="CregStat"/>.</summary>
    public int? CgregStat { get; init; }

    /// <summary>PDN context 1 address, or <c>null</c> when unassigned.</summary>
    public string? PdpIp { get; init; }

    /// <summary>The four raw <c>mipc_wan_cli</c> outputs, joined with <c>---</c>.</summary>
    public string Raw { get; init; } = "";

    public override string ToString() =>
        $"rssi={(Rssi?.ToString() ?? "-")}/31 ber={(Ber?.ToString() ?? "-")} " +
        $"creg={(CregStat?.ToString() ?? "-")} cgreg={(CgregStat?.ToString() ?? "-")} ip={PdpIp ?? "-"}";
}
