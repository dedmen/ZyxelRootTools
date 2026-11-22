using System.Text;

namespace ZyxelRoot;

/// <summary>
/// Tails one log file over the root shell using line offsets.
/// </summary>
/// <remarks>
/// Line offsets (not byte offsets) are used deliberately: the telnetd pty applies
/// ONLCR, so every LF in the output arrives as CRLF and byte counts would drift.
/// The poll command is
/// <code>
///   wc -l &lt; &lt;file&gt;; tail -n +&lt;next&gt; &lt;file&gt; | head -n &lt;max&gt;
/// </code>
/// Only fully terminated lines are emitted, and <c>next</c> advances only past
/// them, so a line cut by <c>head</c> is re-read in full on the next poll.
/// </remarks>
internal sealed class LogTailSource
{
    private readonly Func<string, string, AtLogRecord?> _transform;

    public LogTailSource(string path, Func<string, string, AtLogRecord?>? lineParser = null)
    {
        Path = path;
        // Default: the stateful vendor-log parser. A custom parser (e.g. a raw
        // passthrough for non-vendor formats like the mipc_tap log) is allowed;
        // it receives (sourcePath, rawLine) and may return null to drop a line.
        _transform = lineParser ?? new AtLogParser().Parse;
    }

    public string Path { get; }

    /// <summary>Last <c>wc -l</c> value for the file.</summary>
    public long FileLines { get; private set; }

    /// <summary>Next 1-based line number to read.</summary>
    public long NextLine { get; private set; } = -1;

    public long RecordsRead { get; private set; }

    public bool Available { get; private set; } = true;

    /// <summary>True when the last poll could not reach the shell (timeout / closed).</summary>
    public bool LastPollFailed { get; private set; }

    public string? LastError { get; private set; }

    public AtLogSourceStatus GetStatus() => new()
    {
        Path = Path,
        Available = Available,
        FileLines = FileLines,
        NextLine = NextLine,
        RecordsRead = RecordsRead,
        LastError = LastError
    };

    /// <summary>
    /// Determines the starting line so the monitor begins with a little history
    /// instead of replaying the whole file.
    /// </summary>
    public async Task InitializeAsync(ICommandRunner shell, int historyLines, CancellationToken ct)
    {
        CommandResult result = await shell.RunAsync($"wc -l < {Path} 2>/dev/null", TimeSpan.FromSeconds(10), ct)
            .ConfigureAwait(false);

        if (!TryParseCount(result.Output, out long lines))
        {
            Available = false;
            LastError = $"cannot read {Path}: {result.Output.Trim()}";
            NextLine = 1;
            return;
        }

        Available = true;
        LastError = null;
        FileLines = lines;
        NextLine = Math.Max(1, lines - Math.Max(0, historyLines) + 1);
    }

    /// <summary>Reads new lines and returns the parsed records.</summary>
    public async Task<List<AtLogRecord>> PollAsync(ICommandRunner shell, int maxLines, CancellationToken ct)
    {
        var records = new List<AtLogRecord>();
        LastPollFailed = false;

        if (NextLine < 1) NextLine = 1;
        if (maxLines < 1) maxLines = 1;

        int blooms = 0;
        while (true)
        {
            long requestedFrom = NextLine;
            string command = $"wc -l < {Path} 2>/dev/null; tail -n +{requestedFrom} {Path} 2>/dev/null | head -n {maxLines}";

            CommandResult result = await shell.RunAsync(command, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            if (result.TimedOut)
            {
                LastPollFailed = true;
                LastError = "poll timed out";
                return records;
            }

            if (!TryReadFirstLine(result.RawBytes, out string countText) || !TryParseCount(countText, out long lines))
            {
                Available = false;
                LastError = $"cannot stat {Path}: {result.Output.Trim()}";
                return records;
            }

            Available = true;
            LastError = null;
            FileLines = lines;

            // Rotated or truncated: start over.
            if (lines + 1 < requestedFrom)
            {
                NextLine = 1;
                if (blooms++ == 0) continue;
                return records;
            }

            int consumed = 0;
            int position = IndexAfterFirstLine(result.RawBytes);

            if (position >= 0)
            {
                int lineStart = position;
                for (int i = position; i < result.RawBytes.Length; i++)
                {
                    if (result.RawBytes[i] != (byte)'\n') continue;

                    int end = i;
                    if (end > lineStart && result.RawBytes[end - 1] == (byte)'\r') end--;

                    string lineText = Encoding.UTF8.GetString(result.RawBytes, lineStart, end - lineStart);
                    lineStart = i + 1;
                    consumed++;

                    AtLogRecord? record = _transform(Path, lineText);
                    if (record is not null)
                    {
                        records.Add(record);
                        RecordsRead++;
                    }
                }
            }

            NextLine = requestedFrom + consumed;
            return records;
        }
    }

    private static int IndexAfterFirstLine(byte[] bytes)
    {
        int index = Array.IndexOf(bytes, (byte)'\n');
        return index < 0 ? -1 : index + 1;
    }

    private static bool TryReadFirstLine(byte[] bytes, out string line)
    {
        if (bytes.Length == 0)
        {
            line = "";
            return false;
        }

        int index = Array.IndexOf(bytes, (byte)'\n');
        int length = index < 0 ? bytes.Length : index;
        line = Encoding.UTF8.GetString(bytes, 0, length);
        return true;
    }

    private static bool TryParseCount(string text, out long value) =>
        long.TryParse(text.Trim(), out value);
}
