using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ZyxelRoot.Sms;

/// <summary>Message state as stored in the router's Inbox JSON.</summary>
public enum SmsState
{
    Unknown,
    RecUnread,
    RecRead,
    StoUnsent,
    StoSent
}

/// <summary>
/// One SMS stored in <c>/xdata/SMS/Inbox/</c> on the router (one JSON file per
/// message; received and sent messages share the directory — sent ones carry
/// <see cref="SmsState.StoSent"/> and <see cref="IsSent"/> is true).
/// </summary>
public sealed class SmsMessage
{
    /// <summary>Inbox file name, e.g. <c>sms_26_11_19_19_31_11_81.txt</c>.</summary>
    public required string File { get; init; }

    /// <summary>Originator as stored (<c>From</c>); for sent messages the destination.</summary>
    public string Sender { get; init; } = "";

    public SmsState State { get; init; }

    /// <summary>True when this is a sent (outbox) record, not a received message.</summary>
    public bool IsSent => State == SmsState.StoSent;

    /// <summary>Timestamp decoded from the PDU SCTS, or <c>null</c> when unparseable.</summary>
    public DateTimeOffset? TimeStamp { get; init; }

    /// <summary>
    /// Decoded message text. <c>null</c> when the stored user data is not
    /// decodable as its declared character set — on this router most received
    /// messages are encrypted carrier notifications and keep their raw hex
    /// in <see cref="UserDataHex"/>.
    /// </summary>
    public string? Body { get; init; }

    /// <summary>Raw user data hex from the PDU (UDH included when <see cref="Udh"/>).</summary>
    public string UserDataHex { get; init; } = "";

    /// <summary>Full raw PDU hex from the modem.</summary>
    public string PduHex { get; init; } = "";

    /// <summary>0 = GSM 7-bit, 1 = IRA, 2 = UCS-2 (from the PDU DCS).</summary>
    public int CharacterSet { get; init; }

    public int? MsgId { get; init; }
    public int? SegmentSequence { get; init; }
    public int? SegmentInTotal { get; init; }

    /// <summary>True when a user data header (e.g. concat UDH) precedes the payload.</summary>
    public bool Udh { get; init; }

    public override string ToString()
    {
        string who = IsSent ? $"to {Sender}" : $"from {Sender}";
        string body = Body ?? $"(undecodable, {UserDataHex.Length / 2} raw bytes)";
        string when = TimeStamp?.ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "?";
        return $"[{when}] {who} {State}: {body}";
    }

