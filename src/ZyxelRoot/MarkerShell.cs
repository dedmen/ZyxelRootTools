using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace ZyxelRoot;

/// <summary>
/// Shared machinery for command execution over a dumb terminal: a rolling receive
/// buffer, an async line stream, and the marker protocol.
/// </summary>
/// <remarks>
/// The marker protocol never parses the pty echo. Every command is wrapped as:
/// <code>
///   echo ZYX''RBS&lt;token&gt;; &lt;command&gt;; echo ZYX''RES&lt;token&gt;=$?
/// </code>
/// The echoed input line contains <c>ZYX''RBS</c> (with quotes), so it can never
/// match the bare marker <c>ZYXRBS</c>. Everything between the two markers is the
/// command output, byte-exact; the exit status follows the end marker.
/// </remarks>
public abstract class MarkerShell : IDisposable, ICommandRunner, IInterruptibleShell
{
    private const int KeepBack = 512 * 1024;

    private readonly object _gate = new();
    private readonly RollingBuffer _buffer = new(256 * 1024);
    private readonly SemaphoreSlim _dataSignal = new(0);
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly Channel<string>? _lines;

    private long _lineScan;
    private volatile bool _disposed;

    protected MarkerShell(bool enableLineStream)
    {
        if (enableLineStream)
        {
            // Bounded: a live line monitor that nobody drains must not grow without
            // limit. Oldest lines are dropped; the marker protocol is unaffected.
            _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });
        }
    }

    /// <summary>Every line received from the pty, including echoes and prompts.</summary>
    public ChannelReader<string> Lines =>
        _lines?.Reader ?? throw new InvalidOperationException("line stream not enabled");

    /// <summary>Absolute offset one past the newest received byte.</summary>
    public long BufferEnd
    {
        get { lock (_gate) return _buffer.End; }
    }

    protected bool IsDisposed => _disposed;

    protected abstract Task SendRawAsync(string text, CancellationToken ct);

    /// <inheritdoc/>
    public Task SendBreakAsync(CancellationToken ct = default) => SendRawAsync("\x03", ct);

    /// <summary>Continuously reads from the transport and calls <see cref="Feed"/>.</summary>
    protected void StartPump(Func<CancellationToken, Task> pump, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try { await pump(ct).ConfigureAwait(false); }
            catch { /* transport closed */ }
            finally { MarkClosed(); }
        }, CancellationToken.None);
    }

    /// <summary>Appends received bytes, publishes complete lines, trims old data.</summary>
    protected void Feed(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;

        lock (_gate)
        {
            _buffer.Append(data);
            PublishLines();
            Trim();
        }

        _dataSignal.Release();
    }

    private void PublishLines()
    {
        if (_lines is null) return;

        while (true)
        {
            long newline = _buffer.IndexOfByte((byte)'\n', _lineScan);
            if (newline < 0) break;

            string line = _buffer.GetUtf8(_lineScan, newline);
            _lineScan = newline + 1;

            if (line.EndsWith('\r')) line = line[..^1];
            _lines.Writer.TryWrite(line);
        }
    }

    private void Trim()
    {
        long trimTo = Math.Min(_lineScan, _buffer.End) - KeepBack;
        if (trimTo > _buffer.Start)
            _buffer.TrimBefore(trimTo);
    }

    /// <summary>
    /// Discards everything received so far (used after the login banner, before a
    /// prompt-driven sequence starts).
    /// </summary>
    protected void DiscardBuffered()
    {
        lock (_gate)
        {
            long end = _buffer.End;
            _buffer.TrimBefore(end);
            _lineScan = end;
        }
    }

    protected void MarkClosed()
    {
        _disposed = true;
        _lines?.Writer.TryComplete();
        _dataSignal.Release();
    }

    /// <summary>Searches the receive buffer for a text needle.</summary>
    protected Task<long> WaitForTextAsync(string needle, long from, TimeSpan timeout, CancellationToken ct) =>
        WaitForAsync(Encoding.ASCII.GetBytes(needle), from, timeout, ct);

    /// <summary>
    /// Waits until <paramref name="needle"/> appears at or after
    /// <paramref name="from"/>. Returns its absolute offset, or -1 on timeout.
    /// </summary>
    protected async Task<long> WaitForAsync(byte[] needle, long from, TimeSpan timeout, CancellationToken ct)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (true)
        {
            long position;
            lock (_gate) position = _buffer.IndexOf(needle, from);
            if (position >= 0) return position;

            if (_disposed)
                throw new ZyxelRootException("shell closed while waiting for output");

            long remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) return -1;

            try
            {
                await _dataSignal.WaitAsync((int)Math.Min(remaining, 100), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return -1;
            }

            // Drain any extra permits so the next wait blocks again.
            while (_dataSignal.Wait(0)) { }
        }
    }

    /// <summary>
    /// Runs a single-line command and returns its output and exit status.
    /// </summary>
    public async Task<CommandResult> RunAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("command is empty", nameof(command));
        if (command.Contains('\n') || command.Contains('\r'))
            throw new ArgumentException("command must be a single line (use the SSH script writer for multi-line scripts)", nameof(command));

        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);

        await _commandLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string token = NewToken();
            byte[] startNeedle = Encoding.ASCII.GetBytes("ZYXRBS" + token);
            byte[] endNeedle = Encoding.ASCII.GetBytes("ZYXRES" + token + "=");

            long from;
            lock (_gate) from = _buffer.End;

            string wire = $"echo ZYX''RBS{token}; {command}; echo ZYX''RES{token}=$?\n";
            await SendRawAsync(wire, ct).ConfigureAwait(false);

            long endPosition = await WaitForAsync(endNeedle, from, effectiveTimeout, ct).ConfigureAwait(false);

            if (endPosition < 0)
            {
                byte[] partialBytes;
                lock (_gate) partialBytes = _buffer.GetBytes(from, _buffer.End);
                string partial = Normalize(partialBytes);
                return new CommandResult(null, partial, partialBytes, timedOut: true);
            }

            long startPosition = await WaitForAsync(startNeedle, from, TimeSpan.FromMilliseconds(500), CancellationToken.None)
                .ConfigureAwait(false);

            long contentStart = startPosition >= 0 ? startPosition + startNeedle.Length : from;

            byte[] raw;
            string statusText;
            lock (_gate)
            {
                raw = _buffer.GetBytes(contentStart, endPosition);
                statusText = _buffer.GetUtf8(endPosition + endNeedle.Length,
                    Math.Min(_buffer.End, endPosition + endNeedle.Length + 32));
            }

            // Drop the single CRLF that follows the start marker.
            int skip = 0;
            if (raw.Length >= 2 && raw[0] == (byte)'\r' && raw[1] == (byte)'\n') skip = 2;
            else if (raw.Length >= 1 && raw[0] == (byte)'\n') skip = 1;

            var body = skip == 0 ? raw : raw.AsSpan(skip).ToArray();

            int? exitCode = null;
            int length = 0;
            while (length < statusText.Length && (statusText[length] == '-' || char.IsDigit(statusText[length])))
                length++;
            if (length > 0 && int.TryParse(statusText.AsSpan(0, length), out int parsed))
                exitCode = parsed;

            return new CommandResult(exitCode, Normalize(body), body, timedOut: false);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private static string Normalize(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").TrimEnd('\n');

    internal static string NewToken() =>
        Random.Shared.NextInt64(0, long.MaxValue).ToString("x16");

    public virtual void Dispose()
    {
        MarkClosed();
    }

    /// <summary>Streams every line the pty produces.</summary>
    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (string line in Lines.ReadAllAsync(ct).ConfigureAwait(false))
            yield return line;
    }
}
