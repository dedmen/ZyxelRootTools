namespace ZyxelRoot.Led;

/// <summary>
/// Panel state of one <c>/sys/class/leds/&lt;name&gt;/</c> node.
/// </summary>
public sealed class LedDeviceState
{
    /// <summary>Sysfs node name (e.g. <c>internet:green</c>).</summary>
    public required string Name { get; init; }

    /// <summary>Raw <c>trigger</c> line (the active one in brackets, e.g.
    /// <c>[none] timer …</c>).</summary>
    public required string Trigger { get; init; }

    /// <summary><c>brightness</c> (0 = dark; the panel is brightness-driven,
    /// all live triggers sit at <c>none</c>).</summary>
    public int Brightness { get; init; }

    public bool IsOn => Brightness > 0;
}

/// <summary>
/// One read-only snapshot of the front panel: every LED node plus the pids
/// of the daemons that write it.
/// </summary>
public sealed class LedStatus
{
    /// <summary>All <c>/sys/class/leds/*</c> nodes, sysfs order.</summary>
    public required IReadOnlyList<LedDeviceState> Devices { get; init; }

    /// <summary>Pid of <c>zycmled</c> (the 10 s signal/SMS state writer),
    /// <c>null</c> when not running.</summary>
    public int? ZycmledPid { get; init; }

    /// <summary>Pid of <c>esmd</c> (WAN/Wi-Fi event writer),
    /// <c>null</c> when not running.</summary>
    public int? EsmdPid { get; init; }

    /// <summary>Pid of <c>zybtnchk</c> (button daemon; writes no LEDs),
    /// <c>null</c> when not running.</summary>
    public int? ZybtnchkPid { get; init; }

    /// <summary>
    /// <c>true</c> when the kernel knob
    /// <c>/sys/kernel/zyinetled/mode</c> exists on this build.
    /// On the verified FWA505 FW V1.18(ACKO.1)C0 it does **not** — the
    /// driver object is absent, so the only control surface is
    /// <c>zyledctl</c> (plus stopping the writer daemons).
    /// </summary>
    public bool KernelModeAvailable { get; init; }

    /// <summary>Kernel knob value (<c>on</c>/<c>off</c>) when available.</summary>
    public string? KernelMode { get; init; }
}

/// <summary>Result of one <c>ZyxelLed</c> operation.</summary>
public sealed class LedActionResult
{
    public required string Operation { get; init; }

    public required int? ExitCode { get; init; }

    public required string Output { get; init; }

    public required bool TimedOut { get; init; }

    public required bool Success { get; init; }

    /// <summary>Free-form detail (e.g. which writer pids a
    /// <c>TurnOffAsync(stopWriters: true)</c> killed).</summary>
    public string? Detail { get; init; }

    public override string ToString() =>
        $"{Operation}: {(Success ? "ok" : TimedOut ? "timed out" : "failed")}" +
        $" (exit {ExitCode?.ToString() ?? "?"})" +
        (Detail is null ? "" : $" — {Detail}");
}

/// <summary>Panel LED actions (the <c>zyledctl</c> action list,
/// live-verified from the tool's own usage output).</summary>
public enum LedAction
{
    On,
    Off,
    Slow,
    Medium,
    Fast,
    Superfast,
    Upgrade
}

