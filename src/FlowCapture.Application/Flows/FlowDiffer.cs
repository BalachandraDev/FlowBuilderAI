using System.Text.Json;
using System.Text.Json.Nodes;
using FlowCapture.Shared.Dtos;

namespace FlowCapture.Application.Flows;

/// <summary>Item-level comparison of two flow.v1 documents (items matched by id / reqId).</summary>
public static class FlowDiffer
{
    private static readonly string[] Scalars = ["flowTitle", "department", "moduleCode", "purpose", "scope", "trigger", "useCase"];

    public static IReadOnlyList<DiffItemDto> Diff(JsonObject from, JsonObject to)
    {
        var items = new List<DiffItemDto>();

        foreach (var field in Scalars)
        {
            var a = from[field];
            var b = to[field];
            if (!JsonNode.DeepEquals(a, b))
                items.Add(new DiffItemDto(field, field, a is null ? "Added" : b is null ? "Removed" : "Modified", ToElement(a), ToElement(b)));
        }

        foreach (var collection in FlowContentValidator.Collections)
        {
            var idProp = FlowLockEnforcer.IdProperty(collection);
            var before = Index(from[collection] as JsonArray, idProp);
            var after = Index(to[collection] as JsonArray, idProp);

            foreach (var (id, node) in before)
                if (!after.TryGetValue(id, out var other))
                    items.Add(new DiffItemDto(collection, id, "Removed", ToElement(node), null));
                else if (!JsonNode.DeepEquals(node, other))
                    items.Add(new DiffItemDto(collection, id, "Modified", ToElement(node), ToElement(other)));

            foreach (var (id, node) in after)
                if (!before.ContainsKey(id))
                    items.Add(new DiffItemDto(collection, id, "Added", null, ToElement(node)));
        }
        return items;
    }

    private static Dictionary<string, JsonNode> Index(JsonArray? array, string idProp)
    {
        var map = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        if (array is null) return map;
        foreach (var item in array.OfType<JsonObject>())
            if (item[idProp]?.GetValue<string>() is { } id) map[id] = item;
        return map;
    }

    private static JsonElement? ToElement(JsonNode? node) =>
        node is null ? null : JsonSerializer.SerializeToElement(node);
}
