using System.Text;
using DiscordGithubBot.Discord;

namespace DiscordGithubBot.Tests.Discord;

/// <summary>
/// The sniffer is the only thing standing between a renamed executable and a file permanently hosted
/// under the repository's identity, so it is pinned format by format — and, just as importantly, on the
/// shapes that must be refused: truncated headers, archives, and a RIFF container that is not WebP.
/// </summary>
public class ImageSnifferTests
{
    /// <summary>A RIFF container whose format field (bytes 8..11) is the given four-character code.</summary>
    private static byte[] Riff(string fourCc) =>
        [.. "RIFF"u8.ToArray(), 0x10, 0x00, 0x00, 0x00, .. Encoding.ASCII.GetBytes(fourCc)];

    [Fact]
    public void Detects_png()
    {
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

        Assert.True(ImageSniffer.TryDetect(bytes, out var contentType));
        Assert.Equal("image/png", contentType);
    }

    [Fact]
    public void Detects_jpeg()
    {
        byte[] bytes = [0xFF, 0xD8, 0xFF, 0xE0];

        Assert.True(ImageSniffer.TryDetect(bytes, out var contentType));
        Assert.Equal("image/jpeg", contentType);
    }

    [Theory]
    [InlineData("GIF87a")]
    [InlineData("GIF89a")]
    public void Detects_gif(string signature)
    {
        var bytes = Encoding.ASCII.GetBytes(signature + "0123");

        Assert.True(ImageSniffer.TryDetect(bytes, out var contentType));
        Assert.Equal("image/gif", contentType);
    }

    [Fact]
    public void Detects_webp()
    {
        Assert.True(ImageSniffer.TryDetect(Riff("WEBP"), out var contentType));
        Assert.Equal("image/webp", contentType);
    }

    [Fact]
    public void Rejects_an_empty_buffer()
    {
        Assert.False(ImageSniffer.TryDetect(ReadOnlySpan<byte>.Empty, out var contentType));
        Assert.Null(contentType);
    }

    [Fact]
    public void Rejects_a_truncated_png_signature()
    {
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47];

        Assert.False(ImageSniffer.TryDetect(bytes, out var contentType));
        Assert.Null(contentType);
    }

    [Fact]
    public void Rejects_a_riff_header_too_short_to_carry_a_format()
    {
        Assert.False(ImageSniffer.TryDetect("RIFF"u8.ToArray(), out var contentType));
        Assert.Null(contentType);
    }

    [Fact]
    public void Rejects_a_wav_file_even_though_it_is_riff()
    {
        Assert.False(ImageSniffer.TryDetect(Riff("WAVE"), out var contentType));
        Assert.Null(contentType);
    }

    [Fact]
    public void Rejects_a_windows_executable()
    {
        byte[] bytes = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00];

        Assert.False(ImageSniffer.TryDetect(bytes, out var contentType));
        Assert.Null(contentType);
    }

    [Fact]
    public void Rejects_a_zip_archive()
    {
        byte[] bytes = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00];

        Assert.False(ImageSniffer.TryDetect(bytes, out var contentType));
        Assert.Null(contentType);
    }

    [Fact]
    public void Rejects_plain_text()
    {
        Assert.False(ImageSniffer.TryDetect("this is not an image, it is prose"u8.ToArray(), out var contentType));
        Assert.Null(contentType);
    }
}
