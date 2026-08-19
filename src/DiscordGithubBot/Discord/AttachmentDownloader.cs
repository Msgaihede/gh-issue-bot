using Discord;
using DiscordGithubBot.Pipeline;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.Discord;

/// <summary>
/// Pulls modal attachments into memory while their CDN links are still alive (Discord expires them after
/// roughly a day) and drops anything the pipeline cannot use: non-images, oversized files, failed downloads.
/// "Non-image" means verified by signature, not declared: the type and size Discord reports come from the
/// uploading client, so they only earn a file the download, never acceptance — see <see cref="ImageSniffer"/>.
/// A skipped file is reported back by name rather than thrown, so one bad screenshot never costs a report.
/// </summary>
public sealed class AttachmentDownloader(HttpClient http, ILogger<AttachmentDownloader> logger)
{
    /// <summary>Largest screenshot accepted; matches what the issue body can reasonably carry.</summary>
    public const long MaxBytes = 10 * 1024 * 1024;

    /// <returns>The downloaded payloads plus the file names that were left out.</returns>
    public async Task<(IReadOnlyList<AttachmentPayload> Payloads, IReadOnlyList<string> Skipped)>
        DownloadAsync(IEnumerable<IAttachment> attachments, CancellationToken ct = default)
    {
        var payloads = new List<AttachmentPayload>();
        var skipped = new List<string>();

        foreach (var attachment in attachments)
        {
            // Declared metadata is only good enough to decide whether a download is worth attempting;
            // the real checks happen on the bytes below.
            if (attachment.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true
                || attachment.Size > MaxBytes)
            {
                skipped.Add(attachment.Filename);
                continue;
            }

            try
            {
                // A fresh array per attachment: the pipeline hands these bytes straight to the entity it
                // persists, so they must not be pooled or reused.
                var bytes = await http.GetByteArrayAsync(attachment.Url, ct);

                if (bytes.Length > MaxBytes)
                {
                    logger.LogWarning(
                        "Attachment {Name} downloaded {Actual} bytes, over the {Limit} byte limit; skipping it.",
                        attachment.Filename, bytes.Length, MaxBytes);
                    skipped.Add(attachment.Filename);
                    continue;
                }

                if (!ImageSniffer.TryDetect(bytes, out var sniffed))
                {
                    logger.LogWarning(
                        "Attachment {Name} is not a PNG, JPEG, GIF or WebP despite its declared type; skipping it.",
                        attachment.Filename);
                    skipped.Add(attachment.Filename);
                    continue;
                }

                // The sniffed type, never the declared one — it is what the bytes actually are, and it
                // travels on to GitHub as the upload's content type.
                payloads.Add(new AttachmentPayload(attachment.Filename, sniffed, bytes));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to download attachment {Name}.", attachment.Filename);
                skipped.Add(attachment.Filename);
            }
        }

        return (payloads, skipped);
    }
}
