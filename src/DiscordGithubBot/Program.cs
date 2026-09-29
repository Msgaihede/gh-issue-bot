using DiscordGithubBot;
using DiscordGithubBot.Ai;
using DiscordGithubBot.CodeContext;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Data;
using DiscordGithubBot.GitHub;
using DiscordGithubBot.OpenRouter;
using DiscordGithubBot.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// The one-shot modes must never fall through to starting the live bot: a mistyped invocation would
// otherwise connect to the gateway and answer real interactions next to the production instance.
if (args is ["--dry-run", ..] and not ["--dry-run", _, _])
{
    Console.Error.WriteLine("Usage: --dry-run owner/repo \"report text\" (quote the text)");
    return 1;
}
if (args is ["--smoke-upload", ..] and not ["--smoke-upload", _])
{
    Console.Error.WriteLine("Usage: --smoke-upload owner/repo");
    return 1;
}

var builder = Host.CreateApplicationBuilder(args);

// config layering: appsettings.json + appsettings.{Env}.json + env vars + command line come from
// CreateApplicationBuilder; Docker secrets (key-per-file) are added LAST so they win.
if (Directory.Exists("/run/secrets"))
    builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);

var options = builder.Configuration.Get<BotOptions>() ?? new BotOptions();

// The OpenAI section went away when every model call moved to OpenRouter; a deployment still carrying it
// would otherwise fail on the missing OpenRouter key without saying why the old key stopped counting.
if (builder.Configuration.GetSection("OpenAI").GetChildren().Any())
    Console.Error.WriteLine("CONFIG WARNING: the OpenAI section is no longer read; configure OpenRouter:ApiKey instead.");
var errors = options.Validate();
if (errors.Count > 0)
{
    foreach (var e in errors) Console.Error.WriteLine($"CONFIG ERROR: {e}");
    return 1;
}

// SQLite will not create a missing folder for the database file; a bare file name has none to create.
var dbDirectory = Path.GetDirectoryName(Path.GetFullPath(options.Database.Path));
if (!string.IsNullOrEmpty(dbDirectory)) Directory.CreateDirectory(dbDirectory);

builder.Services.AddBotServices(options);

// A dry run is for tuning: the decision client logs every raw answer at Debug, and the HTTP client's
// per-request lines and EF's SQL would bury them. appsettings.json already quiets both, but a dry run started
// without its content root (plain `dotnet run` from the repository root) never reads that file.
if (args is ["--dry-run", ..])
{
    builder.Logging.AddFilter("DiscordGithubBot", LogLevel.Debug);
    builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
}

using var host = builder.Build();
var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DiscordGithubBot.Startup");

// The bot ships no migrations: it owns its SQLite file, and a schema from another build is rebuilt.
using (var scope = host.Services.CreateScope())
{
    switch (DatabaseSchema.EnsureCurrent(scope.ServiceProvider.GetRequiredService<BotDbContext>()))
    {
        case SchemaChange.Upgraded:
            startupLogger.LogInformation("Database schema upgraded to version {Version}; all data kept.", DatabaseSchema.Version);
            break;
        case SchemaChange.Rebuilt:
            startupLogger.LogWarning(
                "Database schema was out of date and had no upgrade path; rebuilt it empty at version {Version}.",
                DatabaseSchema.Version);
            break;
    }
}

// one-shot smoke test for the unofficial upload endpoint: dotnet run -- --smoke-upload owner/repo
if (args is ["--smoke-upload", var repo])
{
    var app = options.AppByRepo(repo);
    if (app is null) { Console.Error.WriteLine($"No configured app for repo '{repo}'."); return 1; }
    // Which credentials the upload runs under is the whole question the smoke test answers: the
    // user-attachments endpoint is undocumented, so whether it accepts an App installation token is
    // something only a real run can say.
    Console.WriteLine($"Smoke upload to {app.Repo} — auth: " +
        (app.GitHubApp is null ? "PAT" : "GitHub App (installation token)"));

    var uploader = host.Services.GetRequiredService<IImageUploader>();
    var png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    var result = await uploader.UploadAsync(app, "smoke-test.png", "image/png", png);
    Console.WriteLine(result is null ? "SMOKE FAILED: both tiers failed" : $"SMOKE OK: {result.Url}");
    return result is null ? 1 : 0;
}

// one report through every model call, printed instead of saved or posted:
// dotnet run -- --dry-run owner/repo "the report text"
if (args is ["--dry-run", var dryRepo, var reportText])
{
    var app = options.AppByRepo(dryRepo);
    if (app is null) { Console.Error.WriteLine($"No configured app for repo '{dryRepo}'."); return 1; }
    Console.WriteLine($"Dry run for {app.Repo}: no report is stored and nothing is posted to Discord or GitHub.");

    // Code context needs a map; updating one is incremental, so after the first run this is two GitHub calls.
    // Its own scope, so its usage is reported apart from the report's.
    await using (var mapScope = host.Services.CreateAsyncScope())
    {
        var map = await mapScope.ServiceProvider.GetRequiredService<IRepoMapService>().UpdateAsync(app);
        Console.WriteLine(map.UpToDate && map.Embedded == 0
            ? "Repository map: up to date."
            : $"Repository map: {map.Summarized} file(s) summarized, {map.Embedded} embedded ({mapScope.ServiceProvider.GetRequiredService<AiUsageMeter>()}); " +
              (map.Complete ? "complete." : "incomplete after a failure (see the warning above), so code context may be thin."));
    }

    await using var scope = host.Services.CreateAsyncScope();
    var services = scope.ServiceProvider;

    try
    {
        var analysis = await services.GetRequiredService<IReportPipeline>().AnalyzeAsync(app, reportText);
        var codeContext = await services.GetRequiredService<ICodeContextBuilder>().BuildAsync(app, analysis.Draft);
        Console.WriteLine();
        Console.WriteLine(DryRun.Format(analysis, codeContext, services.GetRequiredService<AiUsageMeter>()));
        return 0;
    }
    catch (NormalizationException ex)
    {
        Console.Error.WriteLine($"DRY RUN FAILED: {ex.Message} {ex.InnerException?.Message}");
        return 1;
    }
}

StartupLog.Write(startupLogger, options);
await host.RunAsync();
return 0;
