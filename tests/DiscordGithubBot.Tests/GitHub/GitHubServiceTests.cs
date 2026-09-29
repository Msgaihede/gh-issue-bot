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
    public async Task CreateIssue_posts_title_body_labels_and_bearer_token()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Post, "repos/owner/repo/issues", HttpStatusCode.Created,
            """{"number":42,"title":"T","body":"B","state":"open","updated_at":"2026-08-18T00:00:00Z","closed_at":null,"html_url":"https://github.com/owner/repo/issues/42"}""");
        var svc = Service(fake);

        var issue = await svc.CreateIssueAsync(App, "T", "B", ["bug", "ui"]);

        Assert.Equal(42, issue.Number);
        Assert.Equal("https://github.com/owner/repo/issues/42", issue.HtmlUrl);
        var req = Assert.Single(fake.Requests);
        Assert.Equal("Bearer PAT123", req.AuthHeader);
        Assert.Contains("\"labels\":[\"bug\",\"ui\"]", req.Body);
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
    public async Task GetIssue_reads_one_issue_by_number()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Get, "repos/owner/repo/issues/42", HttpStatusCode.OK,
            """{"number":42,"title":"T","body":"B","state":"open","updated_at":"2026-08-18T00:00:00Z","closed_at":null,"html_url":"u42"}""");

        var issue = await Service(fake).GetIssueAsync(App, 42);

        Assert.Equal(42, issue.Number);
        Assert.Equal("B", issue.Body);
        Assert.Equal("Bearer PAT123", Assert.Single(fake.Requests).AuthHeader);
    }

    /// <summary>Only the body is sent: a PATCH that also carried the title or labels would undo edits to them.</summary>
    [Fact]
    public async Task UpdateIssueBody_patches_the_body_alone()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Patch, "repos/owner/repo/issues/42", HttpStatusCode.OK, """{"number":42}""");

        await Service(fake).UpdateIssueBodyAsync(App, 42, "New body");

        var req = Assert.Single(fake.Requests);
        Assert.Equal(HttpMethod.Patch, req.Method);
        Assert.Equal("""{"body":"New body"}""", req.Body);
    }

    [Fact]
    public async Task UpdateIssueBody_throws_on_a_failure_status()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Patch, "repos/owner/repo/issues/42", HttpStatusCode.Forbidden, "{}");

        await Assert.ThrowsAsync<HttpRequestException>(() => Service(fake).UpdateIssueBodyAsync(App, 42, "B"));
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
    public async Task Labels_are_listed_with_their_descriptions_across_pages()
    {
        var fake = new FakeHttpMessageHandler();
        var page1 = "[" + string.Join(",", Enumerable.Range(1, 100)
            .Select(i => $$"""{"name":"l{{i}}","description":null}""")) + "]";
        fake.When(HttpMethod.Get, "labels?per_page=100&page=1", HttpStatusCode.OK, page1);
        fake.When(HttpMethod.Get, "labels?per_page=100&page=2", HttpStatusCode.OK,
            """[{"name":"bug","description":"Something isn't working"}]""");

        var labels = await Service(fake).ListLabelsAsync(App);

        Assert.Equal(101, labels.Count);
        Assert.Equal("", labels[0].Description);
        Assert.Equal(new RepoLabel("bug", "Something isn't working"), labels[^1]);
    }

    [Fact]
    public async Task The_default_branch_head_comes_from_the_repo_then_the_branch()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Get, "repos/owner/repo/branches/main", HttpStatusCode.OK,
            """{"commit":{"sha":"c0ffee","commit":{"tree":{"sha":"7ree"}}}}""");
        fake.When(HttpMethod.Get, "repos/owner/repo", HttpStatusCode.OK, """{"default_branch":"main"}""");

        var head = await Service(fake).GetDefaultBranchHeadAsync(App);

        Assert.Equal(new BranchHead("main", "c0ffee", "7ree"), head);
    }

    [Fact]
    public async Task The_tree_lists_blobs_only_and_reports_truncation()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Get, "git/trees/7ree?recursive=1", HttpStatusCode.OK, """
            {"truncated":true,"tree":[
              {"path":"src","type":"tree","sha":"d1"},
              {"path":"src/a.cs","type":"blob","sha":"b1","size":120},
              {"path":"lib","type":"commit","sha":"m1"}]}
            """);

        var tree = await Service(fake).GetTreeAsync(App, "7ree");

        Assert.True(tree.Truncated);
        Assert.Equal(new TreeFile("src/a.cs", "b1", 120), Assert.Single(tree.Files));
    }

    [Fact]
    public async Task A_blob_is_decoded_and_cut_to_the_requested_length()
    {
        var fake = new FakeHttpMessageHandler();
        // GitHub wraps base64 content with newlines, which JSON carries as "\n".
        var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("hello world"));
        fake.When(HttpMethod.Get, "git/blobs/b1", HttpStatusCode.OK,
            "{\"encoding\":\"base64\",\"content\":\"" + base64[..8] + "\\n" + base64[8..] + "\"}");

        Assert.Equal("hello", await Service(fake).GetBlobTextAsync(App, "b1", 5));
    }

    [Fact]
    public async Task A_binary_blob_reads_as_null()
    {
        var fake = new FakeHttpMessageHandler();
        var base64 = Convert.ToBase64String([0x89, 0x50, 0x00, 0x47]);
        fake.When(HttpMethod.Get, "git/blobs/b1", HttpStatusCode.OK, $$"""{"encoding":"base64","content":"{{base64}}"}""");

        Assert.Null(await Service(fake).GetBlobTextAsync(App, "b1", 100));
    }

    [Fact]
    public async Task Failure_status_throws()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Post, "repos/owner/repo/issues", HttpStatusCode.Unauthorized, "{}");
        var svc = Service(fake);
        await Assert.ThrowsAsync<HttpRequestException>(() => svc.CreateIssueAsync(App, "t", "b", ["bug"]));
    }
}
