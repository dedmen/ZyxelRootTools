using System.Text;
using System.Text.RegularExpressions;
using ZyxelRoot;

namespace ZyxelRoot.Sms;

/// <summary>Result of one <see cref="ZyxelSms.SendAsync(string, string, SmsSendOptions, System.Threading.CancellationToken)"/> call (one SMS part).</summary>
public sealed class SmsSendResult
{
    public required bool Accepted { get; init; }
    public required int? ResultCode { get; init; }
    public required string? ModemReport { get; init; }
    public required string? OutboxFile { get; init; }
    public required string Output { get; init; }

    /// <summary>The modem confirmed the send with <c>+CMGS:</c> (fresh
    /// <c>/xdata/SMS/sendMsgRep.txt</c>). This — not cmctl's synchronous
    /// result — is the success criterion. On a failed send this instead
    /// carries the modem's rejection line from <c>/var/log/mmm.log</c>
    /// (e.g. <c>+CME ERROR: 513</c>) when one was found.</summary>
    public bool Success => Accepted;

    public override string ToString() =>
        Accepted
            ? $"accepted, modem: {(ModemReport ?? "").Split('\n')[0]}" +
              $" (cmctl Result Code {ResultCode?.ToString() ?? "n/a"})"
            : $"FAILED — no modem confirmation within the timeout" +
              $" (cmctl Result Code {ResultCode?.ToString() ?? "n/a"})" +
              (ModemReport is not null ? $"; {ModemReport}" : "");
}

/// <summary>
/// Character set for a send. <see cref="Auto"/> picks GSM 7-bit when every
/// character is in the GSM alphabet (more characters per part), otherwise
/// UCS-2.
/// </summary>
public enum SmsCharset
{
    Auto,
    Gsm7,
    Ucs2
}

/// <summary>
/// Options for <see cref="ZyxelSms.SendAsync(string, string, SmsSendOptions, CancellationToken)"/>.
/// </summary>
public sealed class SmsSendOptions
{
    /// <summary>Character set (default <see cref="SmsCharset.Auto"/>).</summary>
    public SmsCharset Charset { get; init; } = SmsCharset.Auto;

    /// <summary>
    /// Raw user data header (UDH) bytes to prepend to the user data; the PDU is
    /// then sent with UDHI=1. For a standard multipart concatenation header use
    /// <see cref="MultipartUdh"/>. <see cref="ZyxelSms.SendLongAsync"/> builds
    /// the multipart header itself and does not accept a custom one.
    /// </summary>
    public byte[]? UserHeader { get; init; }

    /// <summary>How long to wait for the modem's <c>+CMGS:</c> confirmation
    /// (default 20 s).</summary>
    public TimeSpan? ModemReportTimeout { get; init; }

    /// <summary>
    /// The standard 6-byte multipart concatenation UDH (GSM 23.040
    /// concatenated-short-messages IE):
    /// <c>05 00 03 &lt;refNo&gt; &lt;maxParts&gt; &lt;seqNo&gt;</c> — the same
    /// shape the phone side and the device's own SMS forwarding use
    /// (verified against a received multipart: 6-byte UDH, length octet first).
    /// </summary>
    public static byte[] MultipartUdh(int referenceNo, int maxParts, int seqNo)
    {
        if (referenceNo is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(referenceNo));
        if (maxParts is < 1 or > 255)
            throw new ArgumentOutOfRangeException(nameof(maxParts));
        if (seqNo is < 1 or > 255 || seqNo > maxParts)
            throw new ArgumentOutOfRangeException(nameof(seqNo));
        return [0x05, 0x00, 0x03, (byte)referenceNo, (byte)maxParts, (byte)seqNo];
    }
}

/// <summary>Result of a multipart send: one <see cref="SmsSendResult"/> per part.</summary>
public sealed class SmsLongSendResult
{
    /// <summary>Multipart reference number shared by all parts (random per message).</summary>
    public required int RefNo { get; init; }

    /// <summary>The character set the message was encoded in.</summary>
    public required SmsCharset Charset { get; init; }

    public required IReadOnlyList<SmsSendResult> Parts { get; init; }

