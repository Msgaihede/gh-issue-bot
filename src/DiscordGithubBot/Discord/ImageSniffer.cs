using System.Diagnostics.CodeAnalysis;

namespace DiscordGithubBot.Discord;

/// <summary>
/// Identifies an image by the magic bytes at the head of the file. Declared content types are worthless
/// here: Discord infers them from the file name the uploading client chose, so <c>evil.exe</c> renamed to
/// <c>evil.png</c> arrives declared <c>image/png</c> — and whatever we accept ends up permanently hosted
/// under the repository's own identity. Only the formats Discord actually produces and GitHub actually
/// renders inline are allowed: PNG, JPEG, GIF, WebP. SVG is deliberately absent — it is XML that can carry
/// scripts (an XSS vector wherever it is served inline), and being text it has no magic number to sniff.
/// </summary>
public static class ImageSniffer
{
    private static ReadOnlySpan<byte> Png => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> Jpeg => [0xFF, 0xD8, 0xFF];

    /// <summary>Offset of the RIFF container's format field: "RIFF", a 4-byte size, then the four-cc.</summary>
    private const int RiffFormatOffset = 8;

    /// <param name="bytes">The downloaded file, or any prefix of it — a buffer too short to hold a
    /// signature is simply not a match, never an error.</param>
    /// <returns>true and the canonical content type when the bytes are one of the allowed formats.</returns>
    public static bool TryDetect(ReadOnlySpan<byte> bytes, [NotNullWhen(true)] out string? contentType)
    {
        if (bytes.StartsWith(Png))
        {
            contentType = "image/png";
        }
        else if (bytes.StartsWith(Jpeg))
        {
            contentType = "image/jpeg";
        }
        else if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8))
        {
            contentType = "image/gif";
        }
        // A RIFF file is only WebP when its format field says so — .wav and .avi share the container.
        else if (bytes.StartsWith("RIFF"u8)
            && bytes.Length >= RiffFormatOffset + 4
            && bytes[RiffFormatOffset..].StartsWith("WEBP"u8))
        {
            contentType = "image/webp";
        }
        else
        {
            contentType = null;
        }

        return contentType is not null;
    }
}
