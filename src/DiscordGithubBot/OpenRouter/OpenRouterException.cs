using System.Net;

namespace DiscordGithubBot.OpenRouter;

/// <summary>
/// Any failure of an OpenRouter call: an error status, a missed deadline, a network fault, or a response
/// that does not honour the contract (no answer, wrong answer type, unparseable structured output).
/// <see cref="IsTransient"/> separates what another attempt could fix — a queue, a rate limit, a flaky
/// provider — from what it cannot, such as a bad key, exhausted credits or a malformed request.
/// </summary>
public sealed class OpenRouterException(string message, HttpStatusCode? status, bool isTransient, Exception? inner = null)
    : Exception(message, inner)
{
    public HttpStatusCode? Status { get; } = status;

    public bool IsTransient { get; } = isTransient;

    /// <summary>408 and 429 are "try again", as is every 5xx; everything else is the request's own fault.</summary>
    public static bool IsTransientStatus(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
}
