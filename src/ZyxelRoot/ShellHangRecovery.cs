namespace ZyxelRoot;

/// <summary>
/// Best-effort recovery of a shell whose foreground command is stuck. A stuck
/// <c>mipc_wan_cli</c> (the module stopped answering) holds the pty until the
/// vendor-side client timeout fires (atcid's per-command block, 300 s), and
/// every command issued meanwhile only queues up behind it. This sends a
/// Ctrl-C (which usually kills the client immediately), then runs a no-op
/// through the shell; if that still times out it waits out the vendor block
/// with one more no-op, so the next real command runs on a clean, responsive
/// pty.
/// </summary>
internal static class ShellHangRecovery
{
    /// <summary>The vendor-side per-command block, plus margin.</summary>
    private static readonly TimeSpan VendorBlock = TimeSpan.FromSeconds(330);

    /// <summary>
    /// Starts the recovery on a background task (never blocks the caller) and
    /// swallows every failure: a reconnect establishes a fresh, clean shell
    /// anyway.
    /// </summary>
    public static void Schedule(ICommandRunner shell)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (shell is IInterruptibleShell interruptible)
                {
                    try
                    {
                        await interruptible.SendBreakAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // The terminal may already be gone; the probe below
                        // still re-syncs a live one.
                    }
                }

                CommandResult probe = await shell.RunAsync(
                    "true", TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                if (probe.TimedOut)
                    await shell.RunAsync("true", VendorBlock, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Disconnected, disposed, or the shell stopped answering
                // altogether — nothing left to recover here.
            }
        });
    }
}