    /// <summary>True when every sent part got a fresh <c>+CMGS:</c>
    /// confirmation. Sending stops at the first part without one.</summary>
    public bool AllAccepted => Parts.Count > 0 && Parts.All(p => p.Accepted);

    public override string ToString() =>
        AllAccepted
            ? $"accepted, {Parts.Count} part(s) (ref {RefNo}, {Charset})"
            : $"FAILED — {Parts.Count} part(s) sent, no confirmation for the rest (ref {RefNo}, {Charset})";
}

/// <summary>
/// SMS on the FWA505: send text (single part or multipart, with UDH) and
/// read the stored inbox.
/// </summary>
/// <remarks>
/// Everything runs as a single-line command through the marker protocol on a
/// root shell (<see cref="MarkerShell"/>), using the device's own
/// <c>/usr/sbin/smscmd</c>:
/// <list type="bullet">
/// <item>send → <c>smscmd sendpdu &lt;to&gt; &lt;GSM|UCS2&gt; &lt;len&gt;
/// &lt;userDataHex&gt; &lt;UDHI&gt;</c> → modem. The user data is encoded here
/// (GSM 7-bit via <see cref="Gsm7Codec"/> or UCS-2) and the device builds the
/// PDU. Text-mode <c>smscmd send</c> is rejected by this modem
/// (<c>+CME ERROR: 100</c>, verified live 2026-11-19), so the send is always a
/// PDU.</item>
/// <item>A user data header (UDH) is passed through verbatim: when UDHI≠0,
/// smscmd takes the first 6 bytes of <c>userDataHex</c> as the UDH.
/// <see cref="SendLongAsync"/> chunks a long message and builds the standard
/// multipart header per part.</item>
/// <item><c>smscmd</c> writes a <c>STO SENT</c> outbox record into
/// <c>/xdata/SMS/Inbox/</c> and the modem's <c>+CMGS:</c> confirmation is
/// dumped to <c>/xdata/SMS/sendMsgRep.txt</c>.</item>
/// <item>storage: one JSON file per message in <c>/xdata/SMS/Inbox/</c>
/// (received <c>and</c> sent); cap ~100 files, oldest deleted first.</item>
/// </list>
/// Constraints: destination is digits with an optional leading <c>+</c>; no CR
/// in the message (a line feed is fine — it is encodable in both charsets).
/// <para>
/// <b>Size — verified modem cap (2026-11-20):</b> the sent PDU may carry at
/// most <b>14 octets of user data, UDH included</b> (the modem rejects 15+
/// with <c>+CME ERROR: 513</c>, message length exceeds). That is ≤ 16 GSM
/// 7-bit / 7 UCS-2 chars for a plain part, and ≤ 9 / 4 chars with the
/// 6-byte concatenation UDH. Longer messages go through
/// <see cref="SendLongAsync"/>, which sends several small parts that the
/// phone reassembles — multipart works, just with this part size.
/// </para>
/// <para>
/// <b>GSM 7-bit length quirk (verified 2026-11-20):</b> the modem only
/// accepts a GSM 7-bit PDU whose UDL octet equals the user-data octet count
/// <i>plus one</i>; <c>smscmd</c> writes its <c>&lt;len&gt;</c> argument
/// verbatim into that octet for GSM 7-bit (UCS-2 uses the real count). This
/// class passes the correct value automatically — a hand-rolled
/// <c>smscmd sendpdu … GSM</c> must use <c>len = dataBytes + 1</c> or the
/// modem answers <c>+CME ERROR: 513</c>.
/// </para>
/// <para>
/// <b>Mode trap (verified 2026-11-20):</b> the one-line PDU send only works
/// while the modem is in text mode (<c>AT+CMGF=0</c>, the factory state).
/// After <c>AT+CMGF=1</c> even previously-good PDUs fail with
/// <c>+CME ERROR: 303</c>; <c>AT+CMGF=0</c> restores sending. A failed send
/// carries the modem's rejection line (from <c>/var/log/mmm.log</c>) in
/// <see cref="SmsSendResult.ModemReport"/>.
/// </para>
/// </remarks>
public sealed class ZyxelSms
{
    private const string SepMarker = "===ZYXSMSFILE ";
    private const string ReportFile = "/xdata/SMS/sendMsgRep.txt";

