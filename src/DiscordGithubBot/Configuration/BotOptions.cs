namespace DiscordGithubBot.Configuration;

public sealed class DiscordOptions
{
    public string Token { get; set; } = "";
}

public sealed class OpenAIOptions
{
    public string ApiKey { get; set; } = "";
    public string ChatModel { get; set; } = "gpt-5.6-luna";
    public string EmbeddingModel { get; set; } = "text-embedding-3-small";
}

public sealed class DatabaseOptions
{
    public string Path { get; set; } = "db/app.db";
}

public sealed class AppConfig
{
    public string Name { get; set; } = "";

    private string _repo = "";

    /// <summary>
    /// GitHub repository in "owner/repo" form. Trimmed on assignment, so the trailing newline a Docker
    /// secret file carries — or a stray leading space in an env var — cannot turn every GitHub call into
    /// a 404 that validation happily passed.
    /// </summary>
    public string Repo
    {
        get => _repo;
        set => _repo = value?.Trim() ?? "";
    }

    /// <summary>Personal access token. Mutually exclusive with <see cref="GitHubApp"/>.</summary>
    public string GitHubToken { get; set; } = "";

    /// <summary>
    /// GitHub App credentials, as an alternative to <see cref="GitHubToken"/>. Null when the app
    /// authenticates with a PAT; exactly one of the two is configured (enforced by <see cref="BotOptions.Validate"/>).
    /// </summary>
    public GitHubAppAuth? GitHubApp { get; set; }

    public List<ulong> GuildIds { get; set; } = new();
    public List<ulong> ChannelIds { get; set; } = new();
}

/// <summary>
/// Credentials for authenticating as a GitHub App installation: the App's numeric id, the id of the
/// installation on the target repository, and the App's RSA private key — supplied either as PEM text
/// (<see cref="PrivateKey"/>, the natural shape for a key-per-file Docker secret) or as a path to a PEM
/// file (<see cref="PrivateKeyPath"/>). Exactly one of the two is configured.
/// </summary>
public sealed class GitHubAppAuth
{
    private string _privateKeyPath = "";

    public long AppId { get; set; }

    public long InstallationId { get; set; }

    /// <summary>The PEM text itself, PKCS#1 ("BEGIN RSA PRIVATE KEY") or PKCS#8, as GitHub hands it out.</summary>
    public string PrivateKey { get; set; } = "";

    /// <summary>
    /// Path to a PEM file. Trimmed on assignment for the same reason <see cref="AppConfig.Repo"/> is:
    /// a path carrying the trailing newline of a secret file would fail an existence check that the
    /// configured value passes when you read it.
    /// </summary>
    public string PrivateKeyPath
    {
        get => _privateKeyPath;
        set => _privateKeyPath = value?.Trim() ?? "";
    }
}

public sealed class BotOptions
{
    public DiscordOptions Discord { get; set; } = new();
    public OpenAIOptions OpenAI { get; set; } = new();
    public DatabaseOptions Database { get; set; } = new();
    public List<AppConfig> Apps { get; set; } = new();

    /// <summary>
    /// Returns an empty list when the configuration is valid, otherwise one
    /// human-readable message per problem, each naming the offending config key.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Discord.Token)) errors.Add("Discord:Token is required.");
        if (string.IsNullOrWhiteSpace(OpenAI.ApiKey)) errors.Add("OpenAI:ApiKey is required.");
        if (string.IsNullOrWhiteSpace(OpenAI.ChatModel)) errors.Add("OpenAI:ChatModel is required.");
        if (string.IsNullOrWhiteSpace(OpenAI.EmbeddingModel)) errors.Add("OpenAI:EmbeddingModel is required.");
        if (string.IsNullOrWhiteSpace(Database.Path)) errors.Add("Database:Path is required.");
        if (Apps.Count == 0) errors.Add("Apps: at least one app must be configured.");

        for (var i = 0; i < Apps.Count; i++)
        {
            var app = Apps[i];
            var prefix = $"Apps[{i}]";

            if (string.IsNullOrWhiteSpace(app.Name)) errors.Add($"{prefix}.Name is required.");

            var parts = app.Repo.Split('/');
            if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
                errors.Add($"{prefix}.Repo: '{app.Repo}' must be 'owner/repo'.");

            errors.AddRange(ValidateAuth(app, prefix));
            if (app.GuildIds.Count == 0) errors.Add($"{prefix}.GuildIds: at least one guild id is required.");
            if (app.ChannelIds.Count == 0) errors.Add($"{prefix}.ChannelIds: at least one channel id is required.");
        }

        var dupes = Apps.GroupBy(a => a.Repo, StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1);
        errors.AddRange(dupes.Select(g => $"Apps: duplicate Repo '{g.Key}'."));

        return errors;
    }

    /// <summary>
    /// Exactly one authentication method per app. Both configured is ambiguous — the bot would have to
    /// pick one silently, and the operator would never learn which — and neither leaves every GitHub call
    /// unauthenticated. A partially filled <c>GitHubApp</c> block is reported field by field, because the
    /// half-configured case is the likely one: an App id copied but the installation id still missing.
    /// </summary>
    private static IEnumerable<string> ValidateAuth(AppConfig app, string prefix)
    {
        var hasToken = !string.IsNullOrWhiteSpace(app.GitHubToken);
        var auth = app.GitHubApp;

        if (hasToken && auth is not null)
            yield return $"{prefix}: set either GitHubToken or GitHubApp, not both.";
        else if (!hasToken && auth is null)
            yield return $"{prefix}: one of GitHubToken or GitHubApp is required.";

        if (auth is null) yield break;

        if (auth.AppId <= 0) yield return $"{prefix}.GitHubApp.AppId is required and must be positive.";
        if (auth.InstallationId <= 0)
            yield return $"{prefix}.GitHubApp.InstallationId is required and must be positive.";

        var hasKey = !string.IsNullOrWhiteSpace(auth.PrivateKey);
        var hasPath = !string.IsNullOrWhiteSpace(auth.PrivateKeyPath);

        if (hasKey && hasPath)
            yield return $"{prefix}.GitHubApp: set either PrivateKey or PrivateKeyPath, not both.";
        else if (!hasKey && !hasPath)
            yield return $"{prefix}.GitHubApp: one of PrivateKey or PrivateKeyPath is required.";
        // Checked here rather than on first use: a typo'd path should fail the same startup that a
        // missing app id does, not the first report an hour into the run.
        else if (hasPath && !File.Exists(auth.PrivateKeyPath))
            yield return $"{prefix}.GitHubApp.PrivateKeyPath: '{auth.PrivateKeyPath}' does not exist.";
    }

    /// <summary>Apps configured for the given Discord guild.</summary>
    public IReadOnlyList<AppConfig> AppsForGuild(ulong guildId) =>
        Apps.Where(a => a.GuildIds.Contains(guildId)).ToList();

    /// <summary>The app owning the given "owner/repo", or null when none matches.</summary>
    public AppConfig? AppByRepo(string repo) =>
        Apps.FirstOrDefault(a => string.Equals(a.Repo, repo, StringComparison.OrdinalIgnoreCase));
}
