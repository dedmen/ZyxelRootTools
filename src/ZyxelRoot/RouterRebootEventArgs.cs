namespace ZyxelRoot;

/// <summary>
/// Facts about a detected router reboot, raised with
/// <see cref="PersistentRootShell.RouterRebooted"/>.
/// </summary>
public sealed class RouterRebootEventArgs : EventArgs
{
    public RouterRebootEventArgs(
        double observedUptimeSeconds,
        TimeSpan disconnectedFor,
        TimeSpan elapsedSinceFirstConnect,
        bool daemonRecreated,
        string? bootstrapPath)
    {
        ObservedUptimeSeconds = observedUptimeSeconds;
        DisconnectedFor = disconnectedFor;
        ElapsedSinceFirstConnect = elapsedSinceFirstConnect;
        DaemonRecreated = daemonRecreated;
        BootstrapPath = bootstrapPath;
    }

    /// <summary>Router uptime in seconds, read from <c>/proc/uptime</c> right after the reconnect.</summary>
    public double ObservedUptimeSeconds { get; }

    /// <summary>How long the persistent connection was down.</summary>
    public TimeSpan DisconnectedFor { get; }

    /// <summary>Time since the original connection (measured before this cycle).</summary>
    public TimeSpan ElapsedSinceFirstConnect { get; }

    /// <summary>
    /// True when the reconnect required (re-)creating the daemon from scratch
    /// (the port was refusing connections). The daemon only disappears on a
    /// reboot or a manual kill — corroborating evidence, not the decision
    /// itself (the uptime rule alone is sufficient).
    /// </summary>
    public bool DaemonRecreated { get; }

    /// <summary>Bootstrap path that recreated the daemon (<c>debug</c> or <c>web</c>), if any.</summary>
    public string? BootstrapPath { get; }
}

/// <summary>
/// The reboot rule used by <see cref="PersistentRootShell"/> — pure, so it can
/// be tested without a router.
/// </summary>
/// <remarks>
/// Without a reboot, the router's uptime is always at least the time elapsed
/// since the original connection (both tick at the same rate), so
/// <c>uptime &lt; elapsed − slack</c> is only true when the box restarted in
/// between. A 10 s LAN hiccup on a box up for days: uptime is days,
/// elapsed is seconds — never fires.
/// </remarks>
public static class RebootDetection
{
    /// <summary>Default slack in seconds, absorbing clock/reading jitter.</summary>
    public const double DefaultSlackSeconds = 5;

    /// <summary>True when a reconnect proves the router rebooted.</summary>
    public static bool RebootDetected(
        TimeSpan elapsedSinceFirstConnect,
        double observedUptimeSeconds,
        double slackSeconds = DefaultSlackSeconds)
        => observedUptimeSeconds < elapsedSinceFirstConnect.TotalSeconds - slackSeconds;
}
