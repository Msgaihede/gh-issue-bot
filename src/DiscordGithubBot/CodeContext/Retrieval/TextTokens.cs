using System.Text.RegularExpressions;

namespace DiscordGithubBot.CodeContext.Retrieval;

/// <summary>
/// Turns prose, paths and identifiers into comparable search terms. Code and reports name the same things
/// differently — <c>fetchCardImages</c>, <c>card_images.rs</c>, "the card pictures" — so identifiers are split at
/// case and underscore boundaries, everything is lower-cased, filler words are dropped, and a light stemmer
/// folds plurals and verb forms ("searching", "searches" → "search") together.
/// </summary>
public static partial class TextTokens
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "been", "but", "by", "can", "could", "do", "does", "for",
        "from", "had", "has", "have", "he", "her", "his", "i", "if", "in", "into", "is", "it", "its", "me", "my",
        "no", "not", "of", "on", "or", "our", "she", "so", "than", "that", "the", "their", "them", "then",
        "there", "these", "they", "this", "to", "too", "was", "we", "were", "what", "when", "which", "while",
        "who", "will", "with", "would", "you", "your", "also", "just", "only", "very", "should", "all", "any",
        "some", "such", "how", "why", "where", "after", "before", "about", "up", "out", "get", "got",
    };

    /// <summary>Runs of letters and digits; everything else separates.</summary>
    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();

    /// <summary>Case boundaries inside an identifier: <c>HTTPServer</c> → HTTP, Server; <c>fts5Query</c> → fts5, Query.</summary>
    [GeneratedRegex(@"\p{Lu}+(?=\p{Lu}\p{Ll})|\p{Lu}?\p{Ll}+\p{N}*|\p{Lu}+\p{N}*|\p{N}+")]
    private static partial Regex IdentifierParts();

    public static IEnumerable<string> Of(string text)
    {
        foreach (Match word in Words().Matches(text))
        {
            foreach (Match part in IdentifierParts().Matches(word.Value))
            {
                var token = part.Value.ToLowerInvariant();
                if (token.Length < 2 || StopWords.Contains(token)) continue;
                yield return Stem(token);
            }
        }
    }

    /// <summary>
    /// Deliberately light: enough to meet "searches"/"searching"/"search" and "images"/"image" in the middle,
    /// without the over-folding of a full Porter stemmer ("general" and "generate" stay apart).
    /// </summary>
    internal static string Stem(string word)
    {
        if (word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal)) return TrimDouble(word[..^3]);
        if (word.Length > 4 && word.EndsWith("ies", StringComparison.Ordinal)) return word[..^3] + "y";
        if (word.Length > 4 && word.EndsWith("ed", StringComparison.Ordinal)) return TrimDouble(word[..^2]);
        if (word.Length > 4 && (word.EndsWith("ches", StringComparison.Ordinal) || word.EndsWith("shes", StringComparison.Ordinal)
                                || word.EndsWith("xes", StringComparison.Ordinal) || word.EndsWith("sses", StringComparison.Ordinal)))
            return word[..^2];
        if (word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal)
            && !word.EndsWith("us", StringComparison.Ordinal) && !word.EndsWith("is", StringComparison.Ordinal))
            return word[..^1];
        return word;
    }

    /// <summary>"stopped" → "stopp" → "stop": undoes the doubled consonant a suffix brought with it.</summary>
    private static string TrimDouble(string stem) =>
        stem.Length > 3 && stem[^1] == stem[^2] && stem[^1] is not ('l' or 's' or 'z') ? stem[..^1] : stem;
}
