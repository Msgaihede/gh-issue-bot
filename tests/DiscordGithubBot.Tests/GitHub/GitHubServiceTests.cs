using System.Net;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.GitHub;
using DiscordGithubBot.Tests.TestDoubles;

namespace DiscordGithubBot.Tests.GitHub;

public class GitHubServiceTests
{
    private static readonly AppConfig App = new()
    {
        Name = "MyApp", Repo = "owner/repo", GitHubToken = "PAT123",
        GuildIds = [1UL], ChannelIds = [2UL],
    };

    /// <summary>The PAT path through the auth provider: the token the app configures is the token sent.</summary>
    private static GitHubService Service(FakeHttpMessageHandler fake) =>
        new(fake.CreateClient(), new PassThroughAuthProvider());

    [Fact]
    public async Task CreateIssue_posts_title_body_label_and_bearer_token()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Post, "repos/owner/repo/issues", HttpStatusCode.Created,
            """{"number":42,"title":"T","body":"B","state":"open","updated_at":"2026-08-18T00:00:00Z","closed_at":null,"html_url":"https://github.com/owner/repo/issues/42"}""");
        var svc = Service(fake);

        var issue = await svc.CreateIssueAsync(App, "T", "B", "bug");

        Assert.Equal(42, issue.Number);
        Assert.Equal("https://github.com/owner/repo/issues/42", issue.HtmlUrl);
        var req = Assert.Single(fake.Requests);
        Assert.Equal("Bearer PAT123", req.AuthHeader);
        Assert.Contains("\"bug\"", req.Body);
        Assert.Contains("\"T\"", req.Body);
    }

    [Fact]
    public async Task AddComment_returns_comment_url()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Post, "repos/owner/repo/issues/7/comments", HttpStatusCode.Created,
            """{"html_url":"https://github.com/owner/repo/issues/7#issuecomment-1"}""");
        var svc = Service(fake);

        var url = await svc.AddCommentAsync(App, 7, "hello");

        Assert.Equal("https://github.com/owner/repo/issues/7#issuecomment-1", url);
    }

    [Fact]
    public async Task AddComment_fails_when_github_returns_no_comment_url()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Post, "repos/owner/repo/issues/7/comments", HttpStatusCode.Created, """{"id":1}""");
        var svc = Service(fake);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => svc.AddCommentAsync(App, 7, "hello"));

        Assert.Contains("owner/repo#7", ex.Message);
    }

    [Fact]
    public async Task ListIssues_filters_pull_requests_and_maps_fields()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Get, "repos/owner/repo/issues?", HttpStatusCode.OK,
            """
            [
              {"number":1,"title":"Bug A","body":"b","state":"open","updated_at":"2026-08-01T10:00:00Z","closed_at":null,"html_url":"u1"},
              {"number":2,"title":"PR","body":"p","state":"open","updated_at":"2026-08-01T10:00:00Z","closed_at":null,"html_url":"u2","pull_request":{"url":"x"}},
              {"number":3,"title":"Bug B","body":null,"state":"closed","updated_at":"2026-08-02T10:00:00Z","closed_at":"2026-08-02T10:00:00Z","html_url":"u3"}
            ]
            """);
        var svc = Service(fake);

        var issues = await svc.ListIssuesAsync(App, "all", null);

        Assert.Equal([1, 3], issues.Select(i => i.Number).ToArray());
        Assert.Equal("", issues[1].Body);           // null body -> empty string
        Assert.NotNull(issues[1].ClosedAtUtc);
        Assert.Equal(DateTimeKind.Utc, issues[0].UpdatedAtUtc.Kind);
    }

    [Fact]
    public async Task ListIssues_passes_state_and_since()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Get, "repos/owner/repo/issues?", HttpStatusCode.OK, "[]");
        var svc = Service(fake);

        await svc.ListIssuesAsync(App, "all", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));

        var url = Assert.Single(fake.Requests).Url;
        Assert.Contains("state=all", url);
        Assert.Contains("since=2026-08-01T00%3A00%3A00Z", url);
        Assert.Contains("per_page=100", url);
    }

    [Fact]
    public async Task Failure_status_throws()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Post, "repos/owner/repo/issues", HttpStatusCode.Unauthorized, "{}");
        var svc = Service(fake);
        await Assert.ThrowsAsync<HttpRequestException>(() => svc.CreateIssueAsync(App, "t", "b", "bug"));
    }
}
