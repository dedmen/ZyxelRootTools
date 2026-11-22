using System.Globalization;
using System.Net.NetworkInformation;

namespace ZyxelRoot;

/// <summary>
/// A persistent root shell for a long-lived monitoring process: keeps one
/// <see cref="RootShell"/> connection alive across daemon restarts and router
/// reboots, and raises <see cref="RouterRebooted"/> when a reconnect proves
/// the box was rebooted.
/// </summary>
/// <remarks>
/// When the transport drops, the maintain loop waits for the router's port to
/// accept again, re-bootstrapping the <c>telnetd</c> daemon through
/// <see cref="RootDaemonManager"/> whenever the port is not accepting (the
/// daemon lives in <c>/tmp</c> and dies with a reboot) — but only after a ping
/// proves the router is reachable; while the router (or the local LAN) is
/// unreachable it just waits, since the web-login bootstrap could not reach it
/// either. After every
/// successful reconnect it reads <c>/proc/uptime</c> through the marker
/// protocol and applies the pure rule from <see cref="RebootDetection"/>:
/// without a reboot the router's uptime can never be less than the time
/// elapsed since the original connection, so
/// <c>uptime &lt; elapsed − slack</c> means the box restarted in between. A
/// transient drop on a long-running box never satisfies that, so the event
/// cannot fire on a LAN hiccup.
/// </remarks>
public sealed class PersistentRootShell : ICommandRunner, IInterruptibleShell, IAsyncDisposable
{
    private readonly ZyxelSettings _settings;
    private readonly Action<string>? _log;
    private readonly TimeSpan _reconnectDelay;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _runLock = new(1, 1);

    private RootShell? _shell;
    private DateTime _firstConnectUtc;
    private int _reconnects;
    private Task? _maintain;

    public PersistentRootShell(ZyxelSettings settings, Action<string>? log = null, TimeSpan? reconnectDelay = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log;
        _reconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// Raised exactly once per drop→reconnect cycle in which the router's
    /// uptime proves it rebooted. A plain reconnect without a reboot is
    /// silent. The handler runs on the maintain loop after a live shell is
    /// available: call <see cref="RunAsync"/> from it (e.g. to re-register a
    /// cron job).
    /// </summary>
    public event EventHandler<RouterRebootEventArgs>? RouterRebooted;

    /// <summary>Successful reconnects since the original connect.</summary>
    public int ReconnectCount => Volatile.Read(ref _reconnects);

    /// <summary>True while a live shell is attached.</summary>
    public bool IsConnected => _shell is { } shell && !shell.IsClosed;

    /// <summary>
    /// Connects (bootstrapping the daemon when the port is not accepting) and
    /// starts the maintain loop. Call this once; <see cref="RunAsync"/> then
    /// always proxies the live shell.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_shell is not null)
            throw new InvalidOperationException("already connected");

        string? validation = _settings.Validate();
        if (validation is not null)
            throw new ZyxelRootException(validation);

        _shell = await EstablishAsync(ct).ConfigureAwait(false);
        _firstConnectUtc = DateTime.UtcNow;
        _maintain = Task.Run(MaintainAsync, CancellationToken.None);
    }

