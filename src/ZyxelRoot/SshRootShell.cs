using Renci.SshNet;

namespace ZyxelRoot;

/// <summary>
/// A root shell on the router's SSH server: a plain password login as
/// <c>root</c> (<see cref="ZyxelSettings.RootUsername"/> /
/// <see cref="ZyxelSettings.RootPassword"/>), no daemon and no shell
/// breakout. Available on firmware builds with the root account enabled;
/// on other firmware the login simply fails and the caller (e.g.
/// <see cref="PersistentRootShell"/>) falls back to the root-telnetd path.
/// </summary>
/// <remarks>
/// The login is verified before the shell is returned: <c>id</c> must report
/// <c>uid=0</c>. A wrong password (or a firmware without the root account)
/// fails fast with <see cref="ZyxelRootException"/>, before any shell exists.
/// </remarks>
public sealed class SshRootShell : SshShell
{
    private SshRootShell(SshClient client, ShellStream stream)
        : base(client, stream)
    {
    }

    /// <summary>
    /// Opens the direct root SSH shell and verifies that the login really is
    /// root (<c>id</c> reports <c>uid=0</c>). Throws
    /// <see cref="ZyxelRootException"/> when <c>Zyxel:RootPassword</c> is not
    /// configured, on connect/auth failure, or when the login is not root.
    /// </summary>
    public static async Task<SshRootShell> OpenAsync(
        ZyxelSettings settings,
        Action<string>? log = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(settings.RootPassword))
            throw new ZyxelRootException("root SSH is not configured (Zyxel:RootPassword is empty)");

        ct.ThrowIfCancellationRequested();

        log?.Invoke($"direct root SSH as {settings.RootUsername}@{settings.Host}:{settings.SshPort}...");
        SshRootShell shell = await Task.Run(() =>
        {
            (SshClient client, ShellStream stream) = ConnectPair(
                settings.Host, settings.SshPort, settings.RootUsername, settings.RootPassword, timeout);
            return new SshRootShell(client, stream);
        }, ct).ConfigureAwait(false);

        try
        {
            shell.DiscardLoginOutput();

            CommandResult id = await shell.RunAsync("id", TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            if (id.TimedOut || id.ExitCode != 0 ||
                !id.Output.Contains("uid=0", StringComparison.Ordinal))
                throw new ZyxelRootException(
                    $"root SSH login did not yield root (exit {id.ExitCode?.ToString() ?? "?"}, output \"{id.Output.Trim()}\")");

            return shell;
        }
        catch
        {
            shell.Dispose();
            throw;
        }
    }
}
