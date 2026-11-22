namespace ZyxelRoot;

/// <summary>Data-connection state of one APN on the router.</summary>
public enum DataState
{
    /// <summary>Not observed yet (before the first successful poll).</summary>
    Unknown,

    /// <summary>No data call on the APN (<c>apnConnStatus:0</c>). Covers every
    /// cause — a same-account kick, an <c>OnHold</c> back-off, loss of signal,
    /// a reboot — because the state is polled from the modem's own status.</summary>
    Disconnected,

    /// <summary>Data call active on the APN (<c>apnConnStatus:1</c>).</summary>
    Connected
}

/// <summary>Raised by <see cref="DataStateMonitor"/> when the polled data
/// connection state changes (including the first observation, whose previous
/// state is <see cref="DataState.Unknown"/>).</summary>
public sealed class DataStateEventArgs : EventArgs
{
    /// <summary>The new state.</summary>
    public required DataState State { get; init; }

    /// <summary>The state before the change.</summary>
    public required DataState Previous { get; init; }

    /// <summary>When the change was observed (local time).</summary>
    public required DateTime At { get; init; }

    /// <summary>The data interface name when connected (e.g. <c>ccmni2</c>),
    /// otherwise <c>null</c>.</summary>
    public string? Interface { get; init; }

    /// <summary>The <c>apnConnStatus</c> line as read from the device.</summary>
    public required string Detail { get; init; }
}

/// <summary>
/// Polls the data-connection state of one APN and fires
/// <see cref="StateChanged"/> on every transition — whatever the cause:
/// a same-account kick, the APN back-off hold, signal loss, a reboot. The
/// state is the modem's own <c>apnConnStatus</c> from
/// <c>cmctl get apnInfo &lt;n&gt;</c>, the same status cm itself uses.
/// </summary>
/// <remarks>
/// Built on <see cref="ICommandRunner"/>: pass a
/// <see cref="PersistentRootShell"/> and the monitor keeps working across
/// daemon restarts and router reboots with no rebuild (polls issued during a
/// reconnect simply wait for the live channel). A failed or timed-out poll
/// never changes the state — the previous reading stands until a good poll
/// proves otherwise, so a dropped shell can never look like a data
/// disconnect.
/// </remarks>
public sealed class DataStateMonitor : IAsyncDisposable
{
    private static readonly TimeSpan CmdTimeout = TimeSpan.FromSeconds(15);

    private readonly ICommandRunner _runner;
    private readonly int _apnNumber;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    private DataState _current = DataState.Unknown;
    private string? _interface;

    /// <param name="runner">Command channel (live <see cref="RootShell"/> or
    /// <see cref="PersistentRootShell"/>).</param>
    /// <param name="pollInterval">Poll period (default 2 s, minimum 1 s).</param>
    /// <param name="apnNumber">Which APN to watch (default 1 — the data APN).</param>
    public DataStateMonitor(ICommandRunner runner, TimeSpan? pollInterval = null, int apnNumber = 1)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _apnNumber = apnNumber;
        _pollInterval = pollInterval is null
            ? TimeSpan.FromSeconds(2)
            : pollInterval.Value < TimeSpan.FromSeconds(1)
                ? TimeSpan.FromSeconds(1)
                : pollInterval.Value;
    }

    /// <summary>The last observed state.</summary>
    public DataState Current => _current;

    /// <summary>The data interface of the last connected reading (e.g.
    /// <c>ccmni2</c>), or <c>null</c> while disconnected / unknown.</summary>
    public string? CurrentInterface => _current == DataState.Connected ? _interface : null;

    /// <summary>Fired on every state transition, including the first
    /// observation (previous state <see cref="DataState.Unknown"/>). Not
    /// fired when a poll simply fails.</summary>
    public event EventHandler<DataStateEventArgs>? StateChanged;

    /// <summary>
    /// Reads the initial state and starts the poll loop. The first
    /// successful read is fired as the first <see cref="StateChanged"/> event
    /// (previous = <see cref="DataState.Unknown"/>).
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_loop is not null)
            throw new InvalidOperationException("already started");

        await ReadStateAsync(ct).ConfigureAwait(false);
        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
    }

    /// <summary>
    /// One-shot state read (also used by <see cref="StartAsync"/> and the
    /// loop). Returns the state the device reports; a failed read leaves the
    /// previous state in place and returns that.
    /// </summary>
    public async Task<DataState> ReadStateAsync(CancellationToken ct = default)
    {
        try
        {
            CommandResult r = await _runner.RunAsync(
                $"cmctl get apnInfo {_apnNumber} 2>/dev/null", CmdTimeout, ct).ConfigureAwait(false);

            if (!r.TimedOut && !string.IsNullOrWhiteSpace(r.Output))
            {
                foreach (string raw in r.Output.Split('\n'))
                {
                    string line = raw.Trim();
                    if (!line.StartsWith("apnConnStatus", StringComparison.Ordinal))
                        continue;

                    int i = line.IndexOf(':');
                    if (i < 0 || !int.TryParse(line[(i + 1)..].Trim(), out int status))
                        break;

                    DataState state = status == 1 ? DataState.Connected : DataState.Disconnected;
                    RaiseIfChanged(state,
                        state == DataState.Connected ? IfaceOf(r.Output) : null,
                        line);
                    return state;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // a failed read must never change the state or crash the loop
        }

        return _current;
    }

    /// <summary>Stops the poll loop (the current state is retained).</summary>
    public async Task StopAsync()
    {
        if (_loop is null) return;
        _cts.Cancel();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }
        _loop = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private void RaiseIfChanged(DataState state, string? iface, string detail)
    {
        DataState previous = _current;
        if (state == previous)
            return;

        _current = state;
        _interface = state == DataState.Connected ? iface : null;

        StateChanged?.Invoke(this, new DataStateEventArgs
        {
            State = state,
            Previous = previous,
            At = DateTime.Now,
            Interface = state == DataState.Connected ? _interface : null,
            Detail = detail,
        });
    }

    /// <summary>The <c>ifName</c> value from an <c>apnInfo</c> dump.</summary>
    private static string? IfaceOf(string apnInfoOutput)
    {
        foreach (string raw in apnInfoOutput.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("ifName", StringComparison.Ordinal))
            {
                int i = line.IndexOf(':');
                return i >= 0 ? line[(i + 1)..].Trim() : null;
            }
        }

        return null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
                await ReadStateAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            // the loop must never die silently
        }
    }
}
