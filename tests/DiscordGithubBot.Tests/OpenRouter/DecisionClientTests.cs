using System.Net;
using System.Text.Json.Nodes;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.OpenRouter;
using DiscordGithubBot.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordGithubBot.Tests.OpenRouter;

public class DecisionClientTests
{
    private readonly ScriptedHttpHandler _http = new();
    private readonly AiUsageMeter _usage = new();

    private DecisionClient Client() =>
        new(_http.CreateClient(), new BotOptions(), _usage, NullLogger<DecisionClient>.Instance);

    private static readonly Dictionary<string, DecisionQuestion> Questions = new()
    {
        ["kind"] = new ChoiceQuestion("Which kind?", [new("bug", "Broken."), new("feature", "Wanted.")]),
        ["is_ui"] = new NoulQuestion("Is it about the UI?", "It is.", "It is not."),
        ["same"] = new ScoreQuestion("How related?", ["different", "related", "same"]),
    };

    private const string Response = """
        {
          "model": "typesafe/jev-1.13-20260917",
          "answers": {
            "kind": { "type": "choice", "choice": "bug", "probabilities": { "bug": 0.8, "feature": 0.2 }, "confidence": 0.6 },
            "is_ui": { "type": "noul", "noul": 0.93 },
            "same": { "type": "score", "score": 1.7, "probabilities": { "0": 0.05, "1": 0.2, "2": 0.75 }, "confidence": 0.5 }
          },
          "usage": { "input_tokens": 300, "output_tokens": 30, "cost": 0.0000126 }
        }
        """;

    [Fact]
    public async Task Sends_the_pinned_model_the_state_and_each_question_in_its_wire_shape()
    {
        _http.Then(HttpStatusCode.OK, Response);

        await Client().DecideAsync("test", new JsonObject { ["report"] = "it broke" }, Questions);

        var (url, body) = _http.Requests.Single();
        Assert.EndsWith("/api/alpha/decisions", url);
        Assert.Equal("typesafe/jev-1.13", body!["model"]!.GetValue<string>());
        Assert.Equal("it broke", body["state"]!["report"]!.GetValue<string>());

        var questions = body["questions"]!;
        Assert.Equal("choice", questions["kind"]!["type"]!.GetValue<string>());
        Assert.Equal("Broken.", questions["kind"]!["criteria"]!["bug"]!.GetValue<string>());
        Assert.Equal("It is not.", questions["is_ui"]!["criteria"]!["false"]!.GetValue<string>());
        Assert.Equal(["different", "related", "same"],
            questions["same"]!["criteria"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task Parses_every_answer_type_and_records_the_cost()
    {
        _http.Then(HttpStatusCode.OK, Response);

        var result = await Client().DecideAsync("test", new JsonObject(), Questions);

        Assert.Equal("typesafe/jev-1.13-20260917", result.Model);
        var kind = result.Get<ChoiceAnswer>("kind");
        Assert.Equal("bug", kind.Choice);
        Assert.Equal(0.2, kind.Probability("feature"));
        Assert.Equal(0.93, result.Get<NoulAnswer>("is_ui").Probability);
        Assert.Equal(0.75, result.Get<ScoreAnswer>("same").Probability(2));
        Assert.Equal(0.0000126m, _usage.TotalCost);
        // Under BYOK the decision provider bills directly and the response carries no dollar figure; the
        // tokens are the billing unit the meter can still report.
        Assert.Equal(300, _usage.DecisionInputTokens);
    }

    /// <summary>A missing or mistyped answer must fail loudly — defaulting it would be acting on nothing.</summary>
    [Fact]
    public async Task A_missing_or_mistyped_answer_throws_instead_of_defaulting()
    {
        _http.Then(HttpStatusCode.OK, Response);

        var result = await Client().DecideAsync("test", new JsonObject(), Questions);

        Assert.Throws<OpenRouterException>(() => result.Get<NoulAnswer>("absent"));
        Assert.Throws<OpenRouterException>(() => result.Get<NoulAnswer>("kind"));
    }

    [Fact]
    public async Task Missing_probabilities_fall_back_to_the_chosen_option_or_level()
    {
        _http.Then(HttpStatusCode.OK, """
            {"answers": {
              "kind": { "type": "choice", "choice": "feature" },
              "same": { "type": "score", "score": 1.6 }
            }}
            """);

        var result = await Client().DecideAsync("test", new JsonObject(), Questions);

        Assert.Equal(1.0, result.Get<ChoiceAnswer>("kind").Probability("feature"));
        Assert.Equal(1.0, result.Get<ScoreAnswer>("same").Probability(2));
    }

    [Fact]
    public async Task A_transient_failure_is_retried_once()
    {
        _http.Then(HttpStatusCode.TooManyRequests, "{}").Then(HttpStatusCode.OK, Response);

        var result = await Client().DecideAsync("test", new JsonObject(), Questions);

        Assert.Equal(2, _http.Requests.Count);
        Assert.Equal("bug", result.Get<ChoiceAnswer>("kind").Choice);
    }

    [Fact]
    public async Task A_response_without_answers_is_an_error()
    {
        _http.Then(HttpStatusCode.OK, """{"model":"x"}""");

        await Assert.ThrowsAsync<OpenRouterException>(
            () => Client().DecideAsync("test", new JsonObject(), Questions));
    }
}
