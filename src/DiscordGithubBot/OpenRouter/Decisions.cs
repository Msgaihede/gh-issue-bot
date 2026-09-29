using System.Globalization;
using System.Text.Json.Nodes;

namespace DiscordGithubBot.OpenRouter;

/// <summary>
/// One typed question for a decision model. Question keys are for code only and never reach the model, so
/// <see cref="Instructions"/> and the criteria must carry the whole meaning; they may reference state by a
/// backticked path such as <c>`report.body`</c>.
/// </summary>
public abstract record DecisionQuestion(string Instructions)
{
    internal abstract JsonObject ToJson();
}

/// <summary>An option of a <see cref="ChoiceQuestion"/>: its name as the answer returns it, and what it means.</summary>
public sealed record ChoiceOption(string Key, string Criterion);

/// <summary>Pick exactly one of a defined set. Include a no-match option whenever nothing may fit.</summary>
public sealed record ChoiceQuestion(string Instructions, IReadOnlyList<ChoiceOption> Options)
    : DecisionQuestion(Instructions)
{
    internal override JsonObject ToJson()
    {
        var criteria = new JsonObject();
        foreach (var option in Options) criteria[option.Key] = option.Criterion;
        return new JsonObject { ["type"] = "choice", ["instructions"] = Instructions, ["criteria"] = criteria };
    }
}

/// <summary>Probability that a condition holds. Absolute, so one per label when several may apply.</summary>
public sealed record NoulQuestion(string Instructions, string? IfTrue = null, string? IfFalse = null)
    : DecisionQuestion(Instructions)
{
    internal override JsonObject ToJson()
    {
        var json = new JsonObject { ["type"] = "noul", ["instructions"] = Instructions };
        if (IfTrue is not null && IfFalse is not null)
            json["criteria"] = new JsonObject { ["true"] = IfTrue, ["false"] = IfFalse };
        return json;
    }
}

/// <summary>Position along ordered levels, lowest first; each level must describe a situation on its own.</summary>
public sealed record ScoreQuestion(string Instructions, IReadOnlyList<string> Levels) : DecisionQuestion(Instructions)
{
    internal override JsonObject ToJson() => new()
    {
        ["type"] = "score",
        ["instructions"] = Instructions,
        ["criteria"] = new JsonArray(Levels.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()),
    };
}

public abstract record DecisionAnswer;

/// <summary>The chosen option plus the whole distribution, which is what ranking and gating read.</summary>
public sealed record ChoiceAnswer(string Choice, IReadOnlyDictionary<string, double> Probabilities, double? Confidence)
    : DecisionAnswer
{
    public double Probability(string option) => Probabilities.GetValueOrDefault(option);
}

/// <param name="Probability">P(yes). Near 0.5 means yes and no are about equally likely, not "medium".</param>
public sealed record NoulAnswer(double Probability) : DecisionAnswer;

/// <param name="Score">Probability-weighted level; use it to pass a threshold, never as a quantity.</param>
/// <param name="Probabilities">Probability per level index (0 = the first level sent).</param>
public sealed record ScoreAnswer(double Score, IReadOnlyDictionary<int, double> Probabilities, double? Confidence)
    : DecisionAnswer
{
    public double Probability(int level) => Probabilities.GetValueOrDefault(level);
}

/// <summary>
/// Every answer of one Decisions request, keyed like the questions were, plus the exact model build that
/// answered. Reading is strict: a missing key or an answer of the wrong type is a contract violation and
/// throws, rather than defaulting to a value the caller would then act on.
/// </summary>
public sealed class DecisionResult(string model, IReadOnlyDictionary<string, DecisionAnswer> answers)
{
    public string Model { get; } = model;

    public IReadOnlyDictionary<string, DecisionAnswer> Answers { get; } = answers;

    /// <exception cref="OpenRouterException">no answer for <paramref name="key"/>, or one of another type</exception>
    public T Get<T>(string key) where T : DecisionAnswer =>
        Answers.TryGetValue(key, out var answer)
            ? answer as T ?? throw new OpenRouterException(
                $"Decision '{key}' was answered as {answer.GetType().Name}, expected {typeof(T).Name}.",
                null, isTransient: false)
            : throw new OpenRouterException($"Decision '{key}' was not answered.", null, isTransient: false);

    /// <summary>Parses the <c>answers</c> object of a Decisions response. Answers of an unknown type are left out.</summary>
    internal static IReadOnlyDictionary<string, DecisionAnswer> ParseAnswers(JsonObject answers)
    {
        var parsed = new Dictionary<string, DecisionAnswer>();

        foreach (var (key, node) in answers)
        {
            if (node is not JsonObject answer) continue;

            DecisionAnswer? value = answer["type"]?.GetValue<string>() switch
            {
                "noul" => new NoulAnswer(answer["noul"]!.GetValue<double>()),
                "choice" => ParseChoice(answer),
                "score" => ParseScore(answer),
                _ => null,
            };

            if (value is not null) parsed[key] = value;
        }

        return parsed;
    }

    /// <summary>
    /// The schema marks <c>probabilities</c> optional; without it the chosen option is all the evidence
    /// there is, so it stands in as certain rather than leaving every probability at zero.
    /// </summary>
    private static ChoiceAnswer ParseChoice(JsonObject answer)
    {
        var choice = answer["choice"]!.GetValue<string>();
        var probabilities = answer["probabilities"] is JsonObject p
            ? p.ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<double>())
            : new Dictionary<string, double> { [choice] = 1.0 };

        return new ChoiceAnswer(choice, probabilities, answer["confidence"]?.GetValue<double>());
    }

    private static ScoreAnswer ParseScore(JsonObject answer)
    {
        var score = answer["score"]!.GetValue<double>();
        var probabilities = answer["probabilities"] is JsonObject p
            ? p.ToDictionary(
                kv => int.Parse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture),
                kv => kv.Value!.GetValue<double>())
            : new Dictionary<int, double> { [(int)Math.Round(score, MidpointRounding.AwayFromZero)] = 1.0 };

        return new ScoreAnswer(score, probabilities, answer["confidence"]?.GetValue<double>());
    }
}