/// <summary>
/// Front-panel LED control through the vendor's <c>/bin/zyledctl</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Live-verified on FWA505 FW V1.18(ACKO.1)C0 (2026-11-20) — the device
/// does NOT match the offline research notes:</b> the kernel kill-switch
/// <c>/sys/kernel/zyinetled/mode</c> does not exist on the running system
/// (see <see cref="LedStatus.KernelModeAvailable"/>), there is no
/// <c>service</c> applet and no <c>/etc/init.d/led_ctrld.init</c>, so
/// <c>zycmled</c>/<c>esmd</c> are plain <c>&amp;</c>-children of
/// <c>zcmd</c> with no procd respawn. The only control surface is
/// <c>zyledctl</c>.
/// </para>
/// <para>
/// Logical LED names (the tool's usage list): POWER_R/B/G, SIG_R/B/G,
/// INTERNET_R/B/G, SYS_R/B/G, WPS, WIFI, VOICE_G, SMS_G, FW_UPGRADE, RTD,
/// plus the undocumented-but-verified group alias <c>all</c>. A color name
/// (e.g. INTERNET_G) forces its two siblings off first. On this device the
/// POWER_*/VOICE_G/SMS_G/WPS logical LEDs have no sysfs node, so
/// <c>zyledctl</c> prints <c>trigger/open: No such file or directory</c>
/// noise for them while still exiting 0 — harmless.
/// </para>
/// <para>
/// <b>Turning the panel off and keeping it off</b> =
/// <see cref="TurnOffAsync"/>: <c>RTD off</c> drives every
/// <c>zyledctl</c>-mapped node to 0, and the state writers (<c>zycmled</c>
/// 10 s poll, <c>esmd</c> event-driven) only re-assert on a <i>state
/// change</i>, so the off state holds with them still running (verified:
/// 30+ s hold, writers alive). Pass <c>stopWriters: true</c> to also kill
/// <c>zycmled</c>/<c>esmd</c> — note that killing <c>esmd</c> suspends WAN
/// link-state tracking and PPP connect/disconnect handling, and
/// <c>zcmd</c> (unkillable) re-lights INTERNET_G on every WAN reconnect, so
/// without a kernel kill-switch the kill is belt-and-braces, not a
/// guarantee. <see cref="RestoreAsync"/> reverses it: restart any dead
/// writer and <c>all on</c>, after which the daemons re-assert the correct
/// state within ~10 s (verified).
/// </para>
/// <para>
/// <b>Watch out for FW_UPGRADE:</b> <c>zyledctl FW_UPGRADE on</c> also
/// touches <c>/tmp/disable_led_action</c>, which freezes every other LED
/// action until the marker is removed — use it only during an actual
/// firmware upgrade.
/// </para>
/// </remarks>
public sealed class ZyxelLed
{
    private const string Cli = "/bin/zyledctl";

