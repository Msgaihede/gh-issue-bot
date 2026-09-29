using System.Security.Cryptography;

namespace DiscordGithubBot.Configuration;

public sealed class DiscordOptions
{
    public string Token { get; set; } = "";
}

/// <summary>
/// Every model call the bot makes goes through OpenRouter: chat completions for text it has to write, the
/// Decisions API for judgments it has to make. Both models are pinned here, never aliased — a decision
/// threshold belongs to the build it was set on.
/// </summary>
public sealed class OpenRouterOptions
{
    /// <summary>Tried first: OpenAI's half-price flex tier, then OpenAI's regular endpoint.</summary>
    public static readonly IReadOnlyList<string> DefaultChatProviders = ["openai/flex", "openai"];

    /// <summary>
    /// The regular tier: where a call that must be fast goes (<see cref="DiscordGithubBot.OpenRouter.ChatTier.Regular"/>), and where
    /// a flex call is retried after missing its deadline or failing transiently.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultRegularProviders = ["openai"];

    public string ApiKey { get; set; } = "";

    /// <summary>Model that writes drafts, file summaries and code notes.</summary>
    public string ChatModel { get; set; } = "openai/gpt-6-luna";

    /// <summary>
    /// OpenRouter <c>provider.order</c> for the first attempt of a flex-tier chat call (every call except the
    /// ones marked <see cref="DiscordGithubBot.OpenRouter.ChatTier.Regular"/>). Nullable rather than initialised because the
    /// configuration binder appends to an existing list instead of replacing it — a configured list would
    /// otherwise land after the defaults. See <see cref="EffectiveChatProviders"/>.
    /// </summary>
    public List<string>? ChatProviders { get; set; }

    /// <summary>
    /// <c>provider.order</c> for regular-tier calls and for every retry; nullable for the same reason as
    /// <see cref="ChatProviders"/>.
    /// </summary>
    public List<string>? RegularProviders { get; set; }

    /// <summary>
    /// How long an interactive chat call (a reporter is waiting) may spend on the first attempt before it is
    /// retried on <see cref="EffectiveRegularProviders"/>. Flex can queue; this caps what that costs.
    /// </summary>
    public int ChatDeadlineSeconds { get; set; } = 30;

    /// <summary>The <c>reasoning.effort</c> values OpenRouter accepts.</summary>
    public static readonly IReadOnlyList<string> ReasoningEfforts = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    /// <summary>
    /// OpenRouter <c>reasoning.effort</c> for every chat call (env var <c>OpenRouter__ReasoningEffort</c>); one of
    /// <see cref="ReasoningEfforts"/>, or empty to leave the model's own default.
    /// </summary>
    public string ReasoningEffort { get; set; } = "medium";

    /// <summary>Decision (System One) model for every judgment: type, title, labels, dedup, file selection.</summary>
    public string DecisionModel { get; set; } = "typesafe/jev-1.13";

    /// <summary>
    /// Embeds the repository map's summaries, and each issue, for finding an issue's files by meaning. Every
    /// stored vector is stamped with this id; changing it re-embeds the map at the next check.
    /// </summary>
    public string EmbeddingModel { get; set; } = "voyageai/voyage-4";

    public IReadOnlyList<string> EffectiveChatProviders => ChatProviders ?? DefaultChatProviders;

    public IReadOnlyList<string> EffectiveRegularProviders => RegularProviders ?? DefaultRegularProviders;
}

public sealed class LimitsOptions
{
    /// <summary>Reports one Discord user may submit per rolling 24 hours; 0 turns the cap off.</summary>
    public int ReportsPerUserPerDay { get; set; } = 10;
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

    /// <summary>
    /// Repository labels never attached automatically. Null means the defaults (triage outcomes such as
    /// "duplicate" and "wontfix"); an explicit list replaces them. Nullable because the configuration
    /// binder appends to an initialised list rather than replacing it.
    /// </summary>
    public List<string>? IgnoredLabels { get; set; }
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
    public OpenRouterOptions OpenRouter { get; set; } = new();
    public DatabaseOptions Database { get; set; } = new();
    public LimitsOptions Limits { get; set; } = new();
    public List<AppConfig> Apps { get; set; } = new();