    /// <summary>
    /// Parses one Inbox JSON file (pretty-printed, one message per file) and
    /// decodes the body. Returns <c>null</c> when the JSON is malformed.
    /// </summary>
    public static SmsMessage? TryParse(string file, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string StateJson = root.TryGetProperty("State", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()!
                : "";

            string From = root.TryGetProperty("From", out var f) && f.ValueKind == JsonValueKind.String
                ? f.GetString()!
                : "";

            int charset = root.TryGetProperty("CharacterSet", out var cs) && cs.ValueKind == JsonValueKind.Number
                ? cs.GetInt32()
                : 0;

            string userData = root.TryGetProperty("UserData", out var ud) && ud.ValueKind == JsonValueKind.String
                ? ud.GetString()!
                : "";

            string pdu = root.TryGetProperty("PduContent", out var pc) && pc.ValueKind == JsonValueKind.String
                ? pc.GetString()!
                : "";

            bool udhi = root.TryGetProperty("UDHI", out var u) && u.ValueKind == JsonValueKind.True;

            return new SmsMessage
            {
                File = file,
                Sender = From,
                State = StateJson switch
                {
                    "REC UNREAD" => SmsState.RecUnread,
                    "REC READ" => SmsState.RecRead,
                    "STO UNSENT" => SmsState.StoUnsent,
                    "STO SENT" => SmsState.StoSent,
                    _ => SmsState.Unknown
                },
                TimeStamp = TryParseTimeStamp(
                    root.TryGetProperty("TimeStamp", out var ts) && ts.ValueKind == JsonValueKind.String
                        ? ts.GetString()
                        : null),
                UserDataHex = userData,
                PduHex = pdu,
                CharacterSet = charset,
                Udh = udhi,
                MsgId = TryInt(root, "MsgID"),
                SegmentSequence = TryInt(root, "MsgSegmentSequence"),
                SegmentInTotal = TryInt(root, "MsgSegmentInTotal"),
                Body = TryDecodeBody(charset, userData, udhi) ?? TryPlainText(pdu)
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? TryInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    /// <summary>
    /// Device TimeStamp format: <c>26/11/19,19:31:11+04</c> (yy/MM/dd,HH:mm:ss±zz).
    /// </summary>
    private static DateTimeOffset? TryParseTimeStamp(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return null;

        string t = raw.TrimEnd('/').TrimEnd();
        if (t.Length < 16)
            return null;
        if (t[2] != '/' || t[5] != '/' || (t[8] != ',' && t[8] != ' '))
            return null;
        // outbox records use "yy/MM/dd HH:mm:ss" (no offset); normalise to the
        // received form "yy/MM/dd,HH:mm:ss(+zz)".
        if (t[8] == ' ')
            t = t[..8] + "," + t[9..];

        try
        {
            int yy = int.Parse(t[0..2], CultureInfo.InvariantCulture);
            int mo = int.Parse(t[3..5], CultureInfo.InvariantCulture);
            int dd = int.Parse(t[6..8], CultureInfo.InvariantCulture);
            int hh = int.Parse(t[9..11], CultureInfo.InvariantCulture);
            int mi = int.Parse(t[12..14], CultureInfo.InvariantCulture);
            int ss = int.Parse(t[15..17], CultureInfo.InvariantCulture);

            TimeSpan offset = TimeSpan.Zero;
            if (t.Length > 18)
            {
                int sign = t[17] == '-' ? -1 : 1;
                int offH = int.Parse(t[18..20], CultureInfo.InvariantCulture);
                offset = sign * TimeSpan.FromHours(offH);
            }

            int year = 2000 + yy;
            return new DateTimeOffset(year, mo, dd, hh, mi, ss, offset);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? TryDecodeBody(int charset, string userDataHex, bool udhi)
    {
        if (userDataHex.Length == 0 || userDataHex.Length % 2 != 0)
            return null;

        try
        {
            byte[] all = Convert.FromHexString(userDataHex);

            // Skip a concat UDH when one is really present: [len][IEI][IEDL]…,
            // total header = 1 + len bytes. Only trust it when the header looks
            // like a standard concatenated-SMS header (IEI 0x00, IEDL 0x03), so
            // outbox records that already stored UDH-free data are not mangled.
            int skip = 0;
            if (udhi && all.Length >= 4 &&
                all[1] == 0x00 && all[2] == 0x03)
            {
                skip = Math.Min(1 + all[0], all.Length);
            }

            byte[] payload = all[skip..];
            if (payload.Length == 0)
                return null;

            return charset switch
            {
                0 => Gsm7Codec.Decode(payload),
                1 => Encoding.GetEncoding("ISO-8859-1").GetString(payload),
                2 => Encoding.BigEndianUnicode.GetString(payload),
                _ => null
            };
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// For sent text messages the outbox record stores <c>PduContent</c> as the
    /// raw message text, not a hex PDU. Return it as the body when it clearly
    /// is text (i.e. not a valid hex string).
    /// </summary>
    private static string? TryPlainText(string pdu)
    {
        if (pdu.Length == 0 || pdu.Length % 2 != 0)
            return pdu.Length > 0 ? pdu : null;

        foreach (char c in pdu)
        {
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex)
                return pdu;
        }

        // All hex → it is a real PDU, not plain text.
        return null;
    }
}
