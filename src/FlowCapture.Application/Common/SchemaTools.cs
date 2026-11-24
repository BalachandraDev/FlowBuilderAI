using System.Text.Json.Nodes;
using FlowCapture.Shared;

namespace FlowCapture.Application.Common;

/// <summary>Helpers for working with the JSON schemas that ship in /prompts/schemas.</summary>
public static class SchemaTools
{
    /// <summary>Replaces requirement.category enum with the active category codes plus "To be confirmed".</summary>
    public static JsonObject PatchCategories(JsonObject schema, IEnumerable<string> codes)
    {
        var clone = (JsonObject)schema.DeepClone();
        var cat = clone["$defs"]?["requirement"]?["properties"]?["category"] as JsonObject;
        if (cat is null) return clone;
        var arr = new JsonArray();
        foreach (var c in codes.Distinct(StringComparer.Ordinal)) arr.Add(c);
        arr.Add(Tbc.ToBeConfirmed);
        cat["enum"] = arr;
        return clone;
    }

    /// <summary>
    /// Returns a self-contained schema: every {"$ref": "#/$defs/x"} is replaced by the definition, and
    /// $defs / $schema / $id are removed. Used for the tool input_schema so the API never needs to resolve references.
    /// </summary>
    public static JsonObject InlineRefs(JsonObject schema)
    {
        var defs = schema["$defs"] as JsonObject ?? new JsonObject();
        var result = (JsonObject)Inline(schema, defs, 0)!;
        result.Remove("$defs");
        result.Remove("$schema");
        result.Remove("$id");
        return result;
    }

    private static JsonNode? Inline(JsonNode? node, JsonObject defs, int depth)
    {
        if (depth > 32) throw new InvalidOperationException("Schema reference nesting is too deep (recursive schema?).");
        switch (node)
        {
            case JsonObject obj:
            {
                if (obj["$ref"] is JsonValue refValue && refValue.TryGetValue<string>(out var reference))
                {
                    var name = reference.Replace("#/$defs/", "", StringComparison.Ordinal);
                    if (defs[name] is not JsonNode target)
                        throw new InvalidOperationException($"Schema reference '{reference}' was not found.");
                    var resolved = Inline(target.DeepClone(), defs, depth + 1) as JsonObject ?? new JsonObject();
                    foreach (var (key, value) in obj)
                        if (key != "$ref") resolved[key] = Inline(value?.DeepClone(), defs, depth + 1);
                    return resolved;
                }
                var copy = new JsonObject();
                foreach (var (key, value) in obj)
                    copy[key] = Inline(value?.DeepClone(), defs, depth + 1);
                return copy;
            }
            case JsonArray arr:
            {
                var copy = new JsonArray();
                foreach (var item in arr) copy.Add(Inline(item?.DeepClone(), defs, depth + 1));
                return copy;
            }
            default:
                return node?.DeepClone();
        }
    }
}