    /// <summary>Modem hard cap: max total user data per sent PDU — payload
    /// plus user header — in octets. Verified live 2026-11-20 on this
    /// firmware: 14 octets accepted (plain GSM 7-bit, UCS-2, and with the
    /// 6-byte concat UDH), 15+ rejected with <c>+CME ERROR: 513</c>
    /// (message length exceeds). Independent of the destination address
    /// length.</summary>
    private const int MaxUserDataOctets = 14;
    private const int Gsm7PartLengthUdh = 9;     // (14 − 6 UDH) = 8 packed octets → 9 GSM 7-bit chars
    private const int Ucs2PartLengthUdh = 4;     // (14 − 6 UDH) octets → 4 UCS-2 chars
    private const int Gsm7PackedBudgetUdh = 8;   // MaxUserDataOctets − 6-byte UDH

    private static readonly Regex ToPattern =
        new(@"^\+?[0-9]{1,20}$", RegexOptions.Compiled);
    private static readonly Regex ResultCodeRx =
        new(@"Result Code:(-?\d+)", RegexOptions.Compiled);
    private static readonly Regex OutboxFileRx =
        new(@"Updated sent SMS Message to (?:file|Inbox): (\S+)", RegexOptions.Compiled);
    private static readonly Regex HexLineRx =
        new("^[0-9a-fA-F][0-9a-fA-F ]*$", RegexOptions.Compiled);

    private readonly ICommandRunner _shell;

    /// <summary>Works with any command channel: a live <see cref="MarkerShell"/>
    /// (<see cref="RootShell"/>) or a <see cref="PersistentRootShell"/> that
    /// keeps the SMS working across reconnects and reboots.</summary>
    public ZyxelSms(ICommandRunner shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
    }

    /// <summary>Sends a plain-text SMS to <paramref name="to"/>; see
    /// <see cref="SendAsync(string, string, SmsSendOptions, CancellationToken)"/>
    /// for charset and UDH control.</summary>
    public Task<SmsSendResult> SendAsync(string to, string message, TimeSpan? modemReportTimeout = null, CancellationToken ct = default)
        => SendAsync(to, message, new SmsSendOptions { ModemReportTimeout = modemReportTimeout }, ct);

    /// <summary>
    /// Sends one SMS part to <paramref name="to"/>.
    /// </summary>
    /// <remarks>
    /// Sends in **PDU mode** (<c>smscmd sendpdu …</c>): the user data is
    /// encoded here and the device builds the PDU. The charset defaults to
    /// <see cref="SmsCharset.Auto"/> (GSM 7-bit when every character is in the
    /// GSM alphabet, else UCS-2); <paramref name="options.UserHeader"/>
    /// attaches a UDH (UDHI=1). The text-mode command (<c>smscmd send</c>) is
    /// rejected by this modem with <c>+CME ERROR: 100</c> — verified live
    /// 2026-11-19 (a PDU send succeeded minutes after a text send failed).
    /// </remarks>
    public async Task<SmsSendResult> SendAsync(string to, string message, SmsSendOptions options, CancellationToken ct = default)
    {
        ValidateTo(to);
        ValidateMessage(message);
        options ??= new SmsSendOptions();

        PduPayload payload = BuildPayload(message, options.Charset, options.UserHeader);
        return await SendPartAsync(to, payload, options.ModemReportTimeout, ct);
    }

