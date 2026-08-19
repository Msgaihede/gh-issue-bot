using DiscordGithubBot.Data;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.Ai;

/// <summary>A cleaned-up issue title and Markdown body, ready for the reporter to confirm.</summary>
public sealed record IssueDraft(string Title, string Body);

/// <summary>Thrown when the model could not produce a usable draft, even after a retry.</summary>
public sealed class NormalizationException(string message, Exception? inner = null)
    : Exception(message, inner);

public interface IReportNormalizer
{
    /// <summary>Turns raw user text into a clean issue draft. Throws NormalizationException after one retry.</summary>
    Task<IssueDraft> NormalizeAsync(ReportType type, string appName, string rawText, CancellationToken ct = default);
}

/// <summary>
/// Rewrites a free-form user report as a well-formed GitHub issue. Normalization is the one step with no
/// sensible fallback — a half-written issue is worse than none — so a failed attempt is retried once and
/// then surfaced as <see cref="NormalizationException"/> for the caller to report back to the user.
/// </summary>
public sealed class ReportNormalizer(IChatClient chat, ILogger<ReportNormalizer> logger) : IReportNormalizer
{
    /// <summary>Longest raw report handed to the model; longer reports are cut to keep the prompt bounded.</summary>
    private const int MaxRawTextChars = 4000;

    private const int Attempts = 2;

    /// <summary>Shape requested from the model; property names map to the JSON schema sent with the request.</summary>
    private sealed class IssueDraftDto
    {
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
    }

    public async Task<IssueDraft> NormalizeAsync(
        ReportType type, string appName, string rawText, CancellationToken ct = default)
    {
        var prompt = BuildPrompt(type, appName, Truncate(rawText, MaxRawTextChars));
        Exception? lastError = null;

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                var response = await chat.GetResponseAsync<IssueDraftDto>(prompt, cancellationToken: ct);

                // TryGetResult, never .Result: malformed model output is an expected case, not an exception.
                if (response.TryGetResult(out var dto) && !string.IsNullOrWhiteSpace(dto.Title))
                {
                    return new IssueDraft(
                        dto.Title.Trim(),
                        string.IsNullOrEmpty(dto.Body) ? "" : dto.Body);
                }

                logger.LogWarning(
                    "Normalization attempt {Attempt} for {App} produced no usable draft.", attempt, appName);
            }
            // Only a genuine cancellation of *our* token escapes: an HttpClient or OpenAI timeout also
            // surfaces as a TaskCanceledException, and that is an ordinary failed attempt to retry.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                logger.LogWarning(ex, "Normalization attempt {Attempt} for {App} failed.", attempt, appName);
            }
        }

        throw new NormalizationException(
            $"Could not turn the report for {appName} into an issue draft after {Attempts} attempts.", lastError);
    }

    private static string BuildPrompt(ReportType type, string appName, string rawText)
    {
        var sections = type == ReportType.Bug
            ? "## Description, ## Steps to Reproduce, ## Expected Behavior, ## Actual Behavior"
            : "## Summary, ## Motivation, ## Proposed Solution";

        var kind = type == ReportType.Bug ? "bug report" : "feature request";

        return $"""
            You are preparing a GitHub issue for the app "{appName}".

            Rewrite the {kind} below as a well-formed GitHub issue in English.

            Rules:
            - Never invent details that are not present in the report. If there is nothing to put in a
              section, omit that whole section rather than guessing or writing a placeholder.
            - Use these Markdown sections, in this order: {sections}
            - The title must be at most 80 characters, written in the imperative mood, with no trailing period.
            - Preserve the reporter's facts exactly; correct only grammar, spelling, and structure.
            - Translate the report into English if it is written in another language.

            Report from the user of {appName}:
            ---
            {rawText}
            ---
            """;
    }

    private static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars];
}
