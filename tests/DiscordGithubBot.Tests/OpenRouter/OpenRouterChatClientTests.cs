using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.OpenRouter;
using DiscordGithubBot.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordGithubBot.Tests.OpenRouter;

public class OpenRouterChatClientTests
{
    public sealed record Answer(string Title, string[] Tags);

    private static readonly ChatPrompt Prompt = new("test_answer", "system rules", "user material");

    private readonly ScriptedHttpHandler _http = new();
    private readonly AiUsageMeter _usage = new();

    private OpenRouterChatClient Client(Action<OpenRouterOptions>? configure = null)
    {
        var options = new BotOptions();
        configure?.Invoke(options.OpenRouter);
        return new OpenRouterChatClient(_http.CreateClient(), options, _usage, NullLogger<OpenRouterChatClient>.Instance);
    }

    private static string Completion(object answer, string finishReason = "stop", decimal cost = 0.0004m) =>
        JsonSerializer.Serialize(new
        {
            model = "openai/gpt-6-luna-20260922",
            provider = "OpenAI",
            choices = new[]
            {
                new { finish_reason = finishReason, message = new { content = JsonSerializer.Serialize(answer, StructuredOutput.JsonOptions) } },
            },
            usage = new { prompt_tokens = 100, completion_tokens = 20, cost },
        });

    [Fact]
    public async Task Parses_the_structured_answer_and_records_its_cost()
    {
        _http.Then(HttpStatusCode.OK, Completion(new Answer("Crash on save", ["a", "b"])));

        var answer = await Client().CompleteAsync<Answer>(Prompt, ChatUrgency.Interactive);

        Assert.Equal("Crash on save", answer.Title);
        Assert.Equal(["a", "b"], answer.Tags);
        Assert.Equal(0.0004m, _usage.TotalCost);
        Assert.EndsWith("/api/v1/chat/completions", _http.Requests.Single().Url);
    }