    /// <summary>
    /// Sends a long message as a multipart SMS: the text is chunked into parts
    /// (9 GSM 7-bit / 4 UCS-2 chars each — the modem caps total user data at
    /// 14 octets per part, UDH included, verified 2026-11-20), every part
    /// carries the standard 6-byte concatenation UDH, and the parts are sent
    /// in order, waiting for the modem's <c>+CMGS:</c> confirmation between
    /// parts. The phone reassembles the parts into one message from the UDH.
    /// </summary>
    /// <remarks>
    /// Sending stops at the first part without a confirmation;
    /// <see cref="SmsLongSendResult.AllAccepted"/> tells you whether the
    /// message went out whole. A custom
    /// <see cref="SmsSendOptions.UserHeader"/> cannot be combined with this
    /// (the UDH is the multipart header).
    /// </remarks>
    public async Task<SmsLongSendResult> SendLongAsync(string to, string message, SmsSendOptions? options = null, CancellationToken ct = default)
    {
        ValidateTo(to);
        ValidateMessage(message);
        options ??= new SmsSendOptions();
        if (options.UserHeader is not null)
            throw new ArgumentException(
                "UserHeader cannot be combined with SendLongAsync: the UDH is the multipart header",
                nameof(options));

        SmsCharset charset = options.Charset is SmsCharset.Auto
            ? (Gsm7Codec.CanEncode(message) ? SmsCharset.Gsm7 : SmsCharset.Ucs2)
            : options.Charset;

        List<string> parts = SplitMultipart(message, charset);
        int refNo = Random.Shared.Next(byte.MaxValue);
        var results = new List<SmsSendResult>(parts.Count);
        for (int i = 0; i < parts.Count; i++)
        {
            byte[] udh = SmsSendOptions.MultipartUdh(refNo, parts.Count, i + 1);
            results.Add(await SendPartAsync(to, BuildPayload(parts[i], charset, udh), options.ModemReportTimeout, ct));
            if (!results[^1].Accepted)
                break; // the following parts would only make the message more broken
        }

        return new SmsLongSendResult
        {
            RefNo = refNo,
            Charset = charset,
            Parts = results
        };
    }

