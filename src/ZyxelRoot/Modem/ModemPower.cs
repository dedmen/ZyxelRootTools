using System.Text.RegularExpressions;

namespace ZyxelRoot.Modem;

/// <summary>Result of one <c>ModemPower</c> operation.</summary>
public sealed class ModemPowerResult
{
    public required string Operation { get; init; }

    public required int? ExitCode { get; init; }

    public required string Output { get; init; }

    public required bool TimedOut { get; init; }

    public required bool Success { get; init; }

    /// <summary>
    /// Parsed radio state for <c>radio-state</c> and <c>poweron</c>
    /// (<c>ON</c> / <c>OFF</c> / <c>UNKNOWN</c>), else <c>null</c>.
    /// </summary>
    public string? RadioState { get; init; }

    public override string ToString() =>
        $"{Operation}: {(Success ? "ok" : TimedOut ? "timed out" : "failed")}" +
        $" (exit {ExitCode?.ToString() ?? "?"})" +
        (RadioState is null ? "" : $", radio state {RadioState}");
}

/// <summary>
/// Cellular-module power and reset control from the root shell, through the
/// vendor's one-shot <c>/usr/bin/mipc_wan_cli</c> client.
/// </summary>
/// <remarks>
/// Three operations, in increasing order of force:
/// <list type="bullet">
/// <item><see cref="RadioStateAsync"/> — read-only.
/// <c>--nw_radio_state_get</c> (MIPC msg 1025) prints
/// <c>Get SW radio state:MIPC_NW_RADIO_STATE_ON|OFF|UNKNOWN</c>.</item>
/// <item><see cref="RadioCycleAsync"/> — the vendor's own recovery sequence
/// (<c>/etc/auto_recovery.sh</c>): <c>AT+CFUN=0</c> → pause →
/// <c>AT+CFUN=1</c> → <c>ubus send modem_ee</c>. The module stays powered;
/// cm's FSM re-camps and re-dials the data call (tens of seconds). The WAN
/// link is down while the radio is off.</item>
/// <item><see cref="PowerOffAsync"/> / <see cref="PowerOnAsync"/> —
/// <c>--modem_poweroff</c> (MIPC msg 1027) powers the module fully down
/// (client prints <c>Power off MD done</c>). <see cref="PowerOnAsync"/>
/// sends <c>--nw_radio_state_set 1</c> (MIPC msg 1027, state ON) to the
/// module-side MIPC daemon and then verifies with a state read.</item>
/// </list>
/// <b>Power-on caveat (why <c>PowerOnAsync</c> is best-effort).</b> The
/// SET-radio-state request only reaches a <i>live</i> module: after a full
/// power-off there is no MIPC peer on the CCCI line, so the request hangs
/// until the timeout. The host-side power-on that works on a dead module
/// (<c>libtrm_power_on_md</c>, kernel ioctl <c>0x437C</c> on a ccmni fd,
/// wrapped by <c>mipc_power_on</c> in <c>libmipc_msg</c>) is exposed by no
/// on-device CLI, and even the vendor's own wrapper is unusable on a dead
/// module: it needs an established MIPC session, and <c>mipc_init</c> polls
/// <c>/sys/kernel/ccci/boot</c> in an endless loop until the module reports
/// <c>md1:4</c>. Recovery from a full power-off on this firmware is therefore
/// a router reboot (or a small cross-compiled ioctl helper, see README §8) —
/// prefer <see cref="RadioCycleAsync"/> when the module still answers.
///
/// Every operation runs exactly one <c>mipc_wan_cli</c> process (plus the
/// <c>ubus</c> poke in the cycle) through the marker protocol.
/// </remarks>
public sealed class ModemPower
{
    private const string Cli = "/usr/bin/mipc_wan_cli";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OffPause = TimeSpan.FromSeconds(2);

    private static readonly Regex StateRegex = new(
        @"Get SW radio state:(\w+)", RegexOptions.Compiled);

    private readonly ICommandRunner _shell;

    public ModemPower(ICommandRunner shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
    }

    /// <summary>
    /// Reads the current (software) radio state. Read-only.
    /// </summary>
    public async Task<ModemPowerResult> RadioStateAsync(CancellationToken ct = default)
    {
        CommandResult result = await CliAsync("--nw_radio_state_get", ct).ConfigureAwait(false);
        string output = result.Output ?? "";
        Match match = StateRegex.Match(output);
        return new ModemPowerResult
        {
            Operation = "radio-state",
            ExitCode = result.ExitCode,
            Output = output,
            TimedOut = result.TimedOut,
            Success = !result.TimedOut && match.Success,
            RadioState = match.Success ? match.Groups[1].Value : null
        };
    }

