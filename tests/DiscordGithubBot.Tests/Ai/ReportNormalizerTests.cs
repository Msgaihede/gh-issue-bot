using System.Net;
using DiscordGithubBot.Ai;
using DiscordGithubBot.Data;
using DiscordGithubBot.OpenRouter;
using DiscordGithubBot.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordGithubBot.Tests.Ai;

public class ReportNormalizerTests
{
    private static ReportNormalizer Sut(FakeChat chat) => new(chat, NullLogger<ReportNormalizer>.Instance);

    private const string Valid =
        """{"titles":["Save button does nothing on mobile","Mobile save is ignored","Tapping Save has no effect"],"body":"## Description\nIt broke."}""";

    [Fact]
    public async Task Returns_the_titles_and_body_from_the_model()
    {
        var draft = await Sut(new FakeChat(Valid)).NormalizeAsync(ReportType.Bug, "MyApp", "it broke");

        Assert.Equal(3, draft.Titles.Count);
        Assert.Equal("Save button does nothing on mobile", draft.Titles[0]);
        Assert.StartsWith("## Description", draft.Body);
    }

    /// <summary>The rules forbid trailing periods and quotes; the output is cleaned rather than trusted.</summary>
    [Fact]
    public async Task Titles_are_cleaned_deduplicated_and_blank_ones_dropped()
    {
        var chat = new FakeChat("""{"titles":["\"Crash on save.\"","crash on save","  ",""],"body":"b"}""");

        var draft = await Sut(chat).NormalizeAsync(ReportType.Bug, "MyApp", "x");

        Assert.Equal(["Crash on save"], draft.Titles);
    }

    [Fact]
    public async Task A_draft_without_any_usable_title_is_retried()
    {
        var chat = new FakeChat("""{"titles":[" "],"body":"b"}""", Valid);

        var draft = await Sut(chat).NormalizeAsync(ReportType.Bug, "MyApp", "x");

        Assert.Equal(2, chat.Calls.Count);
        Assert.Equal(3, draft.Titles.Count);
    }

    [Fact]
    public async Task A_transient_failure_is_retried_once_then_succeeds()
    {
        var chat = new FakeChat(new OpenRouterException("queue", null, isTransient: true), Valid);

        await Sut(chat).NormalizeAsync(ReportType.Bug, "MyApp", "x");

        Assert.Equal(2, chat.Calls.Count);
    }

    [Fact]
    public async Task Throws_after_two_failures()
    {
        var chat = new FakeChat(new OpenRouterException("bad json", null, isTransient: false));

        await Assert.ThrowsAsync<NormalizationException>(
            () => Sut(chat).NormalizeAsync(ReportType.Bug, "MyApp", "x"));
        Assert.Equal(2, chat.Calls.Count);
    }

    /// <summary>Exhausted credits fail the second attempt the same way; the reporter hears about it at once.</summary>
    [Fact]
    public async Task A_refused_request_is_not_retried()
    {
        var chat = new FakeChat(new OpenRouterException("402", HttpStatusCode.PaymentRequired, isTransient: false));

        await Assert.ThrowsAsync<NormalizationException>(
            () => Sut(chat).NormalizeAsync(ReportType.Bug, "MyApp", "x"));
        Assert.Single(chat.Calls);
    }

    [Fact]
    public async Task Cancellation_of_our_own_token_still_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var chat = new FakeChat(new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Sut(chat).NormalizeAsync(ReportType.Bug, "MyApp", "x", cts.Token));
    }

    [Fact]
    public async Task The_call_is_interactive_and_carries_the_report_in_the_user_message()
    {
        var chat = new FakeChat(Valid);

        await Sut(chat).NormalizeAsync(ReportType.Bug, "MyApp", "the save button does nothing");

        var (prompt, urgency) = Assert.Single(chat.Calls);
        Assert.Equal(ChatUrgency.Interactive, urgency);
        Assert.Equal(ChatTier.Regular, prompt.Tier); // the reporter waits on this one; flex doubled it
        Assert.Contains("the save button does nothing", prompt.User);
        Assert.DoesNotContain("the save button does nothing", prompt.System);
    }

    [Fact]
    public void Bug_and_feature_prompts_use_their_own_templates()
    {
        var bug = ReportNormalizer.SystemPrompt(ReportType.Bug);
        var feature = ReportNormalizer.SystemPrompt(ReportType.Feature);

        Assert.Contains("Steps to Reproduce", bug);
        Assert.DoesNotContain("Steps to Reproduce", feature);
        Assert.Contains("Proposed Solution", feature);
    }

    /// <summary>The owner's requirement: a title must say what the issue actually is.</summary>
    [Theory]
    [InlineData(ReportType.Bug)]
    [InlineData(ReportType.Feature)]
    public void The_prompt_demands_specific_titles_and_bans_generic_ones(ReportType type)
    {
        var prompt = ReportNormalizer.SystemPrompt(type);

        Assert.Contains($"exactly {ReportNormalizer.TitleCount} alternative titles", prompt);
        Assert.Contains("never be generic", prompt);
        Assert.Contains("\"Issue with the app\"", prompt);
        Assert.Contains("concrete details", prompt);
    }
}
