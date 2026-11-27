using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ZyxelRoot;

/// <summary>
/// A root shell on the router's unauthenticated telnetd
/// (<c>telnetd -F -p 4444 -l /bin/sh</c>), speaking minimal telnet.
/// </summary>
/// <remarks>
/// The transport is a raw TCP pty. Telnet IAC negotiation is stripped; everything
/// else is passed through. Commands use the marker protocol from
/// <see cref="MarkerShell"/>: the pty echo is never parsed, and long lines are
/// never assumed to arrive unwrapped.
/// </remarks>
public sealed class RootShell : MarkerShell, IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly TelnetIacFilter _iac = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly byte[] _readBuffer = new byte[16 * 1024];

    private RootShell(Socket socket)
        : base(enableLineStream: true)
    {
        _socket = socket;
    }

    /// <summary>Host this shell is connected to.</summary>
    public string Host { get; private init; } = "";

    /// <summary>Root telnetd port this shell is connected to.</summary>
    public int Port { get; private init; }

    /// <summary>
    /// Opens a connection to the root telnetd. Throws
    /// <see cref="ZyxelRootException"/> when the port is not accepting.
    /// </summary>
    public static async Task<RootShell> ConnectAsync(
        string host,
        int port = 4444,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));

        try
        {
            await socket.ConnectAsync(new DnsEndPoint(host, port), connectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new ZyxelRootException($"connect to {host}:{port} timed out");
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new ZyxelRootException($"connect to {host}:{port} failed: {ex.Message}", ex);
        }

        var shell = new RootShell(socket) { Host = host, Port = port };
        shell.StartPump(shell.PumpAsync, shell._lifetime.Token);
        return shell;
    }

    /// <summary>Quick TCP reachability probe.</summary>
    public static async Task<bool> ProbeAsync(string host, int port, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(2));
        try
        {
            await socket.ConnectAsync(new DnsEndPoint(host, port), probeCts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Writes raw text to the pty (no marker protocol).</summary>
    public async Task WriteRawAsync(string text, CancellationToken ct = default)
    {
        if (_lifetime.IsCancellationRequested)
            throw new ZyxelRootException("shell is closed");

        var bytes = Encoding.UTF8.GetBytes(text);
        await _socket.SendAsync(bytes, SocketFlags.None, ct).ConfigureAwait(false);
    }

    /// <summary>Sends one line to the shell.</summary>
    public Task WriteLineAsync(string line, CancellationToken ct = default) =>
        WriteRawAsync(line + "\n", ct);

    protected override Task SendRawAsync(string text, CancellationToken ct) => WriteRawAsync(text, ct);

    private async Task PumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            int received;
            try
            {
                received = await _socket.ReceiveAsync(_readBuffer.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            if (received <= 0) break;

            var clean = _iac.Process(_readBuffer.AsSpan(0, received));
            if (clean.Length > 0) Feed(clean);
        }
    }

    public override void Dispose()
    {
        if (IsDisposed) return;
        try { _lifetime.Cancel(); } catch { }
        try { _socket.Shutdown(SocketShutdown.Both); } catch { }
        try { _socket.Dispose(); } catch { }
        _lifetime.Dispose();
        base.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
