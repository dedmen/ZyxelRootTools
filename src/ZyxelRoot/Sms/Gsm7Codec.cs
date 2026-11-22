namespace ZyxelRoot.Sms;

/// <summary>
/// GSM 3GPP 23.038 (ETSI TS 123 038) 7-bit codec, encode and decode.
/// </summary>
/// <remarks>
/// Tables are the 3GPP TS 23.038 default alphabet and extension table,
/// cross-checked against ETSI TS 123 038 v17.0.0 on 2026-11-20 (this pass
/// fixed five Greek-letter cells that had drifted from the spec). Cell
/// 0x09 is lowercase 'ç': the spec's 'Ç' is rendered lowercase by modern
/// handsets per the Unicode Consortium's GSM 03.38 to Unicode mapping.
///
/// Packing (3GPP TS 23.040): septets are packed LSB-first — the first
/// character in the low 7 bits of the first octet. Trailing spare bits
/// (1–6) are zero; exactly 7 spare bits get the CR filler (0x0D), which
/// the decode side trims back off. Extension characters are preceded by
/// the 0x1B (ESC) prefix septet; 0x1B 0x1B (SS2 national shift) is not
/// supported and makes the payload undecodable.
///
/// <see cref="Decode(ReadOnlySpan{byte})"/> returns <c>null</c> when any
/// septet is unmapped rather than garbage — the stored user data on this
/// device is frequently an encrypted carrier blob, not GSM7 text.
/// </remarks>
public static class Gsm7Codec
{
    /// <summary>ESC (0x1B): extension-table prefix septet.</summary>
    private const int Escape = 0x1B;

    /// <summary>3GPP 23.038 Table 1: default (basic) alphabet, indices 0x00–0x7F.</summary>
    private static readonly string?[] DefaultAlphabet =
    [
        // 0x00
        "\u0040", "\u00A3", "\u0024", "\u00A5", "\u00E8", "\u00E9", "\u00F9", "\u00EC",
        "\u00F2", "\u00E7", "\n", "\u00D8", "\u00F8", "\r", "\u00C5", "\u00E5",
        // 0x10
        "\u0394", "_", "\u03A6", "\u0393", "\u039B", "\u03A9", "\u03A0", "\u03A8",
        "\u03A3", "\u0398", "\u039E", null, "\u00C6", "\u00E6", "\u00DF", "\u00C9",
        // 0x20
        " ", "!", "\"", "#", "\u00A4", "%", "&", "'",
        "(", ")", "*", "+", ",", "-", ".", "/",
        // 0x30
        "0", "1", "2", "3", "4", "5", "6", "7",
        "8", "9", ":", ";", "<", "=", ">", "?",
        // 0x40
        "\u00A1", "A", "B", "C", "D", "E", "F", "G",
        "H", "I", "J", "K", "L", "M", "N", "O",
        // 0x50
        "P", "Q", "R", "S", "T", "U", "V", "W",
        "X", "Y", "Z", "\u00C4", "\u00D6", "\u00D1", "\u00DC", "\u00A7",
        // 0x60
        "\u00BF", "a", "b", "c", "d", "e", "f", "g",
        "h", "i", "j", "k", "l", "m", "n", "o",
        // 0x70
        "p", "q", "r", "s", "t", "u", "v", "w",
        "x", "y", "z", "\u00E4", "\u00F6", "\u00F1", "\u00FC", "\u00E0",
    ];

    /// <summary>
    /// 3GPP 23.038 extension table (defined entries only). Index is the
    /// septet that follows the 0x1B prefix.
    /// </summary>
    private static readonly Dictionary<int, string> ExtensionAlphabet = new()
    {
        [0x0A] = "\f",        // FF (form feed; treated as line feed by handsets)
        [0x0D] = "\r",        // CR2
        [0x14] = "^",
        [0x28] = "{",
        [0x29] = "}",
        [0x2F] = "\\",
        [0x3C] = "[",
        [0x3D] = "~",
        [0x3E] = "]",
        [0x40] = "|",
        [0x65] = "\u20AC",    // €
    };

    private static readonly Dictionary<char, int> DefaultEncodeMap = BuildDefaultEncodeMap();
    private static readonly Dictionary<char, int> ExtensionEncodeMap = BuildExtensionEncodeMap();

