using System.Text.Json.Nodes;
using Json.Schema;

namespace FlowCapture.Application.Analysis;

/// <summary>Validates a whole-project analysis result against project-analysis.v1 and the flows it was asked about.</summary>
public sealed class ProjectAnalysisValidator
{
    private readonly JsonSchema _schema;

    public ProjectAnalysisValidator(JsonObject schema) => _schema = JsonSchema.FromText(schema.ToJsonString());

    public List<string> Validate(JsonObject node, WholeAnalysisInput input)
    {
        var errors = new List<string>();
        var evaluation = _schema.Evaluate(node, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (!evaluation.IsValid)
        {
            foreach (var d in evaluation.Details.Where(d => !d.IsValid && d.Errors is not null))
                foreach (var (keyword, message) in d.Errors!)
                    errors.Add($"{d.InstanceLocation} ({keyword}): {message}");
            if (errors.Count == 0) errors.Add("The result does not match the project-analysis.v1 schema.");
            return errors.Take(40).ToList();
        }

        void CheckFlow(string? flowRef, string where)
        {
            if (flowRef is null || !input.FlowRefs.ContainsKey(flowRef)) errors.Add($"{where}: unknown flow '{flowRef}'.");
        }
        void CheckItem(JsonNode? item, string where)
        {
            var flowRef = item?["flowRef"]?.GetValue<string>();
            var itemId = item?["itemId"]?.GetValue<string>();
            CheckFlow(flowRef, where);
            if (flowRef is not null && itemId is not null && input.ItemIdsByFlowRef.TryGetValue(flowRef, out var ids) && !ids.Contains(itemId))
                errors.Add($"{where}: '{itemId}' does not exist in {flowRef}.");
        }

        foreach (var h in (node["handoffs"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var id = h["id"]?.GetValue<string>();
            CheckItem(h["from"], $"handoffs[{id}].from");
            CheckItem(h["to"], $"handoffs[{id}].to");
            if (h["from"]?["flowRef"]?.GetValue<string>() == h["to"]?["flowRef"]?.GetValue<string>())
                errors.Add($"handoffs[{id}]: a hand-off must connect two different flows.");
        }
        foreach (var d in (node["duplicates"] as JsonArray ?? []).OfType<JsonObject>())
            foreach (var r in (d["flowRefs"] as JsonArray ?? []).Select(x => x?.GetValue<string>()))
                CheckFlow(r, $"duplicates[{d["id"]}].flowRefs");
        foreach (var c in (node["conflicts"] as JsonArray ?? []).OfType<JsonObject>())
        {
            foreach (var r in (c["flowRefs"] as JsonArray ?? []).Select(x => x?.GetValue<string>()))
                CheckFlow(r, $"conflicts[{c["id"]}].flowRefs");
            foreach (var i in (c["itemRefs"] as JsonArray ?? []))
                CheckItem(i, $"conflicts[{c["id"]}].itemRefs");
        }
        foreach (var g in (node["gaps"] as JsonArray ?? []).OfType<JsonObject>())
            foreach (var r in (g["flowRefs"] as JsonArray ?? []).Select(x => x?.GetValue<string>()))
                CheckFlow(r, $"gaps[{g["id"]}].flowRefs");
        foreach (var q in (node["clarificationQuestions"] as JsonArray ?? []).OfType<JsonObject>())
            foreach (var r in (q["flowRefs"] as JsonArray ?? []).Select(x => x?.GetValue<string>()))
                CheckFlow(r, $"clarificationQuestions[{q["id"]}].flowRefs");

        foreach (var name in new[] { "handoffs", "duplicates", "conflicts", "gaps", "clarificationQuestions" })
            foreach (var dup in (node[name] as JsonArray ?? []).OfType<JsonObject>()
                         .Select(o => o["id"]?.GetValue<string>()).GroupBy(x => x).Where(g => g.Count() > 1))
                errors.Add($"{name}: id '{dup.Key}' is used more than once.");

        return errors.Take(40).ToList();
    }
}
