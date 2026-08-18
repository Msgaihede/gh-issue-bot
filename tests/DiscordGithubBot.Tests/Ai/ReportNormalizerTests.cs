using DiscordGithubBot.Ai;
using DiscordGithubBot.Data;
using DiscordGithubBot.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordGithubBot.Tests.Ai;

public class ReportNormalizerTests
{
    [Fact]
    public async Task Returns_draft_from_valid_llm_json()
    {
        var chat = new FakeChatClient("""{"title":"Crash when clicking Live","body":"## Description\nCrash."}""");
        var sut = new ReportNormalizer(chat, NullLogger<ReportNormalizer>.Instance);

        var draft = await sut.NormalizeAsync(ReportType.Bug, "MyApp", "app crashed on live button");

        Assert.Equal("Crash when clicking Live", draft.Title);
        Assert.Contains("## Description", draft.Body);
        Assert.Contains("app crashed on live button", chat.Prompts[0]); // raw text reaches the prompt
        Assert.Contains("MyApp", chat.Prompts[0]);
    }

    [Fact]
    public async Task Retries_once_then_succeeds()
    {
        var chat = new FakeChatClient("not json at all", """{"title":"T","body":"B"}""");
        var sut = new ReportNormalizer(chat, NullLogger<ReportNormalizer>.Instance);
        var draft = await sut.NormalizeAsync(ReportType.Feature, "MyApp", "raw");
        Assert.Equal("T", draft.Title);
        Assert.Equal(2, chat.Prompts.Count);
    }

    [Fact]
    public async Task Throws_after_two_failures()
    {
        var chat = new FakeChatClient("garbage");
        var sut = new ReportNormalizer(chat, NullLogger<ReportNormalizer>.Instance);
        await Assert.ThrowsAsync<NormalizationException>(
            () => sut.NormalizeAsync(ReportType.Bug, "MyApp", "raw"));
    }

    [Fact]
    public async Task Bug_and_feature_prompts_differ()
    {
        var chat = new FakeChatClient("""{"title":"T","body":"B"}""");
        var sut = new ReportNormalizer(chat, NullLogger<ReportNormalizer>.Instance);
        await sut.NormalizeAsync(ReportType.Bug, "MyApp", "x");
        Assert.Contains("Steps to Reproduce", chat.Prompts[0]);
        await sut.NormalizeAsync(ReportType.Feature, "MyApp", "x");
        Assert.Contains("Motivation", chat.Prompts[1]);
    }
}
