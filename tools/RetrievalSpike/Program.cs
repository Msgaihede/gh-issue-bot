// Retrieval spike: how well do cheap retrievers find the files an issue is about, compared with each other and
// with today's "Jev reads the whole map" selection? Ground truth is the repository's own history: for every
// closed issue a merged pull request says it fixes ("Fixes #N"), the files that pull request changed.
//
// usage: RetrievalSpike owner/repo [--models m1,m2,...] [--limit N] [--jev N]
//
// Configuration comes from the environment exactly as for the bot (run it with the variables from .env loaded).
// Every model output is cached under %TEMP%/retrieval-spike/<repo>/, so a re-run only pays for what is new.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DiscordGithubBot;
using DiscordGithubBot.Ai;
using DiscordGithubBot.CodeContext;
using DiscordGithubBot.CodeContext.Retrieval;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Data;
using DiscordGithubBot.GitHub;
using DiscordGithubBot.OpenRouter;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Length == 0 || args[0].StartsWith('-'))
{
    Console.Error.WriteLine("usage: RetrievalSpike owner/repo [--models m1,m2] [--limit N] [--jev N]");
    return 1;
}

var repo = args[0];
string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
string[] models = Arg("--models")?.Split(',', StringSplitOptions.RemoveEmptyEntries)
    ?? ["voyageai/voyage-4-lite", "voyageai/voyage-4", "voyageai/voyage-code-4", "openai/text-embedding-3-small", "qwen/qwen3-embedding-8b"];
var limit = int.Parse(Arg("--limit") ?? "1000");
var jevCount = int.Parse(Arg("--jev") ?? "0");
const int Parallelism = 6;
int[] ks = [10, 20, 40];

var builder = Host.CreateApplicationBuilder([]);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Error);
var options = builder.Configuration.Get<BotOptions>() ?? new BotOptions();
var errors = options.Validate();
if (errors.Count > 0)
{
    foreach (var e in errors) Console.Error.WriteLine($"CONFIG ERROR: {e}");
    return 1;
}

builder.Services.AddBotServices(options);
using var host = builder.Build();
await using var scope = host.Services.CreateAsyncScope();
var sp = scope.ServiceProvider;
var app = options.AppByRepo(repo);
if (app is null) { Console.Error.WriteLine($"No configured app for {repo}."); return 1; }

var usage = sp.GetRequiredService<AiUsageMeter>();
var chat = sp.GetRequiredService<IOpenRouterChat>();
var embedder = sp.GetRequiredService<IEmbeddingModel>();
var expander = sp.GetRequiredService<IQueryExpander>();
var classifier = sp.GetRequiredService<IReportClassifier>();
var normalizer = sp.GetRequiredService<IReportNormalizer>();
var decisions = sp.GetRequiredService<IDecisionModel>();
var cacheDir = Path.Combine(Path.GetTempPath(), "retrieval-spike", repo.Replace('/', '_'));
Directory.CreateDirectory(cacheDir);
var clock = Stopwatch.StartNew();
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

// ---- the map ---------------------------------------------------------------------------------------------
var db = sp.GetRequiredService<BotDbContext>();
var repoKey = repo.ToLowerInvariant();
var map = await db.RepoFiles.AsNoTracking().Where(f => f.RepoKey == repoKey && f.Summary != "").ToListAsync();
var mapState = await db.RepoMapStates.AsNoTracking().FirstOrDefaultAsync(s => s.RepoKey == repoKey);
if (map.Count == 0) { Console.Error.WriteLine("No repository map — run a --dry-run first."); return 1; }
var codeFiles = map.Where(f => SourceFileFilter.KindOf(f.Path) == MapFileKind.Code).ToList();
var docFiles = map.Where(f => SourceFileFilter.KindOf(f.Path) == MapFileKind.Doc).ToList();
var mapped = map.ToDictionary(f => f.Path, StringComparer.Ordinal);
Console.WriteLine($"Map: {map.Count} files ({codeFiles.Count} code, {docFiles.Count} docs), complete={mapState?.IsComplete}");