    /// <summary>Flex first, regular second: the half-price tier is the default, not an opt-in.</summary>
    [Fact]
    public async Task Requests_flex_first_with_fallbacks_a_strict_schema_and_medium_reasoning()
    {
        _http.Then(HttpStatusCode.OK, Completion(new Answer("t", [])));

        await Client().CompleteAsync<Answer>(Prompt, ChatUrgency.Interactive);

        var body = _http.Requests.Single().Body!;
        Assert.Equal("openai/gpt-6-luna", body["model"]!.GetValue<string>());
        Assert.Equal(["openai/flex", "openai"], body["provider"]!["order"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.True(body["provider"]!["allow_fallbacks"]!.GetValue<bool>());
        Assert.True(body["provider"]!["require_parameters"]!.GetValue<bool>());
        Assert.Equal("medium", body["reasoning"]!["effort"]!.GetValue<string>());

        var format = body["response_format"]!["json_schema"]!;
        Assert.Equal("test_answer", format["name"]!.GetValue<string>());
        Assert.True(format["strict"]!.GetValue<bool>());
        Assert.Equal(["title", "tags"], format["schema"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()));

        var messages = body["messages"]!.AsArray();
        Assert.Equal("system rules", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("user material", messages[1]!["content"]!.GetValue<string>());
    }

    /// <summary>
    /// The case decision 71 reverted flex over: a request queued on flex while a reporter waits. Past the
    /// deadline the call goes again on the regular tier instead of waiting out the queue.
    /// </summary>
    [Fact]
    public async Task An_interactive_call_that_misses_its_deadline_is_retried_on_the_regular_tier()
    {
        _http.ThenHang().Then(HttpStatusCode.OK, Completion(new Answer("t", [])));

        var answer = await Client(o => o.ChatDeadlineSeconds = 1).CompleteAsync<Answer>(Prompt, ChatUrgency.Interactive);

        Assert.Equal("t", answer.Title);
        Assert.Equal(2, _http.Requests.Count);
        Assert.Equal(["openai"], _http.Requests[1].Body!["provider"]!["order"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    /// <summary>A regular-tier call never touches flex, first attempt or retry.</summary>
    [Fact]
    public async Task A_regular_tier_call_goes_straight_to_the_regular_providers()
    {
        _http.Then(HttpStatusCode.TooManyRequests, "{}").Then(HttpStatusCode.OK, Completion(new Answer("t", [])));

        await Client().CompleteAsync<Answer>(Prompt with { Tier = ChatTier.Regular }, ChatUrgency.Interactive);

        Assert.All(_http.Requests, r =>
            Assert.Equal(["openai"], r.Body!["provider"]!["order"]!.AsArray().Select(n => n!.GetValue<string>())));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task A_transient_error_is_retried_once(HttpStatusCode status)
    {
        _http.Then(status, """{"error":{"code":429,"message":"flex capacity"}}""")
            .Then(HttpStatusCode.OK, Completion(new Answer("t", [])));

        await Client().CompleteAsync<Answer>(Prompt, ChatUrgency.Background);

        Assert.Equal(2, _http.Requests.Count);
    }

    /// <summary>A bad key or exhausted credits will not be cured by another provider; one call, then fail.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.PaymentRequired)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task A_permanent_error_is_not_retried(HttpStatusCode status)
    {
        _http.Then(status, """{"error":{"code":402,"message":"Insufficient credits"}}""");

        var ex = await Assert.ThrowsAsync<OpenRouterException>(
            () => Client().CompleteAsync<Answer>(Prompt, ChatUrgency.Interactive));

        Assert.Single(_http.Requests);
        Assert.False(ex.IsTransient);
        Assert.Contains("Insufficient credits", ex.Message);
    }

    [Fact]
    public async Task A_second_transient_failure_surfaces()
    {
        _http.Then(HttpStatusCode.ServiceUnavailable, "{}");

        await Assert.ThrowsAsync<OpenRouterException>(
            () => Client().CompleteAsync<Answer>(Prompt, ChatUrgency.Interactive));

        Assert.Equal(2, _http.Requests.Count);
    }

    /// <summary>A completion cut off at max_tokens is half a JSON object, not a shorter answer.</summary>
    [Fact]
    public async Task A_truncated_completion_is_a_failure()
    {
        _http.Then(HttpStatusCode.OK, Completion(new Answer("t", []), finishReason: "length"));

        var ex = await Assert.ThrowsAsync<OpenRouterException>(
            () => Client().CompleteAsync<Answer>(Prompt, ChatUrgency.Interactive));
        Assert.Contains("length", ex.Message);
    }

    /// <summary>
    /// With the account's own OpenAI key behind OpenRouter, "cost" is only OpenRouter's fee (0 here) and OpenAI
    /// bills the inference directly; the meter must count what was really spent, or every log reads $0.
    /// </summary>
    [Fact]
    public async Task A_byok_call_counts_the_upstream_inference_cost()
    {
        _http.Then(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            choices = new[] { new { finish_reason = "stop", message = new { content = """{"title":"t","tags":[]}""" } } },
            usage = new { prompt_tokens = 8, completion_tokens = 6, cost = 0m, is_byok = true,
                cost_details = new { upstream_inference_cost = 0.0000019m } },
        }));

        await Client().CompleteAsync<Answer>(Prompt, ChatUrgency.Interactive);

        Assert.Equal(0.0000019m, _usage.TotalCost);
    }

    /// <summary>Without BYOK the upstream cost is already part of "cost"; adding it again would double-count.</summary>
    [Fact]
    public async Task A_non_byok_call_counts_cost_once()
    {
        _http.Then(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            choices = new[] { new { finish_reason = "stop", message = new { content = """{"title":"t","tags":[]}""" } } },
            usage = new { cost = 0.00002m, is_byok = false, cost_details = new { upstream_inference_cost = 0.00002m } },
        }));

        await Client().CompleteAsync<Answer>(Prompt, ChatUrgency.Interactive);

        Assert.Equal(0.00002m, _usage.TotalCost);
    }

    [Fact]
    public async Task An_upstream_error_mid_answer_is_retried()
    {
        _http.Then(HttpStatusCode.OK, Completion(new Answer("t", []), finishReason: "error"))
            .Then(HttpStatusCode.OK, Completion(new Answer("ok", [])));

        var answer = await Client().CompleteAsync<Answer>(Prompt, ChatUrgency.Interactive);

        Assert.Equal("ok", answer.Title);
        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public async Task Content_outside_the_schema_is_a_failure()
    {
        _http.Then(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            choices = new[] { new { finish_reason = "stop", message = new { content = "not json" } } },
        }));

        await Assert.ThrowsAsync<OpenRouterException>(
            () => Client().CompleteAsync<Answer>(Prompt, ChatUrgency.Interactive));
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_turned_into_a_retry()
    {
        _http.ThenHang();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client().CompleteAsync<Answer>(Prompt, ChatUrgency.Interactive, cts.Token));
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task Configured_providers_replace_the_defaults()
    {
        _http.Then(HttpStatusCode.OK, Completion(new Answer("t", [])));

        await Client(o => o.ChatProviders = ["azure"]).CompleteAsync<Answer>(Prompt, ChatUrgency.Interactive);

        Assert.Equal(["azure"], _http.Requests.Single().Body!["provider"]!["order"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task No_providers_and_no_effort_leave_routing_and_reasoning_to_openrouter()
    {
        _http.Then(HttpStatusCode.OK, Completion(new Answer("t", [])));

        await Client(o => { o.ChatProviders = []; o.ReasoningEffort = ""; })
            .CompleteAsync<Answer>(Prompt, ChatUrgency.Interactive);

        var body = _http.Requests.Single().Body!;
        Assert.Null(body["provider"]);
        Assert.Null(body["reasoning"]);
    }

    [Fact]
    public async Task A_prompt_can_override_the_configured_reasoning_effort()
    {
        _http.Then(HttpStatusCode.OK, Completion(new Answer("t", [])));

        await Client().CompleteAsync<Answer>(Prompt with { ReasoningEffort = "low" }, ChatUrgency.Background);

        Assert.Equal("low", _http.Requests.Single().Body!["reasoning"]!["effort"]!.GetValue<string>());
    }
}
