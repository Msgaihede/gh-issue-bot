using System.Net;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.GitHub;
using DiscordGithubBot.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordGithubBot.Tests.GitHub;

public class ImageUploaderTests
{
    private static readonly AppConfig App = new()
    {
        Name = "MyApp", Repo = "owner/repo", GitHubToken = "PAT",
        GuildIds = [1UL], ChannelIds = [2UL],
    };

    private static GitHubImageUploader Uploader(FakeHttpMessageHandler fake) =>
        new(fake.CreateClient(), NullLogger<GitHubImageUploader>.Instance);

    [Fact]
    public async Task Uses_unofficial_endpoint_when_it_works()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Post, "uploads.github.com/user-attachments/assets", HttpStatusCode.OK,
            """{"id":"x","href":"https://github.com/user-attachments/assets/abc-123"}""");
        fake.When(HttpMethod.Get, "repos/owner/repo", HttpStatusCode.OK, """{"id":1296269,"default_branch":"main"}""");

        var result = await Uploader(fake).UploadAsync(App, "shot.png", "image/png", [1, 2, 3]);

        Assert.Equal("https://github.com/user-attachments/assets/abc-123", result!.Url);
        Assert.Contains(fake.Requests, r => r.Url.Contains("repository_id=1296269"));
    }

    [Fact]
    public async Task A_parameterized_content_type_still_uses_the_unofficial_endpoint()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Post, "uploads.github.com/user-attachments/assets", HttpStatusCode.OK,
            """{"href":"https://github.com/user-attachments/assets/abc-123"}""");
        fake.When(HttpMethod.Get, "repos/owner/repo", HttpStatusCode.OK, """{"id":1,"default_branch":"main"}""");

        var result = await Uploader(fake).UploadAsync(App, "shot.png", "image/png; charset=utf-8", [1]);

        Assert.Equal("https://github.com/user-attachments/assets/abc-123", result!.Url);
        Assert.DoesNotContain(fake.Requests, r => r.Url.Contains("contents/issue-assets"));
    }

    [Fact]
    public async Task Falls_back_to_contents_api_when_unofficial_endpoint_fails()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Post, "uploads.github.com", HttpStatusCode.NotFound, "{}");
        fake.When(HttpMethod.Get, "repos/owner/repo/branches/issue-assets", HttpStatusCode.OK, """{"name":"issue-assets"}""");
        fake.When(HttpMethod.Put, "repos/owner/repo/contents/issue-assets/", HttpStatusCode.Created,
            """{"content":{"path":"issue-assets/x.png"}}""");
        fake.When(HttpMethod.Get, "repos/owner/repo", HttpStatusCode.OK, """{"id":1296269,"default_branch":"main"}""");

        var result = await Uploader(fake).UploadAsync(App, "shot.png", "image/png", [1, 2, 3]);

        Assert.NotNull(result);
        Assert.StartsWith("https://raw.githubusercontent.com/owner/repo/issue-assets/", result.Url);
        Assert.EndsWith("-shot.png", result.Url);
        var put = fake.Requests.Single(r => r.Method == HttpMethod.Put);
        Assert.Contains("issue-assets", put.Body);
    }

    [Fact]
    public async Task Creates_assets_branch_when_missing()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Post, "uploads.github.com", HttpStatusCode.Unauthorized, "{}");
        fake.When(HttpMethod.Get, "repos/owner/repo/branches/issue-assets", HttpStatusCode.NotFound, "{}");
        fake.When(HttpMethod.Get, "repos/owner/repo/git/ref/heads/main", HttpStatusCode.OK,
            """{"object":{"sha":"abc123"}}""");
        fake.When(HttpMethod.Post, "repos/owner/repo/git/refs", HttpStatusCode.Created, "{}");
        fake.When(HttpMethod.Put, "repos/owner/repo/contents/issue-assets/", HttpStatusCode.Created, "{}");
        fake.When(HttpMethod.Get, "repos/owner/repo", HttpStatusCode.OK, """{"id":1,"default_branch":"main"}""");

        var result = await Uploader(fake).UploadAsync(App, "shot.png", "image/png", [1]);

        Assert.NotNull(result);
        var refPost = fake.Requests.Single(r => r.Method == HttpMethod.Post && r.Url.Contains("git/refs"));
        Assert.Contains("refs/heads/issue-assets", refPost.Body);
        Assert.Contains("abc123", refPost.Body);
    }

    [Fact]
    public async Task Returns_null_when_everything_fails()
    {
        var fake = new FakeHttpMessageHandler(); // no routes: everything 404s
        var result = await Uploader(fake).UploadAsync(App, "shot.png", "image/png", [1]);
        Assert.Null(result);
    }
}