// ---- ground truth ----------------------------------------------------------------------------------------
var gtPath = Path.Combine(cacheDir, "ground-truth.json");
List<Example> examples;
if (File.Exists(gtPath))
{
    examples = JsonSerializer.Deserialize<List<Example>>(await File.ReadAllTextAsync(gtPath), json)!;
}
else
{
    var auth = sp.GetRequiredService<IGitHubAuthProvider>();
    using var gh = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
    gh.DefaultRequestHeaders.UserAgent.ParseAdd("retrieval-spike");
    gh.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

    async Task<List<JsonObject>> Pages(string path)
    {
        var all = new List<JsonObject>();
        for (var page = 1; page <= 50; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{path}{(path.Contains('?') ? '&' : '?')}per_page=100&page={page}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.GetTokenAsync(app));
            using var response = await gh.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var items = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray().Select(n => n!.AsObject()).ToList();
            all.AddRange(items);
            if (items.Count < 100) break;
        }
        return all;
    }

    var issues = (await Pages($"repos/{app.Repo}/issues?state=closed"))
        .Where(i => i["pull_request"] is null)
        .ToDictionary(i => i["number"]!.GetValue<int>());
    var prs = (await Pages($"repos/{app.Repo}/pulls?state=closed")).Where(p => p["merged_at"] is not null).ToList();
    var linkRx = new Regex(@"(?i)\b(close[sd]?|fix(e[sd])?|resolve[sd]?)\s+#(\d+)");

    var links = prs
        .SelectMany(p => linkRx.Matches($"{p["title"]} {p["body"]}")
            .Select(m => (Issue: int.Parse(m.Groups[3].Value), Pr: p["number"]!.GetValue<int>())))
        .Where(l => issues.ContainsKey(l.Issue))
        .GroupBy(l => l.Issue)
        .ToList();

    examples = new List<Example>();
    foreach (var group in links)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pr in group.Select(l => l.Pr).Distinct())
            foreach (var f in await Pages($"repos/{app.Repo}/pulls/{pr}/files"))
                files.Add(f["filename"]!.GetValue<string>());

        var issue = issues[group.Key];
        examples.Add(new Example(
            group.Key, group.Select(l => l.Pr).Distinct().ToArray(),
            issue["title"]?.GetValue<string>() ?? "", issue["body"]?.GetValue<string>() ?? "",
            files.ToArray()));
    }

    await File.WriteAllTextAsync(gtPath, JsonSerializer.Serialize(examples, json));
}

// Ground truth is whatever the fix touched that the map (today's default branch) still has.
var cases = examples
    .Select(e => new Case(e,
        e.ChangedFiles.Where(f => mapped.ContainsKey(f) && SourceFileFilter.KindOf(f) == MapFileKind.Code).ToArray(),
        e.ChangedFiles.Where(f => mapped.ContainsKey(f) && SourceFileFilter.KindOf(f) == MapFileKind.Doc).ToArray()))
    .Where(c => c.Code.Length > 0)
    .OrderBy(c => c.Example.Issue)
    .Take(limit)
    .ToList();
Console.WriteLine($"Ground truth: {examples.Count} issues fixed by a linked PR; {cases.Count} with changed code still in the map " +
                  $"(median {Median(cases.Select(c => c.Code.Length))} code files per fix, {cases.Count(c => c.Docs.Length > 0)} also changed docs).");

// ---- queries: the issue as written (dev voice) and as a non-technical user would report it --------------
var queriesPath = Path.Combine(cacheDir, "queries.json");
var queries = File.Exists(queriesPath)
    ? JsonSerializer.Deserialize<ConcurrentDictionary<int, Queries>>(await File.ReadAllTextAsync(queriesPath), json)!
    : new ConcurrentDictionary<int, Queries>();

const string ParaphrasePrompt = """
    You play a non-technical user of an app who ran into the problem, or wants the change, described in a
    GitHub issue. Write the short message they would type into the app's Discord to report it: one to four
    casual sentences in everyday words, describing what they saw on screen or what they want. Never use code
    identifiers, file names, technical jargon, or the issue's own specific terminology — describe things by how
    they look and behave to a user. Do not mention GitHub or the issue.
    """;

await Parallel.ForEachAsync(cases.Where(c => !queries.ContainsKey(c.Example.Issue)),
    new ParallelOptions { MaxDegreeOfParallelism = Parallelism }, async (c, ct) =>
    {
        var e = c.Example;
        var paraphrase = await chat.CompleteAsync<Paraphrase>(new ChatPrompt("user_voice", ParaphrasePrompt,
            $"Issue title: {e.Title}\nIssue body:\n{Cut(e.Body, 3000)}", MaxTokens: 2000), ChatUrgency.Background, ct);
        var type = await classifier.ClassifyAsync(app.Name, paraphrase.Message, ct);
        var draft = await normalizer.NormalizeAsync(type, app.Name, paraphrase.Message, ct);
        queries[e.Issue] = new Queries(
            new Draft(e.Title, Cut(e.Body, 4000)), new Draft(draft.Titles[0], draft.Body), paraphrase.Message);
    });
