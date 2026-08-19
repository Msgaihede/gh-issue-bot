using Discord;
using DiscordGithubBot.Pipeline;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.Discord;

/// <summary>
/// Pulls modal attachments into memory while their CDN links are still alive (Discord expires them after
/// roughly a day) and drops anything the pipeline cannot use: non-images, oversized files, failed downloads.
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
                payloads.Add(new AttachmentPayload(attachment.Filename, attachment.ContentType, bytes));
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
