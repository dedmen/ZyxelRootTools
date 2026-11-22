using System.Globalization;
using System.Text.RegularExpressions;

namespace ZyxelRoot;

/// <summary>
/// Parses the modem daemon log format.
/// </summary>
/// <remarks>
/// Two shapes occur on the device:
/// <code>
/// [11/19 19:50:01]                      &lt;- standalone timestamp (mmm.log, wan.log)
/// [MMM][INFO]mtkGetAllSms(6268) Get All SMS ATCMD:AT+CMGL=4
///
/// [11/19 19:53:33][CM][INFO]Processing event, apnNum:1, ...   &lt;- inline (cm.log)
/// </code>
/// A standalone timestamp applies to all following message lines until the next
/// one, so the parser is stateful and must see lines in order.
/// </remarks>
internal sealed class AtLogParser
{
    private static readonly Regex InlineTimestamp = new(
        @"^\[(?<ts>\d{1,2}/\d{1,2} \d{2}:\d{2}:\d{2})\](?<rest>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex Prefix = new(
        @"^\[(?<module>[A-Za-z]+)\]\[(?<level>[A-Za-z]+)\](?<body>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex FunctionTag = new(
        @"^\s*(?<fn>[A-Za-z_][A-Za-z0-9_]*)\((?<line>\d+)\)\s?(?<msg>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex AnnouncedCommand = new(
        @"(?:ATCMD:|atcmd=)\s*(?<cmd>AT[^;]*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ResponseToken = new(
        @"^\+(?<tok>[A-Z]{2,8})\b",
        RegexOptions.Compiled);

    private DateTimeOffset? _currentTimestamp;

    /// <summary>Parses one log line. Returns <c>null</c> for timestamp-only lines.</summary>
    public AtLogRecord? Parse(string source, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        DateTimeOffset? timestamp = _currentTimestamp;
        string body = line;

        Match tsMatch = InlineTimestamp.Match(line);
        if (tsMatch.Success)
        {
            timestamp = ParseTimestamp(tsMatch.Groups["ts"].Value);
            _currentTimestamp = timestamp;
            body = tsMatch.Groups["rest"].Value;

            if (string.IsNullOrWhiteSpace(body))
                return null; // standalone timestamp line
        }

        string module = "";
        string level = "";

        Match prefix = Prefix.Match(body);
        if (prefix.Success)
        {
            module = prefix.Groups["module"].Value.ToUpperInvariant();
            level = prefix.Groups["level"].Value.ToUpperInvariant();
            body = prefix.Groups["body"].Value;
        }

        string? function = null;
        int? functionLine = null;
        string message = body.TrimEnd();

        Match fn = FunctionTag.Match(body);
        if (fn.Success)
        {
            function = fn.Groups["fn"].Value;
            if (int.TryParse(fn.Groups["line"].Value, out int lineNumber))
                functionLine = lineNumber;
            message = fn.Groups["msg"].Value.TrimEnd();
        }

        (AtDirection direction, string? atCommand) = ClassifyDirection(message);

        return new AtLogRecord
        {
            Source = source,
            Module = module,
            Level = level,
            Function = function,
            FunctionLine = functionLine,
            Message = message,
            RawLine = line,
            Timestamp = timestamp,
            Direction = direction,
            AtCommand = atCommand
        };
    }

    private static (AtDirection Direction, string? Command) ClassifyDirection(string message)
    {
        string trimmed = message.TrimStart();
        if (trimmed.Length == 0) return (AtDirection.None, null);

        // The vendor handlers announce the outgoing command as "ATCMD:<text>"
        // (mtkGetAllSms) or "Send SMS atcmd=<text>" (mtkSetSmsSendMsg).
        Match announced = AnnouncedCommand.Match(trimmed);
        if (announced.Success)
        {
            string command = announced.Groups["cmd"].Value.Trim();
            if (command.Length > 0) return (AtDirection.Outgoing, command);
        }

        // Modem replies, when they are logged verbatim: OK / ERROR / +CME ERROR /
        // +CMS ERROR / +<TOKEN>: ...
        if (trimmed is "OK" or "ERROR")
            return (AtDirection.Incoming, trimmed);

        if (trimmed.StartsWith("+CME ERROR", StringComparison.Ordinal) ||
            trimmed.StartsWith("+CMS ERROR", StringComparison.Ordinal))
            return (AtDirection.Incoming, trimmed.Split(':')[0].TrimEnd());

        Match token = ResponseToken.Match(trimmed);
        if (token.Success)
            return (AtDirection.Incoming, "+" + token.Groups["tok"].Value);

        return (AtDirection.None, null);
    }

    private static DateTimeOffset? ParseTimestamp(string text)
    {
        if (!DateTime.TryParseExact(text, "MM/dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime parsed))
            return null;

        var local = new DateTime(DateTime.Now.Year, parsed.Month, parsed.Day,
            parsed.Hour, parsed.Minute, parsed.Second, DateTimeKind.Local);

        return new DateTimeOffset(local);
    }
}
