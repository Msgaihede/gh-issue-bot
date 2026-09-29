namespace DiscordGithubBot.CodeContext;

/// <summary>What a mapped file is to a report: code that may need changing, or documentation that explains.</summary>
public enum MapFileKind { Code, Doc }

/// <summary>
/// Decides which files of a repository belong in its map: hand-written source code and the project's own
/// documentation — not dependencies, build output, generated code, assets or lock files. Every file admitted
/// costs a summary once and a place in every file-selection request after that, so the filter leans towards
/// leaving things out. Documentation is in because repositories often explain intended behaviour, setup and
/// known limitations in Markdown, which is exactly the context a maintainer wants next to a report.
/// </summary>
public static class SourceFileFilter
{
    /// <summary>Anything larger is almost always generated or data, and would be cut to its head anyway.</summary>
    public const long MaxBytes = 200 * 1024;

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb", ".razor", ".cshtml", ".xaml",
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".vue", ".svelte", ".astro",
        ".py", ".rb", ".php", ".go", ".rs", ".java", ".kt", ".kts", ".scala", ".groovy",
        ".swift", ".m", ".mm", ".c", ".h", ".cc", ".cpp", ".cxx", ".hpp", ".hh",
        ".dart", ".lua", ".ex", ".exs", ".erl", ".clj", ".hs", ".jl", ".r", ".zig", ".nim",
        ".gd", ".sql", ".sh", ".ps1", ".html", ".css", ".scss", ".sass", ".less",
    };

    private static readonly HashSet<string> DocExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".mdx", ".markdown", ".rst", ".adoc",
    };

    /// <summary>Documents every repository carries whose content never bears on a report.</summary>
    private static readonly string[] BoilerplateDocs = ["LICENSE", "LICENCE", "COPYING", "CODE_OF_CONDUCT"];

    /// <summary>
    /// Directory names that hold dependencies, build output, tooling or VCS data rather than source. Matched
    /// against each path segment. <c>packages</c> is deliberately absent: in a monorepo it is the source.
    /// <c>.github</c> holds workflows and issue templates, which describe the process, not the product.
    /// </summary>
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bower_components", "vendor", "third_party", "thirdparty", "external", "deps",
        "dist", "build", "out", "bin", "obj", "target", "coverage",
        ".git", ".github", ".idea", ".vscode", ".next", ".nuxt", ".svelte-kit", ".turbo", ".cache",
        "__pycache__", ".venv", "venv", ".tox", "Pods", "DerivedData", "generated", "__generated__",
    };

    /// <summary>File name endings of generated or minified code, which says nothing a maintainer wrote.</summary>
    private static readonly string[] GeneratedSuffixes =
    [
        ".min.js", ".min.css", ".bundle.js", ".g.cs", ".g.i.cs", ".designer.cs", ".generated.cs",
        ".pb.go", "_pb2.py", "_pb2_grpc.py", ".pb.cc", ".pb.h", ".d.ts", ".map",
    ];

    /// <returns>The file's kind when it belongs in the map, otherwise null.</returns>
    public static MapFileKind? Classify(string path, long size)
    {
        if (size <= 0 || size > MaxBytes) return null;

        var segments = path.Split('/');
        if (segments[..^1].Any(ExcludedDirectories.Contains)) return null;

        var name = segments[^1];
        var extension = Path.GetExtension(name);

        if (DocExtensions.Contains(extension))
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            return BoilerplateDocs.Any(b => stem.Equals(b, StringComparison.OrdinalIgnoreCase)) ? null : MapFileKind.Doc;
        }

        if (GeneratedSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase))) return null;

        return CodeExtensions.Contains(extension) ? MapFileKind.Code : null;
    }

    /// <summary>The kind of a file already in the map; derived from its path so the map stores nothing extra.</summary>
    public static MapFileKind KindOf(string path) =>
        DocExtensions.Contains(Path.GetExtension(path)) ? MapFileKind.Doc : MapFileKind.Code;
}