await File.WriteAllTextAsync(queriesPath, JsonSerializer.Serialize(queries, json));

// ---- query expansion: generic, and grounded in the repository's file tree -------------------------------
var tree = QueryExpander.FileTree(map.Select(f => f.Path));
var expansionsPath = Path.Combine(cacheDir, "expansions.json");
var expansions = File.Exists(expansionsPath)
    ? JsonSerializer.Deserialize<ConcurrentDictionary<string, string[]>>(await File.ReadAllTextAsync(expansionsPath), json)!
    : new ConcurrentDictionary<string, string[]>();

var expansionJobs = cases.SelectMany(c => new[] { "dev", "user" }.SelectMany(voice => new[] { "generic", "tree" }
        .Select(kind => (c, voice, kind, key: $"{c.Example.Issue}:{voice}:{kind}"))))
    .Where(j => !expansions.ContainsKey(j.key))
    .ToList();
await Parallel.ForEachAsync(expansionJobs, new ParallelOptions { MaxDegreeOfParallelism = Parallelism }, async (j, ct) =>
{
    var draft = Voice(queries[j.c.Example.Issue], j.voice);
    var terms = await expander.ExpandAsync(new IssueDraft(draft.Title, draft.Body), j.kind == "tree" ? tree : null, ct);
    expansions[j.key] = terms.ToArray();
});
await File.WriteAllTextAsync(expansionsPath, JsonSerializer.Serialize(expansions, json));

string QueryText(Case c, string voice, string? expansion)
{
    var d = Voice(queries[c.Example.Issue], voice);
    var text = $"{d.Title}\n{d.Body}";
    return expansion is null ? text : text + "\nSearch terms: " + string.Join(", ", expansions[$"{c.Example.Issue}:{voice}:{expansion}"]);
}

// ---- A: keyword search -----------------------------------------------------------------------------------
var codeIndex = new KeywordIndex(codeFiles.Select(f => new IndexedText(f.Path, f.Path + "\n" + f.Summary)));
var docIndex = new KeywordIndex(docFiles.Select(f => new IndexedText(f.Path, f.Path + "\n" + f.Summary)));

var rankings = new Dictionary<string, Dictionary<(int Issue, string Voice), IReadOnlyList<SearchHit>>>();
void Rank(string name, Func<Case, string, IReadOnlyList<SearchHit>> rank)
{
    var byCase = new Dictionary<(int, string), IReadOnlyList<SearchHit>>();
    foreach (var c in cases)
        foreach (var voice in new[] { "dev", "user" })
            byCase[(c.Example.Issue, voice)] = rank(c, voice);
    rankings[name] = byCase;
}

Rank("A  keywords", (c, v) => codeIndex.Search(QueryText(c, v, null), 40));
Rank("A+ keywords + generic terms", (c, v) => codeIndex.Search(QueryText(c, v, "generic"), 40));
Rank("A* keywords + tree terms", (c, v) => codeIndex.Search(QueryText(c, v, "tree"), 40));

// ---- B: embeddings ---------------------------------------------------------------------------------------
var embeddingCost = new Dictionary<string, decimal>();
var docCasesForEmbedding = cases.Where(c => c.Docs.Length > 0).ToList();
var docRankings = new Dictionary<string, Dictionary<int, IReadOnlyList<SearchHit>>>();
foreach (var expansion in new string?[] { null, "tree" })
    docRankings[expansion is null ? "A  keywords" : "A* keywords + tree terms"] =
        docCasesForEmbedding.ToDictionary(c => c.Example.Issue, c => docIndex.Search(QueryText(c, "user", expansion), 10));
