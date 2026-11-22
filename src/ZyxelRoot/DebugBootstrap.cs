using System.Text.RegularExpressions;

namespace ZyxelRoot;

/// <summary>
/// Root bootstrap over the <c>debug</c> account: arms the one-shot
/// <c>pam_debugauth</c> window from the uid-21 shell, then opens a root
/// <c>/bin/sh</c> over SSH. Primary path for <see cref="RootDaemonManager"/>.
/// </summary>
/// <remarks>
/// Chain (device-verified 2026-11-19; the vendor's debug-auth mechanism):
/// <list type="number">
/// <item>Seed = 32 bytes at offset 128 of <c>/proc/security/daas</c>
/// (world-readable, uid 21). 32 zeros at boot; fresh kernel-random after every
/// <c>write_daas 1</c> and every 7 days.</item>
/// <item><c>write_enable_debug 2147483647</c> (setuid root, no auth, callable
/// from uid 21) creates <c>/var/enable_debug</c>. int32 max ≈ 68 years; a
/// larger value overflows the device's <c>atoi</c>.</item>
/// <item>Password = first line of <c>ell y &lt;seedfile&gt;</c> (65 uppercase
/// hex); a zero seed prints <c>0</c>.</item>
/// <item>A successful login rotates the seed and unlinks the window (one-shot).
/// 3 failed logins → 600 s lockout, so at most <see cref="MaxLoginAttempts"/>
/// attempts per arm; re-arm on every failure.</item>
/// </list>
/// </remarks>
internal static class DebugBootstrap
{
    /// <summary>3 failed debug logins lock the account for 600 s — hard cap is 2.</summary>
    public const int MaxLoginAttempts = 2;

    private const string SeedPath = "/tmp/.zs.seed";
    private const int WindowSeconds = 2_147_483_647;

    private static readonly Regex PasswordPattern = new("^[0-9A-Fa-f]{65}$", RegexOptions.Compiled);

    /// <summary>A debug window that is armed, with a usable password.</summary>
    public sealed class ArmedDebug
    {
        public required string Password { get; init; }
        public required bool ZeroSeed { get; init; }
    }

    /// <summary>
    /// Reads the DAAS seed from the uid-21 shell, arms <c>/var/enable_debug</c>
    /// and derives the debug password. Fresh boot (zero seed): no rotation,
    /// password <c>0</c> — rotating would move the seed away from zero.
    /// Long-running box: rotate, re-read, <c>ell y</c>. The seed file is
    /// removed on every path (raw kernel secret), success or failure.
    ///
    /// The seed read is <c>dd bs=32 skip=4 count=1</c> (lseek to 128, one
    /// 32-byte read): the proc handler serves the raw 32 seed bytes at
    /// ppos 128 and misbehaves for the 1-byte form (<c>bs=1 skip=128
    /// count=32</c> returns a 1024-byte blob on this device — verified live).
    /// <c>ell y</c> is run directly (it prints exactly one line); busybox
    /// <c>head -n 1 cmd …</c> would treat <c>cmd</c>'s arguments as files.
    /// </summary>
    public static async Task<ArmedDebug> ArmAsync(SshAdminShell admin, Action<string>? log, CancellationToken ct)
    {
        try
        {
            // 1. Seed (world-readable from uid 21). Must be exactly 32 bytes.
            CommandResult read = await admin.RunAsync(
                $"dd if=/proc/security/daas bs=32 skip=4 count=1 2>/dev/null > {SeedPath} && wc -c < {SeedPath}",
                TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            if (read.ExitCode != 0 || read.Output.Trim() != "32")
                throw new ZyxelRootException($"DAAS seed read failed (rc={read.ExitCode?.ToString() ?? "?"}, output '{read.Output.Trim()}')");

            // 2. Zero check: strip NULs; nothing left => all zeros (fresh boot).
            CommandResult nonzero = await admin.RunAsync(
                $"tr -d '\\000' < {SeedPath} | wc -c", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            bool zeroSeed = nonzero.ExitCode == 0 && nonzero.Output.Trim() == "0";

            // 3. Arm the window (int32 max ≈ 68 y) before any rotation.
            CommandResult arm = await admin.RunAsync(
                $"/bin/write_enable_debug {WindowSeconds}", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            if (arm.ExitCode != 0)
                throw new ZyxelRootException($"write_enable_debug failed (rc={arm.ExitCode?.ToString() ?? "?"}, output '{arm.Output.Trim()}')");

            string password;
            if (zeroSeed)
            {
                // Fresh boot: the seed is 32 zeros and must stay that way, or
                // the password is no longer "0".
                password = "0";
                log?.Invoke("debug arm: zero seed (fresh boot) — no rotation, password=0");
            }
            else
            {
                // Long-running box: rotate to a fresh kernel seed, then derive.
                CommandResult rotate = await admin.RunAsync(
                    "/bin/write_daas 1 && sleep 1", TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                if (rotate.ExitCode != 0)
                    throw new ZyxelRootException($"write_daas rotation failed (rc={rotate.ExitCode?.ToString() ?? "?"})");

                CommandResult reread = await admin.RunAsync(
                    $"dd if=/proc/security/daas bs=32 skip=4 count=1 2>/dev/null > {SeedPath} && wc -c < {SeedPath}",
                    TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                if (reread.ExitCode != 0 || reread.Output.Trim() != "32")
                    throw new ZyxelRootException("DAAS seed re-read after rotation failed");

                // ell prints exactly one line (65 uppercase hex, or "0").
                CommandResult derive = await admin.RunAsync(
                    $"/usr/bin/ell y {SeedPath}", TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                if (derive.ExitCode != 0)
                    throw new ZyxelRootException($"ell password derivation failed (rc={derive.ExitCode?.ToString() ?? "?"}, output '{derive.Output.Trim()}')");

                password = derive.Output.Trim();
                if (!PasswordPattern.IsMatch(password))
                    throw new ZyxelRootException($"unexpected debug password shape (expected 65 hex, got '{password}')");

                log?.Invoke("debug arm: non-zero seed — rotated, password=<65 hex>");
            }

            // 4. Window sanity.
            CommandResult window = await admin.RunAsync(
                "[ -f /var/enable_debug ] && echo WINDOW-UP || echo WINDOW-DOWN",
                TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            if (window.Output.Trim() != "WINDOW-UP")
                throw new ZyxelRootException("debug window file missing after arming");

            return new ArmedDebug { Password = password, ZeroSeed = zeroSeed };
        }
        finally
        {
            // The raw kernel secret never outlives the arm — best effort.
            try { await admin.RunAsync($"rm -f {SeedPath}", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
            catch { /* the shell is going away or the token is dead */ }
        }
    }

    /// <summary>
    /// One login attempt as <c>debug</c>, verified with <c>id</c> (uid 0).
    /// The window is one-shot: on failure the caller must re-arm before
    /// retrying.
    /// </summary>
    public static async Task<SshShell> OpenRootOnceAsync(ZyxelSettings settings, string password, Action<string>? log, CancellationToken ct)
    {
        SshShell shell = await Task.Run(
            () => SshShell.Open(settings.Host, settings.SshPort, "debug", password), ct).ConfigureAwait(false);

        try
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            shell.DiscardLoginOutput();

            CommandResult id = await shell.RunAsync("id", TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            if (id.ExitCode == 0 && id.Output.Contains("uid=0", StringComparison.Ordinal))
                return shell;

            throw new ZyxelRootException($"debug shell verify failed (rc={id.ExitCode?.ToString() ?? "?"}, output '{id.Output.Trim()}')");
        }
        catch
        {
            shell.Dispose();
            throw;
        }
    }
}
