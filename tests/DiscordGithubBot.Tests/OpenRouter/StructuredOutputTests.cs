using System.Text.Json.Nodes;
using DiscordGithubBot.OpenRouter;

namespace DiscordGithubBot.Tests.OpenRouter;

public class StructuredOutputTests
{
    public sealed record Inner(string Path, string Reason);

    public sealed record Outer(string[] Titles, string Body, bool Flag, List<Inner> Items);

    /// <summary>
    /// Strict mode rejects a schema unless every object closes itself and requires every property; the
    /// exporter emits neither, so a regression here would fail every chat call at the provider.
    /// </summary>
    [Fact]
    public void Every_object_is_closed_and_requires_all_its_properties()
    {
        var schema = StructuredOutput.SchemaFor<Outer>().AsObject();

        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(["titles", "body", "flag", "items"], Names(schema["required"]!));

        var item = schema["properties"]!["items"]!["items"]!.AsObject();
        Assert.Equal("object", item["type"]!.GetValue<string>());
        Assert.False(item["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(["path", "reason"], Names(item["required"]!));
    }

    [Fact]
    public void No_field_is_left_nullable()
    {
        var json = StructuredOutput.SchemaFor<Outer>().ToJsonString();

        Assert.DoesNotContain("\"null\"", json);
    }

    [Fact]
    public void Answers_parse_back_case_insensitively()
    {
        var parsed = StructuredOutput.Parse<Outer>(
            """{"Titles":["a"],"body":"b","flag":true,"items":[{"path":"p","reason":"r"}]}""");

        Assert.Equal("a", parsed!.Titles.Single());
        Assert.Equal("p", parsed.Items.Single().Path);
    }

    private static string[] Names(JsonNode array) => array.AsArray().Select(n => n!.GetValue<string>()).ToArray();
}
