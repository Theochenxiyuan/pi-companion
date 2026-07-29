using System.Text;

namespace PiCompanion.Application.PiRpc;

public sealed class JsonlFrameParser
{
    public const int DefaultMaximumFrameBytes = 8 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly int _maximumFrameBytes;
    private byte[] _buffer;
    private int _count;

    public JsonlFrameParser(int maximumFrameBytes = DefaultMaximumFrameBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFrameBytes);
        _maximumFrameBytes = maximumFrameBytes;
        _buffer = new byte[Math.Min(4096, maximumFrameBytes)];
    }

    public IReadOnlyList<string> Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return [];
        }

        var frames = new List<string>();
        var segmentStart = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'\n')
            {
                continue;
            }

            AppendSegment(bytes[segmentStart..index]);
            var frameLength = _count;
            if (frameLength > 0 && _buffer[frameLength - 1] == (byte)'\r')
            {
                frameLength--;
            }

            frames.Add(StrictUtf8.GetString(_buffer, 0, frameLength));
            _count = 0;
            segmentStart = index + 1;
        }

        AppendSegment(bytes[segmentStart..]);

        return frames;
    }

    public string? Complete()
    {
        if (_count == 0)
        {
            return null;
        }

        var count = _count;
        if (_buffer[count - 1] == (byte)'\r')
        {
            count--;
        }

        var frame = StrictUtf8.GetString(_buffer, 0, count);
        _count = 0;
        return frame;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length)
        {
            return;
        }

        var capacity = _buffer.Length;
        while (capacity < required && capacity < _maximumFrameBytes)
        {
            capacity = Math.Min(_maximumFrameBytes, capacity * 2);
        }

        if (capacity < required)
        {
            throw new InvalidDataException($"Pi RPC JSONL frame exceeds {_maximumFrameBytes} bytes.");
        }

        Array.Resize(ref _buffer, capacity);
    }

    private void AppendSegment(ReadOnlySpan<byte> segment)
    {
        if (_count + segment.Length > _maximumFrameBytes)
        {
            throw new InvalidDataException($"Pi RPC JSONL frame exceeds {_maximumFrameBytes} bytes.");
        }

        EnsureCapacity(_count + segment.Length);
        segment.CopyTo(_buffer.AsSpan(_count));
        _count += segment.Length;
    }
}