    /// <summary>
    /// Reads the SMS stored in <c>/xdata/SMS/Inbox/</c>, newest first.
    /// Ordering is by file mtime, so both the older <c>sms_*</c> naming and
    /// the newer <c>sms_i_*</c> (received) / <c>sms_o_*</c> (sent) split on
    /// newer firmware come back in true recency order.
    /// </summary>
    /// <param name="maxMessages">How many of the most recent files to read
    /// (default 50, cap 100 — the device's own inbox cap).</param>
    /// <param name="includeSent">Also return the outbox (<c>STO SENT</c>)
    /// records, which share the directory (default true).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<SmsMessage>> GetStoredAsync(
        int maxMessages = 50,
        bool includeSent = true,
        CancellationToken ct = default)
    {
        if (maxMessages is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maxMessages), "must be 1..100");

        // Ordered by mtime (newest first), not by file name: newer firmware
        // splits the inbox into sms_i_* (received) and sms_o_* (sent) names,
        // so a name sort groups by direction instead of time and the newest
        // received message would fall out of the window.
        // The pty wraps long lines, so the JSON is transferred hex-encoded
        // (33-char lines, never wrapped). One round trip for the whole inbox.
        string command =
            "ls -1t /xdata/SMS/Inbox/sms_*.txt 2>/dev/null | " +
            "grep '^/xdata/SMS/Inbox/sms_' | head -" + maxMessages + " | " +
            "while IFS= read -r f; do echo \"" + SepMarker + "$f\"; " +
            "hexdump -v -e '16/1 \"%02x\" \"\\n\"' \"$f\"; done";

        CommandResult result = await _shell.RunAsync(command, TimeSpan.FromSeconds(60), ct);
        if (result.TimedOut)
            throw new ZyxelRootException("inbox read timed out");

        var messages = ParseDump(result.Output ?? ""); // already newest first
        if (!includeSent)
            messages.RemoveAll(m => m.IsSent);
        return messages;
    }

    private async Task<string?> WaitForModemReportAsync(string? previous, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var probe = await _shell.RunAsync($"cat {ReportFile} 2>/dev/null", TimeSpan.FromSeconds(10), cts.Token);
                if (!probe.TimedOut && probe.Success &&
                    probe.Output != previous && probe.Output.Contains("+CMGS"))
                {
                    return probe.Output.Trim();
                }

                await Task.Delay(500, cts.Token);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // outer cancellation — propagate
        }
        catch (OperationCanceledException)
        {
            // inner timeout elapsed — fall through to "no confirmation"
        }

        return null;
    }

    /// <summary>Single-quotes an argument for busybox ash; byte-exact for any
    /// content except <c>"</c> (rejected earlier) and NUL.</summary>
    internal static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>Encodes <paramref name="text"/> as UCS-2 hex (big-endian
    /// code units, <c>X4</c> each) — the <c>&lt;userData&gt;</c> argument of
    /// <c>smscmd sendpdu</c>.</summary>
    internal static string ToUcs2Hex(string text)
    {
        var sb = new StringBuilder(text.Length * 4);
        foreach (char c in text)
            sb.Append(((int)c).ToString("X4"));
        return sb.ToString();
    }

    private static string ToHex(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
            sb.Append(b.ToString("X2"));
        return sb.ToString();
    }

    private static void ValidateTo(string to)
    {
        if (string.IsNullOrWhiteSpace(to))
            throw new ArgumentException("to is empty", nameof(to));
        if (!ToPattern.IsMatch(to))
            throw new ArgumentException("to must be digits, optionally with a leading '+'", nameof(to));
    }

    private static void ValidateMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
            throw new ArgumentException("message is empty", nameof(message));
        if (message.Any(c => c == '\r'))
            throw new ArgumentException("message must not contain CR", nameof(message));
    }

    /// <summary>The <c>smscmd sendpdu</c> arguments for one part.</summary>
    private sealed record PduPayload(string Charset, int Length, string UserDataHex, bool Udhi);

    private static PduPayload BuildPayload(string message, SmsCharset charset, byte[]? userHeader)
    {
        byte[] udh = userHeader ?? [];
        bool udhi = udh.Length > 0;
        int budget = MaxUserDataOctets - udh.Length; // modem cap, UDH included

        if (charset is not SmsCharset.Ucs2 && Gsm7Codec.CanEncode(message))
        {
            byte[] packed = Gsm7Codec.Encode(message);
            int gsmDataBytes = udh.Length + packed.Length;
            if (gsmDataBytes > MaxUserDataOctets)
                throw new ArgumentException(
                    $"message packs to {gsmDataBytes} octets (user header + GSM 7-bit); the modem caps user data at {MaxUserDataOctets} octets per part (verified: +CME ERROR: 513 above that) — use SendLongAsync to split it into smaller parts",
                    nameof(message));
            // The modem's one-line CMGS handler only accepts a GSM 7-bit PDU
            // whose UDL octet is the data count plus one, and smscmd writes
            // the <len> argument verbatim into that octet for GSM 7-bit.
            return new PduPayload("GSM", gsmDataBytes + 1, ToHex(udh) + ToHex(packed), udhi);
        }

        if (charset == SmsCharset.Gsm7)
            throw new ArgumentException(
                "message contains characters outside the GSM 7-bit alphabet; re-try with SmsCharset.Ucs2",
                nameof(message));

        int bytes = message.Length * 2;
        int dataBytes = udh.Length + bytes;
        if (dataBytes > MaxUserDataOctets)
            throw new ArgumentException(
                $"message is {message.Length} chars; with this header the UCS-2 limit is {budget / 2} chars per part (modem caps user data at {MaxUserDataOctets} octets) — use SendLongAsync to split it into smaller parts",
                nameof(message));
        // For UCS-2 smscmd computes the UDL octet from the data itself and
        // ignores <len>, so pass the real count (harmless either way).
        return new PduPayload("UCS2", dataBytes, ToHex(udh) + ToUcs2Hex(message), udhi);
    }

    private async Task<SmsSendResult> SendPartAsync(string to, PduPayload payload, TimeSpan? timeout, CancellationToken ct)
    {
        // Snapshot the previous modem confirmation so we can detect the new one.
        string? previousReport = null;
        try
        {
            var probe = await _shell.RunAsync($"cat {ReportFile} 2>/dev/null", TimeSpan.FromSeconds(10), ct);
            if (!probe.TimedOut && probe.Success)
                previousReport = probe.Output;
        }
        catch (ZyxelRootException) { /* best effort */ }

        string command = $"/usr/sbin/smscmd sendpdu {ShellQuote(to)} {payload.Charset} {payload.Length} {ShellQuote(payload.UserDataHex)} {(payload.Udhi ? 1 : 0)}";
        CommandResult result = await _shell.RunAsync(command, TimeSpan.FromSeconds(25), ct);

        int? resultCode = null;
        if (result.Output is not null)
        {
            var m = ResultCodeRx.Match(result.Output);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int rc))
                resultCode = rc;
        }

        var outboxMatch = result.Output is null ? null : OutboxFileRx.Match(result.Output);
        string? outboxFile = outboxMatch?.Success == true ? outboxMatch.Groups[1].Value : null;

        // The modem's confirmation is the source of truth. cmctl's
        // synchronous result is unreliable on this firmware (rc 1 even when
        // the modem answers OK, and smscmd may segfault in its post-send
        // bookkeeping), so the send is judged by the fresh +CMGS report.
        string? modemReport = await WaitForModemReportAsync(previousReport,
            timeout ?? TimeSpan.FromSeconds(20), ct);
        bool accepted = modemReport is not null;

        if (!accepted)
        {
            // Failure diagnostic: mmm logs the modem's rejection reason for
            // every send (e.g. "+CME ERROR: 513" = user data over the 14-octet
            // cap or a wrong GSM 7-bit UDL, "+CME ERROR: 303" = the one-line
            // PDU format is not supported in the modem's current mode).
            try
            {
                var diag = await _shell.RunAsync(
                    "grep -a 'CME ERROR' /var/log/mmm.log 2>/dev/null | tail -1",
                    TimeSpan.FromSeconds(10), ct);
                if (!diag.TimedOut && diag.Success && !string.IsNullOrWhiteSpace(diag.Output))
                    modemReport = "(no +CMGS; mmm.log: " + diag.Output.Trim() + ")";
            }
            catch (ZyxelRootException) { /* best effort */ }
        }

        return new SmsSendResult
        {
            Accepted = accepted,
            ResultCode = resultCode,
            ModemReport = modemReport,
            OutboxFile = outboxFile,
            Output = result.Output ?? ""
        };
    }

    private static List<string> SplitMultipart(string message, SmsCharset charset)
    {
        int partSize = charset == SmsCharset.Gsm7 ? Gsm7PartLengthUdh : Ucs2PartLengthUdh;
        var parts = new List<string>();
        int start = 0;

        while (start < message.Length)
        {
            int end = Math.Min(start + partSize, message.Length);

            // A UTF-16 surrogate pair must not be split across parts. For UCS-2
            // this means dropping one preceding char so the pair (4 bytes) fits
            // the 2-byte budget; for GSM 7-bit the alphabet excludes surrogates,
            // so this only ever fires on the UCS-2 path.
            if (end < message.Length && char.IsHighSurrogate(message[end - 1]))
                end--;

            if (charset == SmsCharset.Gsm7)
            {
                // Extension characters cost 2 septets; shrink until the part fits the packed budget.
                while (end > start + 1 && Gsm7Codec.Encode(message[start..end]).Length > Gsm7PackedBudgetUdh)
                    end--;
            }

            parts.Add(message[start..end]);
            start = end;
        }

        return parts;
    }

    /// <summary>Parses the separator + hexdump output of
    /// <see cref="GetStoredAsync"/> into messages.</summary>
    internal static List<SmsMessage> ParseDump(string output)
    {
        var messages = new List<SmsMessage>();
        string? currentFile = null;
        var hex = new StringBuilder();

        void Flush()
        {
            if (currentFile is null || hex.Length == 0)
            {
                currentFile = null;
                hex.Clear();
                return;
            }

            try
            {
                byte[] bytes = Convert.FromHexString(hex.ToString());
                string json = Encoding.UTF8.GetString(bytes);
                if (SmsMessage.TryParse(currentFile, json) is { } message)
                    messages.Add(message);
            }
            catch (FormatException) { /* truncated/empty file — skip */ }

            currentFile = null;
            hex.Clear();
        }

        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd();

            if (line.StartsWith(SepMarker))
            {
                Flush();
                currentFile = line[SepMarker.Length..];
            }
            else if (currentFile is not null && HexLineRx.IsMatch(line))
            {
                hex.Append(line);
            }
            // anything else (prompts, echo residue) is ignored
        }

        Flush();
        return messages;
    }
}