    /// <summary>
    /// Soft reset — the vendor's radio cycle: <c>AT+CFUN=0</c>, a short pause,
    /// <c>AT+CFUN=1</c>, then <c>ubus send modem_ee</c> (the backend poke
    /// <c>/etc/auto_recovery.sh</c> performs; its failure is tolerated). The
    /// module stays powered; cm re-camps and re-dials on its own.
    /// </summary>
    /// <param name="offDuration">How long the radio stays off (default 2 s,
    /// the vendor's value).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ModemPowerResult> RadioCycleAsync(
        TimeSpan? offDuration = null,
        CancellationToken ct = default)
    {
        TimeSpan pause = offDuration ?? OffPause;
        if (pause < TimeSpan.FromSeconds(1))
            throw new ArgumentException("offDuration must be at least 1 s", nameof(offDuration));

        CommandResult off = await CliAsync("--at_cmd AT+CFUN=0", ct).ConfigureAwait(false);
        if (off.TimedOut || HasCmeError(off.Output))
            return new ModemPowerResult
            {
                Operation = "radio-cycle",
                ExitCode = off.ExitCode,
                Output = off.Output ?? "",
                TimedOut = off.TimedOut,
                Success = false
            };

        await Task.Delay(pause, ct).ConfigureAwait(false);

        CommandResult on = await CliAsync("--at_cmd AT+CFUN=1", ct).ConfigureAwait(false);
        if (on.TimedOut || HasCmeError(on.Output))
            return new ModemPowerResult
            {
                Operation = "radio-cycle",
                ExitCode = on.ExitCode,
                Output = (off.Output ?? "") + "\n---\n" + (on.Output ?? ""),
                TimedOut = on.TimedOut,
                Success = false
            };

        // Vendor's backend poke; must not break the cycle if ubus is absent.
        await _shell.RunAsync(
            "ubus send modem_ee 2>/dev/null || true", TimeSpan.FromSeconds(10), ct)
            .ConfigureAwait(false);

        return new ModemPowerResult
        {
            Operation = "radio-cycle",
            ExitCode = on.ExitCode,
            Output = (off.Output ?? "") + "\n---\n" + (on.Output ?? ""),
            TimedOut = false,
            Success = true
        };
    }

    /// <summary>
    /// Powers the module fully down (<c>--modem_poweroff</c>). Internet and
    /// SMS are gone until the module is powered on again — see
    /// <see cref="PowerOnAsync"/> for the caveat that a fully down module may
    /// not answer the power-on request (router reboot needed).
    /// </summary>
    public async Task<ModemPowerResult> PowerOffAsync(CancellationToken ct = default)
    {
        CommandResult result = await CliAsync("--modem_poweroff", ct).ConfigureAwait(false);
        string output = result.Output ?? "";
        return new ModemPowerResult
        {
            Operation = "poweroff",
            ExitCode = result.ExitCode,
            Output = output,
            TimedOut = result.TimedOut,
            Success = !result.TimedOut &&
                output.Contains("Power off MD done", StringComparison.Ordinal)
        };
    }

    /// <summary>
    /// Requests the module back on: <c>--nw_radio_state_set 1</c> (MIPC 1027,
    /// state ON), then verifies with <see cref="RadioStateAsync"/>.
    /// Best-effort by architecture: the request needs a live module-side MIPC
    /// peer, so on a fully powered-down module it times out and the result
    /// says so — see the class remarks.
    /// </summary>
    public async Task<ModemPowerResult> PowerOnAsync(CancellationToken ct = default)
    {
        CommandResult set = await CliAsync("--nw_radio_state_set 1", ct).ConfigureAwait(false);
        if (set.TimedOut)
            return new ModemPowerResult
            {
                Operation = "poweron",
                ExitCode = set.ExitCode,
                Output = set.Output ?? "",
                TimedOut = true,
                Success = false
            };

        ModemPowerResult state = await RadioStateAsync(ct).ConfigureAwait(false);
        bool ok = state.Success && state.RadioState == "MIPC_NW_RADIO_STATE_ON";
        return new ModemPowerResult
        {
            Operation = "poweron",
            ExitCode = set.ExitCode,
            Output = (set.Output ?? "") + "\n---\n" + state.Output,
            TimedOut = state.TimedOut,
            Success = ok,
            RadioState = state.RadioState
        };
    }

    /// <summary>
    /// One <c>mipc_wan_cli</c> round trip. A timeout means the module stopped
    /// answering (the vendor client blocks up to 300 s on a frozen module);
    /// the shell recovers itself in the background (Ctrl-C, then a drain), so
    /// the next command runs on a clean pty.
    /// </summary>
    private async Task<CommandResult> CliAsync(string args, CancellationToken ct)
    {
        CommandResult result = await _shell.RunAsync($"{Cli} {args}", DefaultTimeout, ct).ConfigureAwait(false);
        if (result.TimedOut)
            ShellHangRecovery.Schedule(_shell);
        return result;
    }

    private static bool HasCmeError(string? output) =>
        output?.Contains("+CME ERROR", StringComparison.Ordinal) == true;
}