foreach (var model in models)
{
    var before = usage.TotalCost;
    try
    {
        var fileVectors = await Embed(model, codeFiles.Select(f => f.Path + "\n" + f.Summary).ToList());
        var index = new VectorIndex(codeFiles.Select((f, i) => (f.Path, fileVectors[i])));

        var queryTexts = cases.SelectMany(c => new[] { "dev", "user" }.SelectMany(v => new[]
            { QueryText(c, v, null), QueryText(c, v, "tree") })).Distinct().ToList();
        var queryVectors = (await Embed(model, queryTexts)).Select((v, i) => (queryTexts[i], v)).ToDictionary(x => x.Item1, x => x.v);

        var shortName = model[(model.IndexOf('/') + 1)..];
        Rank($"B  {shortName}", (c, v) => index.Search(queryVectors[QueryText(c, v, null)], 40));
        Rank($"B* {shortName} + tree terms", (c, v) => index.Search(queryVectors[QueryText(c, v, "tree")], 40));

        var docVectors = await Embed(model, docFiles.Select(f => f.Path + "\n" + f.Summary).ToList());
        var docVectorIndex = new VectorIndex(docFiles.Select((f, i) => (f.Path, docVectors[i])));
        docRankings[$"B  {shortName}"] = docCasesForEmbedding.ToDictionary(
            c => c.Example.Issue, c => docVectorIndex.Search(queryVectors[QueryText(c, "user", null)], 10));
        docRankings[$"B* {shortName} + tree terms"] = docCasesForEmbedding.ToDictionary(
            c => c.Example.Issue, c => docVectorIndex.Search(queryVectors[QueryText(c, "user", "tree")], 10));
        docRankings[$"H  A* + {shortName} + tree terms"] = docCasesForEmbedding.ToDictionary(
            c => c.Example.Issue, c => RankFusion.Fuse(10, docRankings["A* keywords + tree terms"][c.Example.Issue],
                docRankings[$"B* {shortName} + tree terms"][c.Example.Issue]));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  {model}: failed ({ex.Message}); skipped.");
    }
    embeddingCost[model] = usage.TotalCost - before;
}

// ---- hybrids: keyword + each embedding model, fused by rank ----------------------------------------------
foreach (var b in rankings.Keys.Where(k => k.StartsWith("B* ", StringComparison.Ordinal)).ToList())
{
    var a = rankings["A* keywords + tree terms"];
    var bRanks = rankings[b];
    Rank($"H  A* + {b[3..]}", (c, v) => RankFusion.Fuse(40, a[(c.Example.Issue, v)], bRanks[(c.Example.Issue, v)]));
}

// ---- scores ----------------------------------------------------------------------------------------------
foreach (var voice in new[] { "user", "dev" })
{
    Console.WriteLine();
    Console.WriteLine(voice == "user"
        ? $"CODE FILES — user voice (report rewritten as a non-technical user, then drafted by the bot), n={cases.Count}"
        : $"CODE FILES — dev voice (the issue as written), n={cases.Count}");
    Console.WriteLine($"{"retriever",-58} {"hit@10",7} {"hit@20",7} {"hit@40",7} {"rec@10",7} {"rec@20",7} {"rec@40",7}");
    foreach (var (name, byCase) in rankings.OrderByDescending(r => HitRate(r.Value, voice, 20)))
    {
        var cells = ks.Select(k => HitRate(byCase, voice, k)).Concat(ks.Select(k => Recall(byCase, voice, k)));
        Console.WriteLine($"{name,-58} " + string.Join(" ", cells.Select(x => $"{x,7:P0}")));
    }
}

if (docCasesForEmbedding.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"DOCS — user voice, n={docCasesForEmbedding.Count}");
    Console.WriteLine($"{"retriever",-58} {"hit@2",7} {"hit@5",7} {"hit@10",7}");
    double DocHit(Dictionary<int, IReadOnlyList<SearchHit>> r, int k) =>
        docCasesForEmbedding.Count(c => r[c.Example.Issue].Take(k).Any(h => c.Docs.Contains(h.Key))) / (double)docCasesForEmbedding.Count;
    foreach (var (name, r) in docRankings.OrderByDescending(r => DocHit(r.Value, 5)))
        Console.WriteLine($"{name,-58} {DocHit(r, 2),7:P0} {DocHit(r, 5),7:P0} {DocHit(r, 10),7:P0}");
}