    /// <summary>
    /// Returns an empty list when the configuration is valid, otherwise one
    /// human-readable message per problem, each naming the offending config key.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Discord.Token)) errors.Add("Discord:Token is required.");
        errors.AddRange(ValidateOpenRouter(OpenRouter));
        if (string.IsNullOrWhiteSpace(Database.Path)) errors.Add("Database:Path is required.");
        if (Limits.ReportsPerUserPerDay < 0) errors.Add("Limits:ReportsPerUserPerDay must be 0 (off) or positive.");
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

    private static IEnumerable<string> ValidateOpenRouter(OpenRouterOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.ApiKey)) yield return "OpenRouter:ApiKey is required.";
        if (string.IsNullOrWhiteSpace(o.ChatModel)) yield return "OpenRouter:ChatModel is required.";
        if (string.IsNullOrWhiteSpace(o.DecisionModel)) yield return "OpenRouter:DecisionModel is required.";
        // A "~vendor/model-latest" alias moves to a new build without notice, and every decision threshold
        // in this bot was set against the probabilities of one particular build.
        else if (o.DecisionModel.TrimStart().StartsWith('~'))
            yield return $"OpenRouter:DecisionModel: '{o.DecisionModel}' is an alias; pin a model id such as typesafe/jev-1.13.";
        if (string.IsNullOrWhiteSpace(o.EmbeddingModel)) yield return "OpenRouter:EmbeddingModel is required.";
        // Checked here rather than left to the first report: OpenRouter rejects an unknown effort with a 400,
        // which would fail every draft until someone read the logs.
        if (!string.IsNullOrWhiteSpace(o.ReasoningEffort) && !OpenRouterOptions.ReasoningEfforts.Contains(o.ReasoningEffort.Trim()))
            yield return $"OpenRouter:ReasoningEffort: '{o.ReasoningEffort}' must be one of " +
                         $"{string.Join(", ", OpenRouterOptions.ReasoningEfforts)} (or empty for the model's default).";
        if (o.ChatDeadlineSeconds <= 0) yield return "OpenRouter:ChatDeadlineSeconds must be positive.";
    }

    /// <summary>
    /// Exactly one authentication method per app. Both configured is ambiguous — the bot would have to
    /// pick one silently, and the operator would never learn which — and neither leaves every GitHub call
    /// unauthenticated. A partially filled <c>GitHubApp</c> block is reported field by field, because the
    /// half-configured case is the likely one: an App id copied but the installation id still missing.
    /// The key is parsed here as well, not merely counted: a PEM mangled on its way through a shell
    /// (the classic being literal <c>\n</c> two-character sequences instead of newlines) satisfies every
    /// "is it set" check and then fails at the first mint, an hour into a run and inside a report the
    /// user is waiting on.
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
        else if (!KeyParses(auth))
            // Names whichever key form was actually configured; the phrasing never carries the key
            // material or the exception text, because startup errors are logged.
            yield return $"{prefix}.GitHubApp.{(hasPath ? "PrivateKeyPath" : "PrivateKey")}: not a valid PEM private key.";
    }

    /// <summary>
    /// Whether the configured private key — inline PEM, or the contents of <c>PrivateKeyPath</c> — is one
    /// <see cref="RSA"/> can actually import. Called only once the "exactly one form, and the file exists"
    /// checks above have passed, so a failure here means the bytes themselves are wrong. Every failure is
    /// the same answer to the caller: nothing about the key, or about why it failed, leaves this method.
    /// </summary>
    private static bool KeyParses(GitHubAppAuth auth)
    {
        try
        {
            var pem = string.IsNullOrWhiteSpace(auth.PrivateKeyPath)
                ? auth.PrivateKey
                : File.ReadAllText(auth.PrivateKeyPath);

            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException or NotSupportedException
                                       or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The apps configured for a server: those listing it in <see cref="AppConfig.GuildIds"/>. Empty for a
    /// DM (no server) or a server nobody configured — what a user may use there depends on which configured
    /// servers they are in, which <c>AppAccess</c> works out.
    /// </summary>
    public IReadOnlyList<AppConfig> AppsForGuild(ulong? guildId) =>
        guildId is { } id ? Apps.Where(a => a.GuildIds.Contains(id)).ToList() : [];

    /// <summary>The app owning the given "owner/repo", or null when none matches.</summary>
    public AppConfig? AppByRepo(string repo) =>
        Apps.FirstOrDefault(a => string.Equals(a.Repo, repo, StringComparison.OrdinalIgnoreCase));
}
