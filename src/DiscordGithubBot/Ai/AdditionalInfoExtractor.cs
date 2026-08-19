using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.Ai;

public interface IAdditionalInfoExtractor
{
    /// <summary>
    /// What the new report adds to the existing issue: markdown when there is something, "" when the
    /// report adds nothing, null when extraction failed and the caller should fall back to the full report.
    /// </summary>
    Task<string?> ExtractAsync(
        IssueDraft draft, string existingTitle, string existingBodyExcerpt, CancellationToken ct = default);
}

/// <summary>
/// Asks the model what a confirmed-duplicate report adds to the issue it duplicates, so the comment
/// posted there says something the issue does not already say. The three-way result is deliberate:
/// "" is the model's positive statement that the report adds nothing (the caller posts attribution
/// only), while null means the extraction itself failed — and the caller falls back to posting the
/// full report, because a redundant comment is recoverable and silently dropped details are not.
/// </summary>
public sealed class AdditionalInfoExtractor(
    IChatClient chat, ILogger<AdditionalInfoExtractor> logger) : IAdditionalInfoExtractor
{
    /// <summary>Shape requested from the model; property names map to the JSON schema sent with the request.</summary>
    private sealed class AdditionalInfoDto
    {
        public bool AddsNewInformation { get; set; }
        public string AdditionalInfo { get; set; } = "";
    }

    public async Task<string?> ExtractAsync(
        IssueDraft draft, string existingTitle, string existingBodyExcerpt, CancellationToken ct = default)
    {
        try
        {
            var response = await chat.GetResponseAsync<AdditionalInfoDto>(
                BuildPrompt(draft, existingTitle, existingBodyExcerpt), cancellationToken: ct);

            // TryGetResult, never .Result: unparseable model output is an expected case, not an exception.
            if (response.TryGetResult(out var dto))
            {
                // A "yes there is something" with nothing in it is treated as nothing: posting an
                // empty details block would read worse than the plain attribution line.
                return dto.AddsNewInformation && !string.IsNullOrWhiteSpace(dto.AdditionalInfo)
                    ? dto.AdditionalInfo.Trim()
                    : "";
            }

            logger.LogWarning("The additional-info extractor returned unparseable output.");
        }
        // Only a genuine cancellation of *our* token escapes: an HttpClient or OpenAI timeout also
        // surfaces as a TaskCanceledException, and that is an ordinary failure to degrade over.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The additional-info extractor failed.");
        }

        return null;
    }

    private static string BuildPrompt(IssueDraft draft, string existingTitle, string existingBodyExcerpt) =>
        $"""
        A GitHub issue already exists, and a user has just reported the same underlying issue again.
        The new report will be posted as a comment on the existing issue — but only the parts the
        issue does not already cover are worth posting.

        Existing issue title: {existingTitle}
        Existing issue body (may be truncated):
        {existingBodyExcerpt}

        New report title: {draft.Title}
        New report body:
        {draft.Body}

        Extract only the information the new report adds to the existing issue: different reproduction
        steps, environment or version details, error messages, frequency, impact, workarounds, and the
        like. Rules:
        - Never repeat what the existing issue already says, and never rephrase it as if it were new.
        - Never invent details that are not in the new report.
        - Answer with addsNewInformation=false when the new report adds nothing meaningful.
        - When there is something new, put it in additionalInfo as brief GitHub-flavored Markdown,
          written so it can stand alone as a comment.
        """;
}