// ---- end to end: Jev picking 4 files from a 40-file shortlist vs from the whole map (today) --------------
if (jevCount > 0)
{
    var best = rankings.OrderByDescending(r => HitRate(r.Value, "user", 40)).First();
    var sample = cases.Take(jevCount).ToList();
    Console.WriteLine();
    Console.WriteLine($"JEV PICKS 4 CODE FILES — user voice, n={sample.Count}; shortlist from \"{best.Key}\"");

    var (shortHits, shortTokens) = (0, 0L);
    var (fullHits, fullTokens) = (0, 0L);
    foreach (var c in sample)
    {
        var d = Voice(queries[c.Example.Issue], "user");
        var issueState = new JsonObject { ["issue"] = new JsonObject { ["title"] = d.Title, ["body"] = Cut(d.Body, 3000) } };

        var t0 = usage.DecisionInputTokens;
        var candidates = best.Value[(c.Example.Issue, "user")].Select(h => mapped[h.Key]).ToList();
        var picked = await Pick(issueState, candidates);
        shortTokens += usage.DecisionInputTokens - t0;
        if (picked.Any(c.Code.Contains)) shortHits++;

        var t1 = usage.DecisionInputTokens;
        var pickedFull = await Pick(issueState, codeFiles);
        fullTokens += usage.DecisionInputTokens - t1;
        if (pickedFull.Any(c.Code.Contains)) fullHits++;
    }

    Console.WriteLine($"  shortlist of 40 + Jev: hit@4 {shortHits / (double)sample.Count:P0}, {shortTokens / sample.Count:N0} Jev tokens per issue");
    Console.WriteLine($"  whole map + Jev (today): hit@4 {fullHits / (double)sample.Count:P0}, {fullTokens / sample.Count:N0} Jev tokens per issue");
}

Console.WriteLine();
Console.WriteLine("Embedding cost of the map + queries per model: " +
                  string.Join(", ", embeddingCost.Select(kv => $"{kv.Key} ${kv.Value:0.#####}")));
Console.WriteLine($"This run: AI usage {usage}; {clock.Elapsed.TotalMinutes:0.0} min.");
return 0;

// ---- helpers ---------------------------------------------------------------------------------------------
async Task<List<float[]>> Embed(string model, List<string> texts)
{
    var path = Path.Combine(cacheDir, $"vectors-{model.Replace('/', '_')}.json");
    var cache = File.Exists(path)
        ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(path))!
        : new Dictionary<string, string>();

    var missing = texts.Where(t => !cache.ContainsKey(Hash(t))).Distinct().ToList();
    if (missing.Count > 0)
    {
        var vectors = await embedder.EmbedAsync(model, missing);
        for (var i = 0; i < missing.Count; i++)
        {
            var bytes = new byte[vectors[i].Length * sizeof(float)];
            Buffer.BlockCopy(vectors[i], 0, bytes, 0, bytes.Length);
            cache[Hash(missing[i])] = Convert.ToBase64String(bytes);
        }
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(cache));
    }

    return texts.Select(t =>
    {
        var bytes = Convert.FromBase64String(cache[Hash(t)]);
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }).ToList();
}

async Task<List<string>> Pick(JsonObject issueState, IReadOnlyList<RepoFile> candidates)
{
    var byKey = candidates.ToDictionary(f => $"file_{f.Id}");
    var items = candidates.Select(f => new ShortlistItem($"file_{f.Id}",
        $"`{f.Path}` contains code that is likely involved in the problem or request in `issue`.",
        new JsonObject { ["path"] = f.Path, ["summary"] = f.Summary })).ToList();
    var spec = new ShortlistSpec("spike_code_files", "files",
        "Which source file in `files`, if any, most likely contains the code involved in the problem or request " +
        "described in `issue`? Judge by what each file is responsible for, as its summary says.",
        "None of the listed files is likely to contain code involved in `issue`.", 150, 0.05, 4);
    var picks = await Shortlist.SelectAsync(decisions, issueState, items, spec);
    return picks.Select(p => byKey[p.Key].Path).ToList();
}

double HitRate(Dictionary<(int, string), IReadOnlyList<SearchHit>> byCase, string voice, int k) =>
    cases.Count(c => byCase[(c.Example.Issue, voice)].Take(k).Any(h => c.Code.Contains(h.Key))) / (double)cases.Count;

double Recall(Dictionary<(int, string), IReadOnlyList<SearchHit>> byCase, string voice, int k) =>
    cases.Average(c => byCase[(c.Example.Issue, voice)].Take(k).Count(h => c.Code.Contains(h.Key)) / (double)c.Code.Length);

static Draft Voice(Queries q, string voice) => voice == "user" ? q.User : q.Dev;
static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..24];
static int Median(IEnumerable<int> values) { var v = values.Order().ToList(); return v.Count == 0 ? 0 : v[v.Count / 2]; }

sealed record Example(int Issue, int[] Prs, string Title, string Body, string[] ChangedFiles);
sealed record Case(Example Example, string[] Code, string[] Docs);
sealed record Draft(string Title, string Body);
sealed record Queries(Draft Dev, Draft User, string UserMessage);
sealed record Paraphrase(string Message);