    private static Dictionary<char, int> BuildDefaultEncodeMap()
    {
        var map = new Dictionary<char, int>();
        for (int i = 0; i < DefaultAlphabet.Length; i++)
            if (DefaultAlphabet[i] is { Length: 1 } ch)
                map.TryAdd(ch[0], i);
        return map;
    }

    private static Dictionary<char, int> BuildExtensionEncodeMap()
    {
        var map = new Dictionary<char, int>();
        foreach (var (code, text) in ExtensionAlphabet)
            map.TryAdd(text[0], code);
        return map;
    }

    /// <summary>True when every character of <paramref name="text"/> has a
    /// GSM 7-bit encoding (default or extension table).</summary>
    public static bool CanEncode(string text)
    {
        if (text is null)
            throw new ArgumentNullException(nameof(text));
        foreach (char c in text)
            if (!DefaultEncodeMap.ContainsKey(c) && !ExtensionEncodeMap.ContainsKey(c))
                return false;
        return true;
    }

    /// <summary>
    /// Packs <paramref name="text"/> as GSM 7-bit data (7 bits per
    /// character, LSB-first). Trailing spare bits (1–6) are zero; exactly
    /// 7 spare bits get the CR filler (0x0D), which
    /// <see cref="Decode(ReadOnlySpan{byte})"/> trims back off.
    /// </summary>
    public static byte[] Encode(string text)
    {
        if (text is null)
            throw new ArgumentNullException(nameof(text));

        var septs = new List<int>(text.Length);
        foreach (char c in text)
        {
            if (DefaultEncodeMap.TryGetValue(c, out int direct))
            {
                septs.Add(direct);
                continue;
            }

            if (ExtensionEncodeMap.TryGetValue(c, out int ext))
            {
                septs.Add(Escape);
                septs.Add(ext);
                continue;
            }

            throw new ArgumentException($"character U+{(int)c:X4} is not in the GSM 7-bit alphabet", nameof(text));
        }

        // 7 bits per septet: ceil(7n / 8) octets.
        int n = septs.Count;
        byte[] result = new byte[(n * 7 + 7) / 8];
        int acc = 0;
        int bits = 0;
        int pos = 0;
        foreach (int septet in septs)
        {
            acc |= septet << bits;
            bits += 7;
            while (bits >= 8)
            {
                result[pos++] = (byte)(acc & 0xFF);
                acc >>= 8;
                bits -= 8;
            }
        }

        // Final partial octet: 1–6 spare bits stay zero; exactly 7 spare bits
        // (n ≡ 7 mod 8) take the CR filler (0x0D), per 3GPP TS 23.038.
        if (n % 8 == 7)
            result[pos] = (byte)((0x0D << 1) | (acc & 1));
        else if (bits > 0)
            result[pos] = (byte)(acc & 0xFF);

        return result;
    }

    /// <summary>
    /// Unpacks 7-bit GSM packed data into text. Returns <c>null</c> when any
    /// septet is unmapped (e.g. encrypted user data or an unsupported
    /// national shift), so callers keep the raw hex instead of showing
    /// garbage.
    /// </summary>
    public static string? Decode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return null;

        var sb = new System.Text.StringBuilder();
        int buffer = 0;
        int bits = 0;

        foreach (byte b in data)
        {
            buffer |= b << bits;
            bits += 8;

            while (bits >= 7)
            {
                int septet = buffer & 0x7F;
                buffer >>= 7;
                bits -= 7;

                if (septet == Escape)
                {
                    if (bits < 7)
                        return null; // dangling ESC
                    int ext = buffer & 0x7F;
                    buffer >>= 7;
                    bits -= 7;
                    if (ext == Escape)
                        return null; // SS2 national shift — not supported
                    if (!ExtensionAlphabet.TryGetValue(ext, out string? ch))
                        return null;
                    sb.Append(ch);
                }
                else
                {
                    string? ch = DefaultAlphabet[septet];
                    if (ch is null)
                        return null;
                    sb.Append(ch);
                }
            }
        }

        // The CR filler for a 7-spare-bit tail can surface as one trailing \r.
        return sb.ToString().TrimEnd('\r');
    }
}
