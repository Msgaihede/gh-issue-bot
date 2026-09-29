using DiscordGithubBot.CodeContext;

namespace DiscordGithubBot.Tests.CodeContext;

public class SourceFileFilterTests
{
    [Theory]
    [InlineData("src/Checkout/PaymentService.cs")]
    [InlineData("app/components/Cart.tsx")]
    [InlineData("server/handlers.go")]
    [InlineData("packages/web/src/index.ts")] // monorepo source lives under packages/
    public void Hand_written_source_is_mapped_as_code(string path) =>
        Assert.Equal(MapFileKind.Code, SourceFileFilter.Classify(path, 1000));

    /// <summary>The owner's ask: repositories explain behaviour and limitations in Markdown.</summary>
    [Theory]
    [InlineData("README.md")]
    [InlineData("docs/configuration.md")]
    [InlineData("docs/guide/sync.mdx")]
    [InlineData("CHANGELOG.md")]
    public void Project_documentation_is_mapped_as_docs(string path) =>
        Assert.Equal(MapFileKind.Doc, SourceFileFilter.Classify(path, 1000));

    [Theory]
    [InlineData("LICENSE.md")]
    [InlineData("CODE_OF_CONDUCT.md")]
    [InlineData(".github/ISSUE_TEMPLATE/bug.md")]
    [InlineData("node_modules/react/README.md")]
    public void Boilerplate_and_third_party_docs_are_left_out(string path) =>
        Assert.Null(SourceFileFilter.Classify(path, 1000));

    [Theory]
    [InlineData("node_modules/react/index.js")]
    [InlineData("src/bin/Debug/app.cs")]
    [InlineData("dist/app.js")]
    [InlineData("web/app.min.js")]
    [InlineData("src/Form1.Designer.cs")]
    [InlineData("api/service_pb2.py")]
    [InlineData("types/index.d.ts")]
    [InlineData("assets/logo.png")]
    [InlineData("package-lock.json")]
    public void Dependencies_build_output_generated_code_and_assets_are_left_out(string path) =>
        Assert.Null(SourceFileFilter.Classify(path, 1000));

    [Theory]
    [InlineData(0)]
    [InlineData(SourceFileFilter.MaxBytes + 1)]
    public void Empty_and_oversized_files_are_left_out(long size) =>
        Assert.Null(SourceFileFilter.Classify("src/a.cs", size));

    [Fact]
    public void The_kind_of_a_mapped_file_follows_from_its_path()
    {
        Assert.Equal(MapFileKind.Doc, SourceFileFilter.KindOf("docs/a.md"));
        Assert.Equal(MapFileKind.Code, SourceFileFilter.KindOf("src/a.py"));
    }
}
