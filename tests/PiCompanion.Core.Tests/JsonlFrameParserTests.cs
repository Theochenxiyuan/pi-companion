using System.Text;
using PiCompanion.Application.PiRpc;

namespace PiCompanion.Core.Tests;

public sealed class JsonlFrameParserTests
{
    [Fact]
    public void Append_HandlesSplitUtf8SplitJsonAndMultipleFrames()
    {
        var parser = new JsonlFrameParser();
        var bytes = Encoding.UTF8.GetBytes("{\"text\":\"你\"}\r\n{\"type\":\"agent_end\"}\n");
        var split = Array.IndexOf(bytes, (byte)0xE4) + 1;

        Assert.Empty(parser.Append(bytes.AsSpan(0, split)));
        var frames = parser.Append(bytes.AsSpan(split));

        Assert.Equal(2, frames.Count);
        Assert.Equal("{\"text\":\"你\"}", frames[0]);
        Assert.Equal("{\"type\":\"agent_end\"}", frames[1]);
        Assert.Null(parser.Complete());
    }

    [Fact]
    public void Complete_ReturnsFinalFrameWithoutLf()
    {
        var parser = new JsonlFrameParser();

        Assert.Empty(parser.Append("{\"type\":\"response\"}"u8));

        Assert.Equal("{\"type\":\"response\"}", parser.Complete());
        Assert.Null(parser.Complete());
    }

    [Fact]
    public void Append_RejectsOversizedFrame()
    {
        var parser = new JsonlFrameParser(8);

        var exception = Assert.Throws<InvalidDataException>(() => parser.Append("123456789"u8));

        Assert.Contains("exceeds", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_AllowsNewlineToFlushNearLimitBeforeMoreBytes()
    {
        var parser = new JsonlFrameParser(8);

        Assert.Empty(parser.Append("1234567"u8));
        var frames = parser.Append("\nabc\n"u8);

        Assert.Equal(new[] { "1234567", "abc" }, frames);
    }
}
