using MistyStep.Core.Capture;
using MistyStep.Core.Tests.Support;

namespace MistyStep.Core.Tests.Capture;

public class TextRulesTests
{
    [Theory]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\r\n\r\nb\r\n", "a\n\nb\n")]
    [InlineData("a\nb", "a\nb")]
    [InlineData("hello\0", "hello")]
    [InlineData("hello\r\n\0", "hello\n")]
    [InlineData("mid\0dle", "mid\0dle")]
    public void Normalize(string raw, string expected) => Assert.Equal(expected, TextNormalizer.Normalize(raw));

    [Theory]
    [InlineData("", true)]
    [InlineData(" \t\n ", true)]
    [InlineData(" x ", false)]
    public void IsBlank(string text, bool expected) => Assert.Equal(expected, TextNormalizer.IsBlank(text));

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com/path?q=1#frag", true)]
    [InlineData("  https://example.com/  ", true)]
    [InlineData("https://example.com\n", true)]
    [InlineData("ftp://example.com", false)]
    [InlineData("example.com", false)]
    [InlineData("see https://example.com", false)]
    [InlineData("https://example.com more", false)]
    [InlineData("https://a.com\nhttps://b.com", false)]
    [InlineData("mailto:a@b.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsUrl(string? text, bool expected) => Assert.Equal(expected, UrlDetector.IsUrl(text));

    [Fact]
    public void PngInfo_reads_size()
    {
        Assert.True(PngInfo.TryRead(TestImages.PngHeader(4096, 2160), out var w, out var h));
        Assert.Equal(4096, w);
        Assert.Equal(2160, h);
    }

    [Fact]
    public void PngInfo_rejects_bad_signature()
    {
        var bytes = TestImages.PngHeader(10, 10);
        bytes[1] = 0;
        Assert.False(PngInfo.TryRead(bytes, out _, out _));
    }

    [Fact]
    public void PngInfo_rejects_truncated_data() =>
        Assert.False(PngInfo.TryRead(TestImages.PngHeader(10, 10).AsSpan(0, 23), out _, out _));

    [Fact]
    public void PngInfo_rejects_missing_ihdr()
    {
        var bytes = TestImages.PngHeader(10, 10);
        "IDAT"u8.CopyTo(bytes.AsSpan(12));
        Assert.False(PngInfo.TryRead(bytes, out _, out _));
    }

    [Theory]
    [InlineData(0u, 10u)]
    [InlineData(10u, 0u)]
    [InlineData(0x8000_0000u, 10u)]
    public void PngInfo_rejects_invalid_dimensions(uint width, uint height) =>
        Assert.False(PngInfo.TryRead(TestImages.PngHeader(width, height), out _, out _));
}
