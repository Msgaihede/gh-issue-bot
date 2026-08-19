using System.Net;
using Discord;
using DiscordGithubBot.Discord;
using DiscordGithubBot.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordGithubBot.Tests.Discord;

/// <summary>
/// The downloader is the trust boundary: everything Discord declares about an attachment comes from the
/// uploading client, so these tests keep the cheap declared-type pre-filter honest and prove that what
/// actually reaches the pipeline was verified against the downloaded bytes.
/// </summary>
public class AttachmentDownloaderTests
{
    private const string CdnUrl = "https://cdn.discordapp.com/attachments/1/2/shot.png";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte[] PngBytes(int filler = 4) => [.. PngSignature, .. new byte[filler]];

    private static FakeAttachment Attachment(
        string filename = "shot.png", string url = CdnUrl,
        string contentType = "image/png", int size = 1024) =>
        new() { Filename = filename, Url = url, ContentType = contentType, Size = size };

    private static AttachmentDownloader Downloader(FakeHttpMessageHandler fake) =>
        new(fake.CreateClient(), NullLogger<AttachmentDownloader>.Instance);

    [Fact]
    public async Task Accepts_a_real_png_and_carries_the_sniffed_content_type()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Get, "shot.png", HttpStatusCode.OK, PngBytes());

        var (payloads, skipped) = await Downloader(fake).DownloadAsync(
            [Attachment(contentType: "image/png; charset=utf-8")]);

        Assert.Empty(skipped);
        var payload = Assert.Single(payloads);
        Assert.Equal("shot.png", payload.FileName);
        Assert.Equal("image/png", payload.ContentType);
        Assert.Equal(PngBytes(), payload.Bytes);
    }

    [Fact]
    public async Task Skips_an_executable_wearing_a_png_name()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Get, "shot.png", HttpStatusCode.OK,
            [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00]);

        var (payloads, skipped) = await Downloader(fake).DownloadAsync([Attachment()]);

        Assert.Empty(payloads);
        Assert.Equal("shot.png", Assert.Single(skipped));
    }

    [Fact]
    public async Task Skips_a_non_image_declared_type_without_downloading_it()
    {
        var fake = new FakeHttpMessageHandler();

        var (payloads, skipped) = await Downloader(fake).DownloadAsync(
            [Attachment(filename: "payload.zip", contentType: "application/zip")]);

        Assert.Empty(payloads);
        Assert.Equal("payload.zip", Assert.Single(skipped));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Skips_a_body_larger_than_the_limit_even_when_the_declared_size_is_small()
    {
        var oversized = new byte[AttachmentDownloader.MaxBytes + 1];
        PngSignature.CopyTo(oversized, 0);

        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Get, "shot.png", HttpStatusCode.OK, oversized);

        var (payloads, skipped) = await Downloader(fake).DownloadAsync([Attachment(size: 512)]);

        Assert.Empty(payloads);
        Assert.Equal("shot.png", Assert.Single(skipped));
    }

    [Fact]
    public async Task Skips_a_download_that_fails()
    {
        var fake = new FakeHttpMessageHandler(); // no routes: everything 404s

        var (payloads, skipped) = await Downloader(fake).DownloadAsync([Attachment()]);

        Assert.Empty(payloads);
        Assert.Equal("shot.png", Assert.Single(skipped));
    }

    [Fact]
    public async Task Keeps_the_good_file_from_a_mixed_batch()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Get, "good.png", HttpStatusCode.OK, PngBytes());
        fake.When(HttpMethod.Get, "evil.png", HttpStatusCode.OK,
            [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00]);

        IAttachment[] attachments =
        [
            Attachment(filename: "good.png", url: "https://cdn.discordapp.com/attachments/1/2/good.png"),
            Attachment(filename: "evil.png", url: "https://cdn.discordapp.com/attachments/1/2/evil.png"),
        ];

        var (payloads, skipped) = await Downloader(fake).DownloadAsync(attachments);

        Assert.Equal("good.png", Assert.Single(payloads).FileName);
        Assert.Equal("evil.png", Assert.Single(skipped));
    }
}
