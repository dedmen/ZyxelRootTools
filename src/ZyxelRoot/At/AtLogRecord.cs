namespace ZyxelRoot;

/// <summary>Direction of an AT exchange, as far as the logs allow it to be inferred.</summary>
public enum AtDirection
{
    /// <summary>The line is not AT traffic (status/FSM/config logging).</summary>
    None = 0,

    /// <summary>The logger announced an AT command being sent to the modem.</summary>
    Outgoing = 1,

    /// <summary>The line looks like a modem AT response token (<c>+CMGL:</c>, <c>OK</c>, ...).</summary>
    Incoming = 2
}

/// <summary>
/// One parsed line of modem daemon logging (<c>/var/log/mmm.log</c>,
/// <c>/var/log/wan.log</c> or <c>/var/log/cm.log</c>).
/// </summary>
public sealed class AtLogRecord
{
    /// <summary>Log file the line came from.</summary>
    public required string Source { get; init; }

    /// <summary>Log module tag, e.g. <c>MMM</c>, <c>WAN</c>, <c>CM</c>.</summary>
    public string Module { get; init; } = "";

    /// <summary>Log level tag: <c>INFO</c>, <c>WARN</c>, <c>ERROR</c>, <c>DEBUG</c>, <c>MISC</c>.</summary>
    public string Level { get; init; } = "";

    /// <summary>Handler / function name when the line carries one (e.g. <c>mtkGetAllSms</c>).</summary>
    public string? Function { get; init; }

    /// <summary>Source line number embedded in the function tag, when present.</summary>
    public int? FunctionLine { get; init; }

    /// <summary>Message body after the function tag.</summary>
    public string Message { get; init; } = "";

    /// <summary>The complete original line.</summary>
    public string RawLine { get; init; } = "";

    /// <summary>
    /// Timestamp from the log. The device writes <c>MM/dd HH:mm:ss</c> without a
    /// year; the current local year is assumed.
    /// </summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>Best-effort AT direction.</summary>
    public AtDirection Direction { get; init; }

    /// <summary>AT command or response token, when <see cref="Direction"/> is not None.</summary>
    public string? AtCommand { get; init; }

    /// <summary>True when the line carries AT traffic.</summary>
    public bool IsAtTraffic => Direction != AtDirection.None;

    public override string ToString()
    {
        string ts = Timestamp?.ToString("HH:mm:ss") ?? "--:--:--";
        string fn = Function is null ? "" : $"{Function}({FunctionLine}) ";
        string at = Direction == AtDirection.None ? "" : $" [{Direction}: {AtCommand}]";
        return $"{ts} [{Module}][{Level}] {fn}{Message}{at}";
    }
}

/// <summary>Per-source health snapshot of an <see cref="AtMonitor"/>.</summary>
public sealed class AtLogSourceStatus
{
    public required string Path { get; init; }
    public required bool Available { get; init; }
    public required long FileLines { get; init; }
    public required long NextLine { get; init; }
    public required long RecordsRead { get; init; }
    public string? LastError { get; init; }

    public override string ToString() =>
        $"{Path}: {(Available ? "ok" : "unavailable")} lines={FileLines} next={NextLine} records={RecordsRead}" +
        (LastError is null ? "" : $" error={LastError}");
}
