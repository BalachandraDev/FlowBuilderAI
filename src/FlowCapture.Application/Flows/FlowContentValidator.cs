using System.Text.Json;
using System.Text.Json.Nodes;
using FlowCapture.Shared;
using FlowCapture.Shared.Content;
using Json.Schema;

namespace FlowCapture.Application.Flows;

/// <param name="Department">Expected department name (must match content.department).</param>
/// <param name="ModuleCode">Expected module code.</param>
/// <param name="AllowedSourceRefs">Valid E#/A# refs. Null skips the check (manual edits).</param>
/// <param name="RequireSources">When true, every item that has a sourceEntryIds field must cite at least one ref, except exempt ids.</param>
/// <param name="ExemptIds">Ids (user-created or locked items) allowed to have no sources.</param>
public sealed record FlowValidationContext(
    string Department,
    string ModuleCode,
    IReadOnlySet<string>? AllowedSourceRefs,
    bool RequireSources,
    IReadOnlySet<string> ExemptIds)
{
    public static FlowValidationContext Manual(string department, string moduleCode) =>
        new(department, moduleCode, null, false, new HashSet<string>());
}

public sealed class FlowValidationResult
{
    public List<string> Errors { get; } = [];
    public JsonObject? Node { get; set; }
    public FlowContent? Content { get; set; }
    public bool IsValid => Errors.Count == 0 && Content is not null;
}

/// <summary>
/// Validates flow.v1 content: JSON Schema, referential integrity, source integrity and the "no blank fields" rule.
/// The same checks run on AI output and on manual edits so stored versions are always structurally sound.
/// </summary>
public sealed class FlowContentValidator
{
    private const int MaxErrors = 40;

    private static readonly string[] CollectionsWithIds =
    [
        "actors", "preConditions", "postConditions", "steps", "decisions", "alternateFlows", "exceptionFlows",
        "businessRules", "validations", "dataEntities", "screens", "reportsAndPrints", "integrations",
        "requirements", "acceptanceCriteria", "userRoles", "nonFunctionalNotes", "gaps", "clarificationQuestions",
    ];

    private static readonly HashSet<string> PlaceholderTokens = new(StringComparer.OrdinalIgnoreCase)
        { "tbc", "tbd", "n/a", "nil", "nill", "unknown", "-", "--", "null" };

    private readonly JsonSchema _schema;

    public FlowContentValidator(JsonObject schema)
    {
        _schema = JsonSchema.FromText(schema.ToJsonString());
    }

    public FlowValidationResult Validate(JsonObject node, FlowValidationContext ctx)
    {
        var result = new FlowValidationResult { Node = node };

        ValidateSchema(node, result.Errors);
        if (result.Errors.Count > 0) return result;

        FlowContent content;
        try { content = node.Deserialize<FlowContent>(FlowJson.Options)!; }
        catch (JsonException ex)
        {
            result.Errors.Add($"Content could not be read: {ex.Message}");
            return result;
        }
        result.Content = content;

        if (!string.Equals(content.Department, ctx.Department, StringComparison.Ordinal))
            Add(result.Errors, $"department must be '{ctx.Department}' but was '{content.Department}'.");
        if (!string.Equals(content.ModuleCode, ctx.ModuleCode, StringComparison.Ordinal))
            Add(result.Errors, $"moduleCode must be '{ctx.ModuleCode}' but was '{content.ModuleCode}'.");

        CheckReferences(content, result.Errors);
        Walk(node, "", null, ctx, result.Errors);

        if (result.Errors.Count > 0) result.Content = null;
        return result;
    }

    // ------------------------------------------------------------------ schema

