namespace ZyxelRoot;

/// <summary>
/// Streaming telnet IAC filter: removes 3-byte WILL/WONT/DO/DONT negotiation,
/// 2-byte commands, and subnegotiation blocks, while handling sequences that are
/// split across reads. A literal 0xFF is preserved (IAC IAC).
/// The telnetd pty sends negotiation on connect; everything after it is raw text.
/// </summary>
internal sealed class TelnetIacFilter
{
    private const byte IAC = 0xFF;
    private const byte SB = 0xFA;
    private const byte SE = 0xF0;

    private readonly List<byte> _pending = new(4);

    public byte[] Process(ReadOnlySpan<byte> data)
    {
        byte[] input;
        if (_pending.Count > 0)
        {
            input = new byte[_pending.Count + data.Length];
            _pending.CopyTo(input);
            data.CopyTo(input.AsSpan(_pending.Count));
            _pending.Clear();
        }
        else
        {
            input = data.ToArray();
        }

        var output = new List<byte>(input.Length);
        int i = 0;

        while (i < input.Length)
        {
            byte b = input[i];
            if (b != IAC)
            {
                output.Add(b);
                i++;
                continue;
            }

            // A lone trailing IAC: keep it for the next read.
            if (i + 1 >= input.Length)
            {
                _pending.Add(IAC);
                break;
            }

            byte command = input[i + 1];

            // IAC IAC = literal 0xFF.
            if (command == IAC)
            {
                output.Add(IAC);
                i += 2;
                continue;
            }

            // WILL / WONT / DO / DONT <option>
            if (command is 0xFB or 0xFC or 0xFD or 0xFE)
            {
                if (i + 2 >= input.Length)
                {
                    for (int k = i; k < input.Length; k++) _pending.Add(input[k]);
                    break;
                }
                i += 3;
                continue;
            }

            // SB ... IAC SE
            if (command == SB)
            {
                int j = i + 2;
                while (j + 1 < input.Length && !(input[j] == IAC && input[j + 1] == SE))
                    j++;

                if (j + 1 >= input.Length)
                {
                    for (int k = i; k < input.Length; k++) _pending.Add(input[k]);
                    break;
                }
                i = j + 2;
                continue;
            }

            // Any other IAC <x>: two bytes.
            i += 2;
        }

        return output.ToArray();
    }
}