    /// <summary>Logical LED names accepted by <see cref="SetLedAsync"/>:
    /// the live usage list plus the verified <c>all</c> alias.
    /// <see cref="AllOnAsync"/> / <see cref="AllOffAsync"/> are the
    /// intended group operations.</summary>
    public static readonly IReadOnlySet<string> KnownNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "POWER_R", "POWER_B", "POWER_G",
        "SIG_R", "SIG_B", "SIG_G",
        "INTERNET_R", "INTERNET_B", "INTERNET_G",
        "SYS_R", "SYS_B", "SYS_G",
        "WPS", "WIFI", "VOICE_G", "SMS_G", "FW_UPGRADE", "RTD",
        "all"
    };

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(30);

    private readonly RootShell _shell;

    public ZyxelLed(RootShell shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
    }

    /// <summary>
    /// Read-only snapshot of the panel and its writer daemons.
    /// </summary>
    public async Task<LedStatus> StatusAsync(CancellationToken ct = default)
    {
        const string command =
            "echo \"MODE=[$(cat /sys/kernel/zyinetled/mode 2>/dev/null)]\"; " +
            "echo \"PID zycmled=$(pidof zycmled) esmd=$(pidof esmd) zybtnchk=$(pidof zybtnchk)\"; " +
            "for d in /sys/class/leds/*; do n=${d##*/}; " +
            "echo \"LED $n $(cat $d/brightness 2>/dev/null) $(cat $d/trigger 2>/dev/null)\"; done";

        CommandResult result = await _shell.RunAsync(command, StatusTimeout, ct).ConfigureAwait(false);
        if (result.TimedOut)
            throw new ZyxelRootException("LED status read timed out");

        var devices = new List<LedDeviceState>();
        bool kernelModeAvailable = false;
        string? kernelMode = null;
        int? zycmled = null, esmd = null, zybtnchk = null;

        foreach (string rawLine in (result.Output ?? "").Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("MODE=[", StringComparison.Ordinal) && line.EndsWith(']'))
            {
                string value = line[6..^1];
                kernelModeAvailable = value.Length > 0;
                kernelMode = value.Length > 0 ? value : null;
            }
            else if (line.StartsWith("PID ", StringComparison.Ordinal))
            {
                foreach (string field in line[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = field.IndexOf('=');
                    if (eq > 0 && eq < field.Length - 1 && int.TryParse(field[(eq + 1)..], out int pid))
                    {
                        switch (field[..eq])
                        {
                            case "zycmled": zycmled = pid; break;
                            case "esmd": esmd = pid; break;
                            case "zybtnchk": zybtnchk = pid; break;
                        }
                    }
                }
            }
            else if (line.StartsWith("LED ", StringComparison.Ordinal))
            {
                string[] f = line[4..].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length >= 2 && int.TryParse(f[1], out int brightness))
                    devices.Add(new LedDeviceState
                    {
                        Name = f[0],
                        Brightness = brightness,
                        Trigger = f.Length > 2 ? f[2] : ""
                    });
            }
        }

        return new LedStatus
        {
            Devices = devices,
            ZycmledPid = zycmled,
            EsmdPid = esmd,
            ZybtnchkPid = zybtnchk,
            KernelModeAvailable = kernelModeAvailable,
            KernelMode = kernelMode
        };
    }

    /// <summary>
    /// Sets one logical LED (<see cref="KnownNames"/>), e.g.
    /// <c>(\"SIG_G\", LedAction.Off)</c>. Runs <c>zyledctl &lt;name&gt;
    /// &lt;action&gt;</c>; success is the tool's exit code 0 (it exits 0 even
    /// for logical LEDs without a sysfs node on this build — see the class
    /// remarks).
    /// </summary>
    public Task<LedActionResult> SetLedAsync(string name, LedAction action, CancellationToken ct = default)
    {
        if (!KnownNames.Contains(name))
            throw new ArgumentException(
                $"unknown LED name '{name}' (known: {string.Join(", ", KnownNames.Order())})",
                nameof(name));
        return RunZyledctlAsync($"{name} {ActionToken(action)}", $"led:{name} {ActionToken(action)}", ct);
    }

    /// <summary>
    /// Lights every group LED on (<c>zyledctl all on</c>). This is the
    /// "return to normal" command: the writer daemons re-assert the correct
    /// per-state colors within ~10 s (verified live).
    /// </summary>
    public Task<LedActionResult> AllOnAsync(CancellationToken ct = default) =>
        RunZyledctlAsync("all on", "all-on", ct);

    /// <summary>
    /// Drives every <c>zyledctl</c>-mapped LED to dark
    /// (<c>zyledctl RTD off</c> — verified: all mapped sysfs nodes go to 0).
    /// Nodes outside the tool's table (<c>lcd-backlight</c>,
    /// <c>led9515/9516/9517</c>) are untouched.
    /// </summary>
    public Task<LedActionResult> AllOffAsync(CancellationToken ct = default) =>
        RunZyledctlAsync("RTD off", "all-off", ct);

    /// <summary>
    /// Turns the whole panel off and keeps it off:
    /// <see cref="AllOffAsync"/> first, then — when
    /// <paramref name="stopWriters"/> — kills <c>zycmled</c> and
    /// <c>esmd</c> so no state writer can re-light anything.
    /// </summary>
    /// <remarks>
    /// With the writers left running the off state holds as long as the
    /// underlying states don't change (verified 30+ s, both daemons alive);
    /// <c>zcmd</c> (unkillable) still re-lights INTERNET_G on a WAN
    /// reconnect, and this firmware has no kernel kill-switch to absorb
    /// that — see the class remarks. <see cref="RestoreAsync"/> reverses
    /// both steps.
    /// </remarks>
    public async Task<LedActionResult> TurnOffAsync(
        bool stopWriters = false,
        CancellationToken ct = default)
    {
        LedActionResult allOff = await AllOffAsync(ct).ConfigureAwait(false);
        if (!allOff.Success)
            return allOff;

        if (!stopWriters)
            return new LedActionResult
            {
                Operation = "turn-off",
                ExitCode = allOff.ExitCode,
                Output = allOff.Output,
                TimedOut = false,
                Success = true,
                Detail = "writers left running (off holds until a state change)"
            };

        const string command =
            "p1=$(pidof zycmled); p2=$(pidof esmd); " +
            "[ -n \"$p1\" ] && kill $p1 2>/dev/null; " +
            "[ -n \"$p2\" ] && kill $p2 2>/dev/null; " +
            "echo \"killed zycmled=$p1 esmd=$p2\"";

        CommandResult kill = await _shell.RunAsync(command, DefaultTimeout, ct).ConfigureAwait(false);
        string killOutput = kill.Output ?? "";
        bool ok = !kill.TimedOut && kill.ExitCode == 0;
        return new LedActionResult
        {
            Operation = "turn-off",
            ExitCode = kill.ExitCode,
            Output = allOff.Output + "\n" + killOutput,
            TimedOut = kill.TimedOut,
            Success = ok,
            Detail = ok
                ? "zycmled + esmd killed (WAN link-state tracking suspended until RestoreAsync)"
                : "writer kill failed"
        };
    }

    /// <summary>
    /// Restores automatic LED control, reversing
    /// <see cref="TurnOffAsync"/> with <c>stopWriters: true</c>: restarts
    /// <c>zycmled</c>/<c>esmd</c> when they are dead (they are
    /// <c>&amp;</c>-children of <c>zcmd</c> with no respawner, so a killed
    /// instance stays dead until reboot), then <see cref="AllOnAsync"/>.
    /// The daemons re-assert the correct state within ~10 s.
    /// </summary>
    public async Task<LedActionResult> RestoreAsync(CancellationToken ct = default)
    {
        const string command =
            "command -v zycmled >/dev/null 2>&1 && ! pidof zycmled 2>/dev/null && (zycmled &); " +
            "command -v esmd >/dev/null 2>&1 && ! pidof esmd 2>/dev/null && (esmd &); " +
            "sleep 1; echo \"now zycmled=$(pidof zycmled) esmd=$(pidof esmd)\"";

        CommandResult restart = await _shell.RunAsync(command, DefaultTimeout, ct).ConfigureAwait(false);
        LedActionResult allOn = await AllOnAsync(ct).ConfigureAwait(false);

        string restartOutput = restart.Output ?? "";
        bool ok = !restart.TimedOut && restart.ExitCode == 0 && allOn.Success;
        return new LedActionResult
        {
            Operation = "restore",
            ExitCode = allOn.ExitCode,
            Output = restartOutput + "\n" + allOn.Output,
            TimedOut = restart.TimedOut || allOn.TimedOut,
            Success = ok,
            Detail = ok ? "writers running, all on (daemons re-assert state ≤10 s)" : "restore incomplete"
        };
    }

    private Task<LedActionResult> RunZyledctlAsync(string args, string operation, CancellationToken ct) =>
        RunOneAsync($"{Cli} {args}", operation, ct);

    private async Task<LedActionResult> RunOneAsync(string command, string operation, CancellationToken ct)
    {
        CommandResult result = await _shell.RunAsync(command, DefaultTimeout, ct).ConfigureAwait(false);
        string output = result.Output ?? "";
        return new LedActionResult
        {
            Operation = operation,
            ExitCode = result.ExitCode,
            Output = output,
            TimedOut = result.TimedOut,
            Success = !result.TimedOut && result.ExitCode == 0
        };
    }

    private static string ActionToken(LedAction action) => action switch
    {
        LedAction.On => "on",
        LedAction.Off => "off",
        LedAction.Slow => "slow",
        LedAction.Medium => "medium",
        LedAction.Fast => "fast",
        LedAction.Superfast => "superfast",
        LedAction.Upgrade => "upgrade",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
    };
}
