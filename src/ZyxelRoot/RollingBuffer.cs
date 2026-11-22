using System.Text;

namespace ZyxelRoot;

/// <summary>
/// A growable, trimmable byte window addressed by absolute stream offsets.
/// The reader appends; command waiters search and slice by absolute position.
/// </summary>
internal sealed class RollingBuffer
{
    private byte[] _data;
    private int _start;
    private int _end;
    private long _base; // absolute offset of _data[0]

    public RollingBuffer(int initialCapacity = 64 * 1024)
    {
        _data = new byte[Math.Max(1024, initialCapacity)];
    }

    /// <summary>Absolute offset of the oldest retained byte.</summary>
    public long Start => _base + _start;

    /// <summary>Absolute offset one past the newest byte.</summary>
    public long End => _base + _end;

    public int Length => _end - _start;

    public void Append(ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty) return;

        if (_data.Length - _end < source.Length)
            Grow(source.Length);

        source.CopyTo(_data.AsSpan(_end));
        _end += source.Length;
    }

    private void Grow(int extra)
    {
        int live = _end - _start;
        int needed = live + extra;

        if (_data.Length >= needed)
        {
            // Compact in place.
            Buffer.BlockCopy(_data, _start, _data, 0, live);
            _base += _start;
            _start = 0;
            _end = live;
            return;
        }

        int size = _data.Length;
        while (size < needed) size *= 2;

        var next = new byte[size];
        Buffer.BlockCopy(_data, _start, next, 0, live);
        _base += _start;
        _data = next;
        _start = 0;
        _end = live;
    }

    /// <summary>Finds <paramref name="needle"/> at or after <paramref name="fromAbsolute"/>.</summary>
    public long IndexOf(ReadOnlySpan<byte> needle, long fromAbsolute)
    {
        if (needle.IsEmpty) return -1;

        long from = Math.Max(fromAbsolute, Start);
        if (from >= End) return -1;

        int offset = (int)(from - Start) + _start;
        var haystack = _data.AsSpan(offset, _end - offset);
        int index = haystack.IndexOf(needle);
        return index < 0 ? -1 : _base + offset + index;
    }

    /// <summary>Finds a single byte at or after <paramref name="fromAbsolute"/>.</summary>
    public long IndexOfByte(byte value, long fromAbsolute)
    {
        long from = Math.Max(fromAbsolute, Start);
        if (from >= End) return -1;

        int offset = (int)(from - Start) + _start;
        int index = _data.AsSpan(offset, _end - offset).IndexOf(value);
        return index < 0 ? -1 : _base + offset + index;
    }

    public byte[] GetBytes(long fromAbsolute, long toAbsolute)
    {
        long from = Math.Clamp(fromAbsolute, Start, End);
        long to = Math.Clamp(toAbsolute, from, End);
        int count = (int)(to - from);
        if (count <= 0) return Array.Empty<byte>();

        var result = new byte[count];
        Buffer.BlockCopy(_data, (int)(from - Start) + _start, result, 0, count);
        return result;
    }

    public string GetUtf8(long fromAbsolute, long toAbsolute)
    {
        var bytes = GetBytes(fromAbsolute, toAbsolute);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Drops bytes older than <paramref name="absolute"/>.</summary>
    public void TrimBefore(long absolute)
    {
        if (absolute <= Start) return;

        if (absolute >= End)
        {
            _base = absolute;
            _start = 0;
            _end = 0;
            return;
        }

        _start += (int)(absolute - Start);
    }
}
