using System.Text;
using Renci.SshNet;

namespace ZyxelRoot;

/// <summary>
/// An SSH pty on the router speaking the marker protocol. Shared transport
/// for <see cref="SshAdminShell"/> (admin, uid 21) and the <c>debug</c>
/// root login.
/// </summary>
internal class SshShell : MarkerShell
{
    private readonly SshClient _client;
    private readonly ShellStream _stream;
    private readonly Thread _reader;

    protected SshShell(SshClient client, ShellStream stream)
        : base(enableLineStream: false)
    {
        _client = client;
        _stream = stream;
        _reader = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "ZyxelRoot.SshPty"
        };
        _reader.Start();
    }

    /// <summary>
    /// Opens an SSH pty with password authentication (no shell breakout).
    /// Throws <see cref="ZyxelRootException"/> on connect or auth failure —
    /// a wrong password fails here, before any shell exists.
    /// </summary>
    public static SshShell Open(string host, int port, string user, string password, TimeSpan? timeout = null)
    {
        (SshClient client, ShellStream stream) = ConnectPair(host, port, user, password, timeout);
        return new SshShell(client, stream);
    }

    /// <summary>Drops everything received so far (login banner / motd).</summary>
    internal void DiscardLoginOutput() => DiscardBuffered();

    /// <summary>Connects and opens a pty stream; the caller owns both objects.</summary>
    protected static (SshClient Client, ShellStream Stream) ConnectPair(
        string host, int port, string user, string password, TimeSpan? timeout = null)
    {
        var connection = new ConnectionInfo(host, port, user, new PasswordAuthenticationMethod(user, password))
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(15)
        };

        var client = new SshClient(connection);
        try
        {
            client.Connect();
            ShellStream stream = client.CreateShellStream("xterm", 512, 64, 0, 0, 16 * 1024);
            return (client, stream);
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw new ZyxelRootException($"SSH connect to {user}@{host}:{port} failed: {ex.Message}", ex);
        }
    }

    protected override Task SendRawAsync(string text, CancellationToken ct)
    {
        if (IsDisposed) throw new ZyxelRootException("SSH shell is closed");

        byte[] bytes = Encoding.UTF8.GetBytes(text);
        _stream.Write(bytes, 0, bytes.Length);
        _stream.Flush();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes a file through a quoted heredoc. Lines are paced: a pty burst can
    /// drop newlines, and the device's line discipline is canonical.
    /// </summary>
    public async Task WriteScriptAsync(string path, string content, CancellationToken ct = default)
    {
        string delimiter = "ZYXR" + NewToken();

        await SendRawAsync($"cat > {path} <<'{delimiter}'\n", ct).ConfigureAwait(false);
        await Task.Delay(500, ct).ConfigureAwait(false);

        string normalized = content.Replace("\r\n", "\n").TrimEnd('\n');
        foreach (string line in normalized.Split('\n'))
        {
            await SendRawAsync(line + "\n", ct).ConfigureAwait(false);
            await Task.Delay(70, ct).ConfigureAwait(false);
        }

        await SendRawAsync(delimiter + "\n", ct).ConfigureAwait(false);
        await Task.Delay(700, ct).ConfigureAwait(false);

        DiscardBuffered();
    }

    private void ReadLoop()
    {
        var buffer = new byte[8192];

        try
        {
            while (!IsDisposed)
            {
                if (_stream.DataAvailable)
                {
                    int read = _stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    Feed(buffer.AsSpan(0, read));
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
        }
        catch
        {
            // Connection closed or stream disposed.
        }
        finally
        {
            MarkClosed();
        }
    }

    public override void Dispose()
    {
        if (IsDisposed) return;

        try { _stream.Dispose(); } catch { }
        try { _client.Disconnect(); } catch { }
        try { _client.Dispose(); } catch { }

        base.Dispose();
    }
}
