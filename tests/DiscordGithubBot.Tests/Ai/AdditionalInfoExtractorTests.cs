using DiscordGithubBot.Ai;
using DiscordGithubBot.OpenRouter;
using DiscordGithubBot.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordGithubBot.Tests.Ai;

public class AdditionalInfoExtractorTests
{
    private static readonly IssueDraft Draft = new("Crash on save", "Happens on Android 14 too.");

    private static Task<string?> Extract(FakeChat chat, CancellationToken ct = default) =>
        new AdditionalInfoExtractor(chat, NullLogger<AdditionalInfoExtractor>.Instance)
            .ExtractAsync(Draft, "Crash when saving", "The app crashes on save.", ct);

    [Fact]
    public async Task New_information_is_returned()
    {
        var info = await Extract(new FakeChat("""{"addsNewInformation":true,"additionalInfo":"Also on Android 14."}"""));

        Assert.Equal("Also on Android 14.", info);
    }

    [Fact]
    public async Task A_report_adding_nothing_returns_empty()
    {
        Assert.Equal("", await Extract(new FakeChat("""{"addsNewInformation":false,"additionalInfo":""}""")));
    }

    [Fact]
    public async Task Claimed_new_information_that_is_blank_counts_as_nothing()
    {
        Assert.Equal("", await Extract(new FakeChat("""{"addsNewInformation":true,"additionalInfo":"   "}""")));
    }

    [Fact]
    public async Task The_returned_info_is_trimmed()
    {
        Assert.Equal("x", await Extract(new FakeChat("""{"addsNewInformation":true,"additionalInfo":"  x\n"}""")));
    }

    /// <summary>null — not "" — so the caller posts the full report rather than dropping it.</summary>
    [Fact]
    public async Task A_failed_call_degrades_to_null()
    {
        Assert.Null(await Extract(new FakeChat(new OpenRouterException("down", null, isTransient: true))));
    }

    [Fact]
    public async Task Cancellation_of_our_own_token_still_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Extract(new FakeChat(new OperationCanceledException(cts.Token)), cts.Token));
    }

    [Fact]
    public async Task The_existing_issue_and_the_draft_reach_the_prompt()
    {
        var chat = new FakeChat("""{"addsNewInformation":false,"additionalInfo":""}""");

        await Extract(chat);

        var user = chat.Calls.Single().Prompt.User;
        Assert.Contains("Crash when saving", user);
        Assert.Contains("The app crashes on save.", user);
        Assert.Contains("Happens on Android 14 too.", user);
    }
}
