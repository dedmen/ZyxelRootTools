namespace ZyxelRoot;

/// <summary>Base exception for all ZyxelRoot failures.</summary>
public class ZyxelRootException : Exception
{
    public ZyxelRootException(string message) : base(message) { }
    public ZyxelRootException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Anything that can run a single-line command on the router: a live
/// <see cref="RootShell"/> connection, or a <see cref="PersistentRootShell"/>
/// that transparently re-targets the command at the live connection after a
/// reconnect (monitoring components that hold one of these keep working
/// across daemon restarts and router reboots).
/// </summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default);
}

/// <summary>
/// A shell that can send Ctrl-C to its terminal. The target use: interrupting
/// a foreground command that is stuck (a <c>mipc_wan_cli</c> waiting on a
/// frozen module holds the pty until the vendor-side client timeout fires).
/// Sending one at an idle prompt is harmless (the shell redraws the prompt).
/// </summary>
public interface IInterruptibleShell
{
    /// <summary>Sends a single Ctrl-C (0x03) to the terminal.</summary>
    Task SendBreakAsync(CancellationToken ct = default);
}

/// <summary>Result of one marker-delimited command executed on a shell.</summary>
public sealed class CommandResult
{
    public CommandResult(int? exitCode, string output, byte[] rawBytes, bool timedOut)
    {
        ExitCode = exitCode;
        Output = output;
        RawBytes = rawBytes;
        TimedOut = timedOut;
    }

    /// <summary>Exit status reported by the shell, or <c>null</c> on timeout.</summary>
    public int? ExitCode { get; }

    /// <summary>
    /// Command output with CRLF normalised to LF and trailing newlines removed.
    /// Safe for display and logs.
    /// </summary>
    public string Output { get; }

    /// <summary>
    /// The exact bytes between the start and end markers. The pty applies ONLCR,
    /// so every LF is delivered as CRLF; use this when byte-exact data matters.
    /// </summary>
    public byte[] RawBytes { get; }

    /// <summary>True when the end marker did not arrive within the timeout.</summary>
    public bool TimedOut { get; }

    /// <summary>True when the command completed with exit status 0.</summary>
    public bool Success => ExitCode == 0;

    public override string ToString() =>
        TimedOut ? $"(timeout) {Output}" : $"(exit {ExitCode?.ToString() ?? "?"}) {Output}";
}

/// <summary>Outcome of a web DAL request.</summary>
public sealed class DalResult
{
    public DalResult(int httpStatus, string rawBody, string? result, string? replyMsg, string? sessionKey)
    {
        HttpStatus = httpStatus;
        RawBody = rawBody;
        Result = result;
        ReplyMsg = replyMsg;
        SessionKey = sessionKey;
    }

    /// <summary>HTTP status code (200 normally).</summary>
    public int HttpStatus { get; }

    /// <summary>Decrypted response body (JSON text) or the raw body when not encrypted.</summary>
    public string RawBody { get; }

    /// <summary>ZCFG <c>result</c> field, e.g. <c>ZCFG_SUCCESS</c> / <c>ZCFG_INTERNAL_ERROR</c>.</summary>
    public string? Result { get; }

    /// <summary>ZCFG <c>ReplyMsg</c> field (target-specific error text).</summary>
    public string? ReplyMsg { get; }

    /// <summary>Session key used for the request.</summary>
    public string? SessionKey { get; }

    /// <summary>
    /// <c>ZCFG_INTERNAL_ERROR</c> is the expected reply for a root injection: the
    /// underlying operation fails, but the injected line still runs as root.
    /// </summary>
    public bool IsExpectedInjectionReply =>
        HttpStatus == 200 && (Result is "ZCFG_INTERNAL_ERROR" or "ZCFG_SUCCESS" or null);

    public string Summary =>
        $"HTTP {HttpStatus} result={Result ?? "-"} reply={ReplyMsg ?? "-"}";
}

/// <summary>Report produced when the root telnetd daemon is (re)started.</summary>
public sealed class RootDaemonReport
{
    public required bool Succeeded { get; init; }
    public required int Port { get; init; }
    public required string DaemonPath { get; init; }
    public required string LogPath { get; init; }
    public required string LaunchOutput { get; init; }
    public required string InjectionSummary { get; init; }
    public required int ProbeAttempts { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required string Message { get; init; }

    /// <summary>
    /// Which path started the daemon: <c>debug</c> (the SSH bootstrap, primary)
    /// or <c>web</c> (DAL injection, fallback).
    /// </summary>
    public string Path { get; init; } = "web";

    public override string ToString() =>
        Succeeded
            ? $"rootd [{Path}] listening on :{Port} after {ProbeAttempts} probe(s) in {Elapsed.TotalSeconds:F1}s ({DaemonPath})"
            : $"rootd [{Path}] FAILED on :{Port} after {ProbeAttempts} probe(s) in {Elapsed.TotalSeconds:F1}s: {Message}";
}
