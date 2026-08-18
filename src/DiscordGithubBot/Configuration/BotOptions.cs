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

    /// <summary>GitHub repository in "owner/repo" form.</summary>
    public string Repo { get; set; } = "";

    public string GitHubToken { get; set; } = "";
    public List<ulong> GuildIds { get; set; } = new();
    public List<ulong> ChannelIds { get; set; } = new();
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

            if (string.IsNullOrWhiteSpace(app.GitHubToken)) errors.Add($"{prefix}.GitHubToken is required.");
            if (app.GuildIds.Count == 0) errors.Add($"{prefix}.GuildIds: at least one guild id is required.");
            if (app.ChannelIds.Count == 0) errors.Add($"{prefix}.ChannelIds: at least one channel id is required.");
        }

        var dupes = Apps.GroupBy(a => a.Repo, StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1);
        errors.AddRange(dupes.Select(g => $"Apps: duplicate Repo '{g.Key}'."));

        return errors;
    }

    /// <summary>Apps configured for the given Discord guild.</summary>
    public IReadOnlyList<AppConfig> AppsForGuild(ulong guildId) =>
        Apps.Where(a => a.GuildIds.Contains(guildId)).ToList();

    /// <summary>The app owning the given "owner/repo", or null when none matches.</summary>
    public AppConfig? AppByRepo(string repo) =>
        Apps.FirstOrDefault(a => string.Equals(a.Repo, repo, StringComparison.OrdinalIgnoreCase));
}