    private void ValidateSchema(JsonNode node, List<string> errors)
    {
        var evaluation = _schema.Evaluate(node, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (evaluation.IsValid) return;

        foreach (var detail in evaluation.Details)
        {
            if (detail.IsValid || detail.Errors is null) continue;
            foreach (var (keyword, message) in detail.Errors)
                Add(errors, $"{detail.InstanceLocation} ({keyword}): {message}");
        }
        if (errors.Count == 0) Add(errors, "The content does not match the flow.v1 schema.");
    }

    // ------------------------------------------------------------------ referential integrity

    private static void CheckReferences(FlowContent c, List<string> errors)
    {
        CheckUnique("actors", c.Actors.Select(x => x.Id), errors);
        CheckUnique("preConditions", c.PreConditions.Select(x => x.Id), errors);
        CheckUnique("postConditions", c.PostConditions.Select(x => x.Id), errors);
        CheckUnique("steps", c.Steps.Select(x => x.Id), errors);
        CheckUnique("decisions", c.Decisions.Select(x => x.Id), errors);
        CheckUnique("alternateFlows", c.AlternateFlows.Select(x => x.Id), errors);
        CheckUnique("exceptionFlows", c.ExceptionFlows.Select(x => x.Id), errors);
        CheckUnique("businessRules", c.BusinessRules.Select(x => x.Id), errors);
        CheckUnique("validations", c.Validations.Select(x => x.Id), errors);
        CheckUnique("dataEntities", c.DataEntities.Select(x => x.Id), errors);
        CheckUnique("screens", c.Screens.Select(x => x.Id), errors);
        CheckUnique("reportsAndPrints", c.ReportsAndPrints.Select(x => x.Id), errors);
        CheckUnique("integrations", c.Integrations.Select(x => x.Id), errors);
        CheckUnique("requirements", c.Requirements.Select(x => x.ReqId), errors);
        CheckUnique("acceptanceCriteria", c.AcceptanceCriteria.Select(x => x.Id), errors);
        CheckUnique("userRoles", c.UserRoles.Select(x => x.Id), errors);
        CheckUnique("nonFunctionalNotes", c.NonFunctionalNotes.Select(x => x.Id), errors);
        CheckUnique("gaps", c.Gaps.Select(x => x.Id), errors);
        CheckUnique("clarificationQuestions", c.ClarificationQuestions.Select(x => x.Id), errors);

        if (c.Steps.Count == 0)
        {
            Add(errors, "steps: at least one step is required.");
            return;
        }

        var nodeIds = new HashSet<string>(c.Steps.Select(s => s.Id).Concat(c.Decisions.Select(d => d.Id)), StringComparer.Ordinal);
        bool Known(string id) => id == "END" || nodeIds.Contains(id);

        if (!c.Steps.Any(s => s.Id == "S1")) Add(errors, "steps: the first step must have id S1.");

        foreach (var s in c.Steps)
            foreach (var n in s.Next.Where(n => !Known(n)))
                Add(errors, $"steps[{s.Id}].next: '{n}' does not exist.");
        foreach (var d in c.Decisions)
        {
            if (!Known(d.YesPath.Next)) Add(errors, $"decisions[{d.Id}].yesPath.next: '{d.YesPath.Next}' does not exist.");
            if (!Known(d.NoPath.Next)) Add(errors, $"decisions[{d.Id}].noPath.next: '{d.NoPath.Next}' does not exist.");
        }
        foreach (var a in c.AlternateFlows)
        {
            if (!Known(a.BranchFrom)) Add(errors, $"alternateFlows[{a.Id}].branchFrom: '{a.BranchFrom}' does not exist.");
            if (!Known(a.RejoinAt)) Add(errors, $"alternateFlows[{a.Id}].rejoinAt: '{a.RejoinAt}' does not exist.");
        }

        // reachability from S1
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var s in c.Steps) adjacency[s.Id] = s.Next;
        foreach (var d in c.Decisions) adjacency[d.Id] = [d.YesPath.Next, d.NoPath.Next];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        if (adjacency.ContainsKey("S1")) stack.Push("S1");
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == "END" || !seen.Add(current)) continue;
            if (adjacency.TryGetValue(current, out var next))
                foreach (var n in next) stack.Push(n);
        }
        foreach (var id in nodeIds.Where(id => !seen.Contains(id)).OrderBy(id => id, StringComparer.Ordinal))
            Add(errors, $"'{id}' cannot be reached from S1.");

        // cross references between sections
        var allIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in nodeIds) allIds.Add(id);
        foreach (var id in c.AlternateFlows.Select(x => x.Id)) allIds.Add(id);
        foreach (var id in c.ExceptionFlows.Select(x => x.Id)) allIds.Add(id);
        foreach (var id in c.Screens.Select(x => x.Id)) allIds.Add(id);
        foreach (var id in c.DataEntities.Select(x => x.Id)) allIds.Add(id);
        foreach (var id in c.Integrations.Select(x => x.Id)) allIds.Add(id);
        foreach (var r in c.BusinessRules)
            foreach (var id in r.AppliesTo.Where(id => !allIds.Contains(id)))
                Add(errors, $"businessRules[{r.Id}].appliesTo: '{id}' does not exist.");

        var reqIds = new HashSet<string>(c.Requirements.Select(r => r.ReqId), StringComparer.Ordinal);
        foreach (var ac in c.AcceptanceCriteria.Where(ac => !reqIds.Contains(ac.ReqId)))
            Add(errors, $"acceptanceCriteria[{ac.Id}].reqId: '{ac.ReqId}' is not a requirement in this flow.");

        var gapIds = new HashSet<string>(c.Gaps.Select(g => g.Id), StringComparer.Ordinal);
        foreach (var q in c.ClarificationQuestions.Where(q => q.RelatedGapId is not null && !gapIds.Contains(q.RelatedGapId)))
            Add(errors, $"clarificationQuestions[{q.Id}].relatedGapId: '{q.RelatedGapId}' does not exist.");
    }

    private static void CheckUnique(string collection, IEnumerable<string> ids, List<string> errors)
    {
        foreach (var dup in ids.GroupBy(i => i, StringComparer.Ordinal).Where(g => g.Count() > 1))
            Add(errors, $"{collection}: id '{dup.Key}' is used more than once.");
    }

    // ------------------------------------------------------------------ sources and blank rule

    private static void Walk(JsonNode? node, string path, JsonObject? owner, FlowValidationContext ctx, List<string> errors)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var nextOwner = obj.ContainsKey("id") || obj.ContainsKey("reqId") ? obj : owner;
                foreach (var (key, value) in obj)
                {
                    if (key == "mermaid") continue;
                    var childPath = path.Length == 0 ? key : $"{path}.{key}";
                    if (key == "sourceEntryIds" && value is JsonArray refs)
                        CheckSources(refs, childPath, nextOwner, ctx, errors);
                    else
                        Walk(value, childPath, nextOwner, ctx, errors);
                }
                break;
            }
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++) Walk(arr[i], $"{path}[{i}]", owner, ctx, errors);
                break;
            case JsonValue val when val.TryGetValue<string>(out var s):
                CheckString(s, path, errors);
                break;
        }
    }

    private static void CheckSources(JsonArray refs, string path, JsonObject? owner, FlowValidationContext ctx, List<string> errors)
    {
        foreach (var r in refs.Select(x => x?.GetValue<string>() ?? ""))
            if (ctx.AllowedSourceRefs is not null && !ctx.AllowedSourceRefs.Contains(r))
                Add(errors, $"{path}: unknown source '{r}'.");

        if (refs.Count == 0 && ctx.RequireSources)
        {
            var ownerId = owner?["id"]?.GetValue<string>() ?? owner?["reqId"]?.GetValue<string>() ?? "";
            if (!ctx.ExemptIds.Contains(ownerId))
                Add(errors, $"{path}: '{ownerId}' must cite at least one source (E#/A#). If the input does not support it, remove the item.");
        }
    }

    private static void CheckString(string value, string path, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(errors, $"{path}: value is blank. Use \"{Tbc.ToBeConfirmed}\" when unknown.");
            return;
        }
        var trimmed = value.Trim();
        if (string.Equals(trimmed, Tbc.ToBeConfirmed, StringComparison.OrdinalIgnoreCase) && trimmed != Tbc.ToBeConfirmed)
            Add(errors, $"{path}: write exactly \"{Tbc.ToBeConfirmed}\".");
        else if (PlaceholderTokens.Contains(trimmed))
            Add(errors, $"{path}: '{trimmed}' is not allowed. Use \"{Tbc.ToBeConfirmed}\" or \"{Tbc.NotApplicable}\".");
    }

    private static void Add(List<string> errors, string message)
    {
        if (errors.Count < MaxErrors) errors.Add(message);
    }

    /// <summary>Names of top-level collections whose items carry ids (used by lock enforcement and diff).</summary>
    public static IReadOnlyList<string> Collections => CollectionsWithIds;
}
