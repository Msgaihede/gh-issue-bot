using Discord;

namespace DiscordGithubBot.Tests.TestDoubles;

/// <summary>
/// A modal attachment as the downloader sees it: only the name, the CDN link, and the two pieces of
/// declared metadata it filters on. Everything else on <see cref="IAttachment"/> is inert padding — the
/// interface is far wider than anything the bot reads.
/// </summary>
public sealed class FakeAttachment : IAttachment
{
    public required string Filename { get; init; }
    public required string Url { get; init; }

    /// <summary>Discord's declared size, which the client supplies — deliberately allowed to lie in tests.</summary>
    public int Size { get; init; }

    /// <summary>Discord's declared type, inferred from the file name — likewise allowed to lie.</summary>
    public string ContentType { get; init; } = "image/png";

    public ulong Id => 1UL;
    public DateTimeOffset CreatedAt => DateTimeOffset.UnixEpoch;
    public string ProxyUrl => Url;
    public int? Height => null;
    public int? Width => null;
    public bool Ephemeral => false;
    public string Description => string.Empty;
    public double? Duration => null;
    public string Waveform => string.Empty;
    public byte[] WaveformBytes => [];
    public AttachmentFlags Flags => AttachmentFlags.None;
    public string Title => string.Empty;
    public DateTimeOffset? ClipCreatedAt => null;
    public IReadOnlyCollection<IUser> ClipParticipants => [];
}