    /// <summary>
    /// Runs a single-line command on the live shell, waiting out any in-flight
    /// reconnect. Throws <see cref="ZyxelRootException"/> when the shell is
    /// found closed in the gap (retry).
    /// </summary>
    public async Task<CommandResult> RunAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        RootShell shell;
        await _runLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            shell = _shell ?? throw new ZyxelRootException("not connected");
            if (shell.IsClosed)
                throw new ZyxelRootException("shell closed during a reconnect; retry");
        }
        finally
        {
            _runLock.Release();
        }

        return await shell.RunAsync(command, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a Ctrl-C to the live shell (interrupts a stuck foreground
    /// command). A no-op while disconnected or mid-reconnect.
    /// </summary>
    public async Task SendBreakAsync(CancellationToken ct = default)
    {
        if (!await _runLock.WaitAsync(1000, ct).ConfigureAwait(false))
            return; // a reconnect owns the shell right now — nothing to interrupt

        try
        {
            if (_shell is { } shell && !shell.IsClosed)
                await shell.SendBreakAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _runLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _lifetime.Cancel();

        Task? maintain = _maintain;
        RootShell? shell = _shell;
        try { shell?.Dispose(); } catch { }
        try { maintain?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        _lifetime.Dispose();
        return ValueTask.CompletedTask;
    }

    //
    // Maintain loop
    //

    private async Task MaintainAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                RootShell current = _shell ?? throw new InvalidOperationException("not connected");

                await WaitForCloseAsync(current).ConfigureAwait(false);
                if (_lifetime.IsCancellationRequested) break;

                DateTime droppedAt = DateTime.UtcNow;
                _log?.Invoke($"connection dropped at {droppedAt:O}; waiting for the router");

                bool daemonRecreated = false;
                string? bootstrapPath = null;
                double? uptime = null;

                await _runLock.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    try { current.Dispose(); } catch { }
                    _shell = null;

                    int notAcceptingStreak = 0;
                    int unreachableStreak = 0;
                    while (!_lifetime.IsCancellationRequested && _shell is null)
                    {
                        bool accepting = await RootShell.ProbeAsync(
                            _settings.Host, _settings.RootPort, TimeSpan.FromSeconds(2), _lifetime.Token)
                            .ConfigureAwait(false);

                        if (accepting)
                        {
                            notAcceptingStreak = 0;
                            unreachableStreak = 0;
                            try
                            {
                                _shell = await RootShell.ConnectAsync(
                                    _settings.Host, _settings.RootPort, TimeSpan.FromSeconds(10), _lifetime.Token)
                                    .ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (ZyxelRootException ex)
                            {
                                _log?.Invoke($"reconnect failed: {ex.Message}");
                            }
                        }
                        else
                        {
                            // A bootstrapped daemon can still die right after
                            // launch: after 3 straight refusals allow another
                            // bootstrap round (the daemon script is idempotent).
                            if (++notAcceptingStreak >= 3)
                                daemonRecreated = false;
                        }

                        if (!accepting && !daemonRecreated)
                        {
                            // A port refusal does not say WHY: the daemon may be
                            // dead (router up) or the whole router/LAN may be
                            // unreachable (e.g. our own interface restarted —
                            // live 2026-11-21). Bootstrap is a web login, which
                            // is equally unreachable, so ping first to tell the
                            // two apart instead of spamming login attempts.
                            bool reachable = RouterReachable(_settings.Host);
                            unreachableStreak = reachable ? 0 : unreachableStreak + 1;

                            if (!reachable && unreachableStreak < 6)
                            {
                                // ~1 minute at the 10 s reconnect delay: just
                                // wait for the router to come back.
                                if (unreachableStreak == 1 || unreachableStreak % 6 == 0)
                                    _log?.Invoke($"port not accepting and no ping reply to {_settings.Host} — router unreachable; waiting without bootstrap (probe {unreachableStreak})");
                            }
                            else
                            {
                                // Router reachable (daemon dead) — or no ping
                                // reply for a full minute: ICMP might be
                                // filtered while the daemon is dead, so try
                                // anyway; a web login to a truly down box
                                // fails fast.
                                _log?.Invoke("port not accepting; bootstrapping the root daemon");
                                try
                                {
                                    var manager = new RootDaemonManager(_settings, _log);
                                    RootDaemonReport report = await manager.StartAsync(_lifetime.Token).ConfigureAwait(false);
                                    if (report.Succeeded)
                                    {
                                        daemonRecreated = true;
                                        bootstrapPath = report.Path;
                                        _log?.Invoke($"daemon recreated via '{report.Path}'");
                                    }
                                    else
                                    {
                                        _log?.Invoke($"bootstrap failed: {report.Message}");
                                    }
                                }
                                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                                {
                                    throw;
                                }
                                catch (Exception ex)
                                {
                                    _log?.Invoke($"bootstrap failed: {ex.Message}");
                                }
                            }
                        }

                        if (_shell is null && !_lifetime.IsCancellationRequested)
                            await Task.Delay(_reconnectDelay, _lifetime.Token).ConfigureAwait(false);
                    }

                    if (_shell is not null)
                    {
                        uptime = await ReadUptimeAsync(_shell).ConfigureAwait(false);
                        _reconnects = Interlocked.Increment(ref _reconnects);
                        _log?.Invoke($"reconnected ({_reconnects}) after {(DateTime.UtcNow - droppedAt).TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}s" +
                                      (uptime is null ? " (uptime unreadable)" : $" (uptime {uptime.Value.ToString("0.0", CultureInfo.InvariantCulture)}s)"));
                    }
                }
                finally
                {
                    _runLock.Release();
                }

                if (_lifetime.IsCancellationRequested || _shell is null) break;

                TimeSpan elapsed = DateTime.UtcNow - _firstConnectUtc;
                TimeSpan disconnectedFor = DateTime.UtcNow - droppedAt;

                // Fire outside the run lock: handlers may call RunAsync.
                if (uptime is double up && RebootDetection.RebootDetected(elapsed, up))
                {
                    _firstConnectUtc = DateTime.UtcNow;
                    _log?.Invoke("router reboot detected (uptime < elapsed since first connect - slack)");
                    RouterRebooted?.Invoke(this,
                        new RouterRebootEventArgs(up, disconnectedFor, elapsed, daemonRecreated, bootstrapPath));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // disposed
        }
        catch (Exception ex)
        {
            _log?.Invoke($"maintain loop stopped: {ex.Message}");
        }
    }

    /// <summary>Initial connect: bootstrap the daemon only when the port refuses.</summary>
    private async Task<RootShell> EstablishAsync(CancellationToken ct)
    {
        bool accepting = await RootShell.ProbeAsync(
            _settings.Host, _settings.RootPort, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);

        if (!accepting)
        {
            // Same discrimination as the maintain loop: a web-login bootstrap
            // is pointless while the router itself cannot be reached.
            if (!RouterReachable(_settings.Host))
                throw new ZyxelRootException(
                    $"root port {_settings.RootPort} not accepting and no ping reply to {_settings.Host} — router unreachable");

            _log?.Invoke("port not accepting; bootstrapping the root daemon");
            var manager = new RootDaemonManager(_settings, _log);
            RootDaemonReport report = await manager.StartAsync(ct).ConfigureAwait(false);
            if (!report.Succeeded)
                throw new ZyxelRootException($"root daemon bootstrap failed: {report.Message}");
            _log?.Invoke($"daemon started via '{report.Path}'");
        }

        return await RootShell.ConnectAsync(
            _settings.Host, _settings.RootPort, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One ICMP ping (1.2 s timeout) to tell "daemon dead, router up" from
    /// "router/LAN unreachable" when the root port refuses: the former needs
    /// a bootstrap, the latter just needs waiting (a web-login bootstrap can
    /// not reach a box it cannot even ping). Live 2026-11-21: a local
    /// interface restart put the maintain loop into bootstrap spam.
    /// </summary>
    private static bool RouterReachable(string host)
    {
        try
        {
            using var pinger = new Ping();
            return pinger.Send(host, 1200).Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    private async Task WaitForCloseAsync(RootShell shell)
    {
        // Polling (not the Lines channel): the channel is the caller's stream.
        // The socket itself only errors when the peer resets (daemon kill —
        // immediate) or after the ~15 minute TCP retransmission timeout (dark
        // reboot), so every 10 s the port is also probed out-of-band: a dead
        // router stops answering new connections, a dead daemon refuses them.
        int ticks = 0;
        while (!_lifetime.IsCancellationRequested && !shell.IsClosed)
        {
            try
            {
                await Task.Delay(1000, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (++ticks < 10) continue;
            ticks = 0;

            bool port = await RootShell.ProbeAsync(
                _settings.Host, _settings.RootPort, TimeSpan.FromSeconds(2), _lifetime.Token)
                .ConfigureAwait(false);

            if (!port)
            {
                _log?.Invoke("port stopped answering while the socket was open; treating as drop");
                shell.Dispose();
                return;
            }
        }
    }

    private static async Task<double?> ReadUptimeAsync(RootShell shell)
    {
        try
        {
            CommandResult result = await shell.RunAsync("cat /proc/uptime", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (result.TimedOut || !result.Success) return null;

            string first = result.Output.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "";
            return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                ? seconds
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
