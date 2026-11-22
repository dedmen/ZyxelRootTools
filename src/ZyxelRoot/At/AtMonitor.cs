using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace ZyxelRoot;

/// <summary>Configuration for <see cref="AtMonitor"/>.</summary>
public sealed class AtMonitorOptions
{
    /// <summary>
    /// Log files to tail. <c>mmm.log</c> is the primary source (every MMM op and
    /// every vendor-plugin line); <c>wan.log</c> carries the <c>[WAN]</c> lines
    /// (SIM changes); <c>cm.log</c> carries the FSM/event log, including event
    /// 26 (SendSMS) and 27 (Atcmd) requests.
    /// </summary>
    public IReadOnlyList<string> Sources { get; set; } = new[]
    {
        "/var/log/mmm.log",
        "/var/log/wan.log",
        "/var/log/cm.log"
    };

    /// <summary>Lines of existing history to replay on start (0 = only new lines).</summary>
    public int HistoryLines { get; set; } = 20;

    /// <summary>Maximum number of lines read per source per poll.</summary>
    public int MaxLinesPerPoll { get; set; } = 500;

    /// <summary>Delay between poll rounds.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Capacity of the bounded record channel (oldest records are dropped).</summary>
    public int ChannelCapacity { get; set; } = 10_000;
}

/// <summary>
/// Live AT/modem log monitor over an existing <see cref="ICommandRunner"/>
/// (a live <see cref="RootShell"/> or a <see cref="PersistentRootShell"/>).
/// </summary>
/// <remarks>
/// <para>
/// The monitor polls <c>/var/log/mmm.log</c> (primary), <c>/var/log/wan.log</c> and
/// <c>/var/log/cm.log</c> with <c>tail -n +N</c>, parses the daemon log format into
/// <see cref="AtLogRecord"/> and publishes them to a channel.
/// </para>
/// <para>
/// <b>Observability limits.</b> On this platform the runtime AT path is
/// <c>cm → libmmm dispatcher → libvdr_mtk_module.so → MIPC msg 3 → libmipc_msg.so</c>;
/// there is no AT tty to tap (the channel plugin's <c>SendAtCmdFull</c> is a dead
/// stub and <c>libmipc_msg.so</c> is not available). AT text is therefore visible
/// only where a vendor handler logs it — in practice the periodic SMS poll
/// (<c>AT+CMGL=4</c>) and the SMS send path — while raw replies are only visible
/// as parsed field values (or not at all for the raw <c>Atcmd</c> op, whose
/// handler logs nothing). See README.md for the full table.
/// </para>
/// <para>The monitor does not own the shell; dispose the shell yourself.</para>
/// </remarks>
public sealed class AtMonitor : IAsyncDisposable
{
    private readonly ICommandRunner _shell;
    private readonly AtMonitorOptions _options;
    private readonly List<LogTailSource> _sources;
    private readonly Channel<AtLogRecord> _channel;
    private readonly CancellationTokenSource _cts = new();

    private Task? _loop;
    private bool _disposed;

    public AtMonitor(ICommandRunner shell, AtMonitorOptions? options = null)
    {
        _shell = shell;
        _options = options ?? new AtMonitorOptions();
        _sources = _options.Sources.Select(path => new LogTailSource(path)).ToList();
        _channel = Channel.CreateBounded<AtLogRecord>(new BoundedChannelOptions(Math.Max(16, _options.ChannelCapacity))
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
    }

    /// <summary>Parsed log records (bounded; oldest are dropped if the consumer stalls).</summary>
    public ChannelReader<AtLogRecord> Records => _channel.Reader;

    /// <summary>True once <see cref="StartAsync"/> has been called.</summary>
    public bool IsRunning => _loop is not null;

    /// <summary>Health snapshot of every tailed source.</summary>
    public IReadOnlyList<AtLogSourceStatus> GetStatus() => _sources.Select(s => s.GetStatus()).ToList();

    /// <summary>Starts the background polling loop. Idempotent.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_loop is not null) return;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        foreach (LogTailSource source in _sources)
        {
            try
            {
                await source.InitializeAsync(_shell, _options.HistoryLines, linked.Token).ConfigureAwait(false);
            }
            catch
            {
                // Status is recorded by the source; the loop retries it.
            }
        }

        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        int consecutiveFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            bool roundFailed = false;

            foreach (LogTailSource source in _sources)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    List<AtLogRecord> records = await source
                        .PollAsync(_shell, _options.MaxLinesPerPoll, ct)
                        .ConfigureAwait(false);

                    foreach (AtLogRecord record in records)
                        _channel.Writer.TryWrite(record);

                    if (source.LastPollFailed) roundFailed = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // A timeout or a closed shell: keep the loop alive, retry next round.
                    roundFailed = true;
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                }
            }

            // A dead shell must not spin forever: give up after a sustained outage.
            consecutiveFailures = roundFailed ? consecutiveFailures + 1 : 0;
            if (consecutiveFailures >= 10)
            {
                _channel.Writer.TryComplete(new ZyxelRootException("AT monitor stopped: the root shell stopped answering"));
                return;
            }

            try
            {
                await Task.Delay(_options.PollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Performs one poll round without touching the channel. Useful for tests and
    /// for callers that want to drive the monitor themselves.
    /// </summary>
    public async Task<IReadOnlyList<AtLogRecord>> PollOnceAsync(CancellationToken ct = default)
    {
        var all = new List<AtLogRecord>();

        foreach (LogTailSource source in _sources)
        {
            if (source.NextLine < 1)
                await source.InitializeAsync(_shell, _options.HistoryLines, ct).ConfigureAwait(false);

            all.AddRange(await source.PollAsync(_shell, _options.MaxLinesPerPoll, ct).ConfigureAwait(false));
        }

        return all;
    }

    /// <summary>Streams records as they are parsed.</summary>
    public async IAsyncEnumerable<AtLogRecord> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (AtLogRecord record in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return record;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _channel.Writer.TryComplete();

        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }

        _cts.Dispose();
    }
}
