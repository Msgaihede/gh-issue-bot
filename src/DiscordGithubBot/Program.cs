using DiscordGithubBot;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Data;
using DiscordGithubBot.GitHub;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// config layering: appsettings.json + appsettings.{Env}.json + env vars + command line come from
// CreateApplicationBuilder; Docker secrets (key-per-file) are added LAST so they win.
if (Directory.Exists("/run/secrets"))
    builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);

var options = builder.Configuration.Get<BotOptions>() ?? new BotOptions();
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

using var host = builder.Build();

// The bot ships no migrations: it owns its SQLite file and materializes the schema on every start.
using (var scope = host.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<BotDbContext>().Database.EnsureCreated();

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

await host.RunAsync();
return 0;
