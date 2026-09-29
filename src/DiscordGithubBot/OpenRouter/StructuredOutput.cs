using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace DiscordGithubBot.OpenRouter;

/// <summary>
/// JSON schemas for strict structured output, generated from the C# type the answer is parsed into, so
/// the schema a model is held to and the shape the code reads can never drift apart. Strict mode
/// (OpenAI's, which OpenRouter passes through) insists that every object closes itself with
/// <c>additionalProperties: false</c> and lists every property as required; the exporter emits neither,
/// and marks reference types nullable, so each node is tightened on the way out.
/// </summary>
public static class StructuredOutput
{
    /// <summary>camelCase on the wire, case-insensitive on the way back in — the web defaults.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>The strict schema for <typeparamref name="T"/>; a fresh node each call, since callers embed it.</summary>
    public static JsonNode SchemaFor<T>() => Cache<T>.Schema.DeepClone();

    public static T? Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions);

    private static class Cache<T>
    {
        public static readonly JsonNode Schema = JsonOptions.GetJsonSchemaAsNode(typeof(T), new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (_, node) => Tighten(node),
        });
    }

    private static JsonNode Tighten(JsonNode node)
    {
        if (node is not JsonObject obj) return node;

        // ["string", "null"] -> "string": every DTO field is always present, and strict mode would
        // otherwise accept a null the parsing code does not expect.
        if (obj["type"] is JsonArray types)
        {
            var concrete = types.Select(t => t!.GetValue<string>()).Where(t => t != "null").ToList();
            if (concrete.Count == 1) obj["type"] = concrete[0];
        }

        if (obj["properties"] is JsonObject properties)
        {
            obj["additionalProperties"] = false;
            obj["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray());
        }

        return node;
    }
}
