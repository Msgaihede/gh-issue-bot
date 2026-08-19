using DiscordGithubBot.Ai;
using DiscordGithubBot.Tests.TestDoubles;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordGithubBot.Tests.Ai;

public class AdditionalInfoExtractorTests
{
    private static AdditionalInfoExtractor Sut(IChatClient chat) =>
        new(chat, NullLogger<AdditionalInfoExtractor>.Instance);

    private static readonly IssueDraft Draft = new("Login crashes", "Crashes on v2.1 with error E42.");

    [Fact]
    public async Task New_information_is_returned()
    {
        var chat = new FakeChatClient(
            """{"addsNewInformation":true,"additionalInfo":"Also happens on v2.1 (error E42)."}""");

        Assert.Equal(
            "Also happens on v2.1 (error E42).",
            await Sut(chat).ExtractAsync(Draft, "Login crashes", "It crashes."));
    }

    [Fact]
    public async Task A_report_adding_nothing_returns_empty()
    {
        var chat = new FakeChatClient("""{"addsNewInformation":false,"additionalInfo":""}""");
        Assert.Equal("", await Sut(chat).ExtractAsync(Draft, "t", "b"));
    }

    [Fact]
    public async Task Claimed_new_information_that_is_blank_counts_as_nothing()
    {
        var chat = new FakeChatClient("""{"addsNewInformation":true,"additionalInfo":"   "}""");
        Assert.Equal("", await Sut(chat).ExtractAsync(Draft, "t", "b"));
    }

    [Fact]
    public async Task The_returned_info_is_trimmed()
    {
        var chat = new FakeChatClient("""{"addsNewInformation":true,"additionalInfo":"  New detail. \n"}""");
        Assert.Equal("New detail.", await Sut(chat).ExtractAsync(Draft, "t", "b"));
    }

    [Fact]
    public async Task Garbage_degrades_to_null()
    {
        var chat = new FakeChatClient("garbage");
        Assert.Null(await Sut(chat).ExtractAsync(Draft, "t", "b"));
    }

    [Fact]
    public async Task Http_timeout_degrades_to_null()
    {
        // The OpenAI client reports its own timeout as a TaskCanceledException even though nobody
        // cancelled; an unguarded catch would let it escape instead of degrading.
        var chat = new TimingOutChatClient("""{"addsNewInformation":false}""");
        Assert.Null(await Sut(chat).ExtractAsync(Draft, "t", "b", CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_of_our_own_token_still_propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var chat = new TimingOutChatClient("""{"addsNewInformation":false}""");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Sut(chat).ExtractAsync(Draft, "t", "b", cts.Token));
    }

    [Fact]
    public async Task The_existing_issue_and_the_draft_reach_the_prompt()
    {
        var chat = new FakeChatClient("""{"addsNewInformation":false}""");

        await Sut(chat).ExtractAsync(Draft, "Existing title", "Existing excerpt");

        Assert.Contains("Existing title", chat.Prompts[0]);
        Assert.Contains("Existing excerpt", chat.Prompts[0]);
        Assert.Contains("Login crashes", chat.Prompts[0]);
        Assert.Contains("Crashes on v2.1 with error E42.", chat.Prompts[0]);
    }
}
