using System.Text.Json.Nodes;

namespace FlowCapture.Application.Flows;

/// <summary>
/// User edits marked as locked are authoritative. After an AI re-analysis the server compares the model output with
/// the previous version and restores anything locked that the model changed or dropped. The prompt asks the model to
/// respect locks; this class guarantees it.
/// </summary>
public static class FlowLockEnforcer
{
    public static readonly string[] LockableScalars = ["flowTitle", "purpose", "scope", "trigger", "useCase"];

    /// <summary>Id property per collection (requirements are keyed by reqId).</summary>
    public static string IdProperty(string collection) => collection == "requirements" ? "reqId" : "id";

    /// <summary>Modifies <paramref name="result"/> in place. Returns a human readable warning per restored item.</summary>
    public static List<string> Enforce(JsonObject previous, JsonObject result, IReadOnlyCollection<string> lockedIds)
    {
        var warnings = new List<string>();
        foreach (var locked in lockedIds.Distinct(StringComparer.Ordinal))
        {
            if (LockableScalars.Contains(locked, StringComparer.Ordinal))
            {
                var before = previous[locked];
                if (before is not null && !JsonNode.DeepEquals(before, result[locked]))
                {
                    result[locked] = before.DeepClone();
                    warnings.Add($"Locked field '{locked}' was changed by the AI and has been restored.");
                }
                continue;
            }

            foreach (var collection in FlowContentValidator.Collections)
            {
                if (previous[collection] is not JsonArray prevArray) continue;
                var idProp = IdProperty(collection);
                var prevIndex = IndexOf(prevArray, idProp, locked);
                if (prevIndex < 0) continue;

                var prevItem = prevArray[prevIndex]!;
                var resultArray = result[collection] as JsonArray;
                if (resultArray is null)
                {
                    resultArray = new JsonArray();
                    result[collection] = resultArray;
                }

                var resultIndex = IndexOf(resultArray, idProp, locked);
                if (resultIndex < 0)
                {
                    resultArray.Insert(Math.Min(prevIndex, resultArray.Count), prevItem.DeepClone());
                    warnings.Add($"Locked item '{locked}' was removed by the AI and has been restored.");
                }
                else if (!JsonNode.DeepEquals(prevItem, resultArray[resultIndex]))
                {
                    resultArray[resultIndex] = prevItem.DeepClone();
                    warnings.Add($"Locked item '{locked}' was changed by the AI and has been restored.");
                }
                break;
            }
        }
        return warnings;
    }

    private static int IndexOf(JsonArray array, string idProp, string id)
    {
        for (var i = 0; i < array.Count; i++)
            if (array[i] is JsonObject o && o[idProp]?.GetValue<string>() == id) return i;
        return -1;
    }

    /// <summary>Ids of items in <paramref name="content"/> that cite no sources (user-created items). They are exempt from the source rule.</summary>
    public static HashSet<string> ItemsWithoutSources(JsonObject content)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var collection in FlowContentValidator.Collections)
        {
            if (content[collection] is not JsonArray arr) continue;
            var idProp = IdProperty(collection);
            foreach (var item in arr.OfType<JsonObject>())
                if (item["sourceEntryIds"] is JsonArray s && s.Count == 0 && item[idProp]?.GetValue<string>() is { } id)
                    ids.Add(id);
        }
        return ids;
    }
}
