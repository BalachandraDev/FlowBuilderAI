using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Common;
using FlowCapture.Domain;
using FlowCapture.Shared;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Analysis;

public sealed record ComposedPrompt(
    string SystemPrompt,
    IReadOnlyList<Guid> TemplateIds,
    string Sha256,
    JsonObject ToolSchema,
    string ToolName,
    string ToolDescription);

/// <summary>
/// Builds the system prompt from versioned PromptTemplate rows. Placeholders:
/// {{include:common-rules.v1}}, {{schema:flow.v1}}, {{example:mrd-003.expected}}, {{category_definitions}}.
/// </summary>
public sealed partial class PromptComposer(IAppDbContext db)
{
    [GeneratedRegex(@"\{\{\s*([a-z_]+)(?::([^}\s]+))?\s*\}\}")]
    private static partial Regex PlaceholderRegex();

    public const string FlowToolName = "submit_flow_analysis";
    public const string ProjectToolName = "submit_project_analysis";

    public async Task<ComposedPrompt> ComposeAsync(string taskKey, CancellationToken ct)
    {
        var templates = await db.PromptTemplates.AsNoTracking().ToListAsync(ct);
        var categories = await ActiveCategoriesAsync(ct);

        PromptTemplate Find(string key, int? version) =>
            (version is null
                ? templates.Where(t => t.Key == key && t.IsActive).OrderByDescending(t => t.Version).FirstOrDefault()
                : templates.FirstOrDefault(t => t.Key == key && t.Version == version))
            ?? throw new DomainException($"Prompt template '{key}'{(version is null ? "" : $" v{version}")} is not available.", "prompt_missing");

        var task = Find(taskKey, null);
        var used = new HashSet<Guid> { task.Id };
        var text = task.Content;

        string Resolve(string kind, string arg)
        {
            switch (kind)
            {
                case "include":
                {
                    var (key, version) = SplitVersion(arg);
                    var t = Find(key, version);
                    used.Add(t.Id);
                    return t.Content.TrimEnd();
                }
                case "schema":
                {
                    var (name, version) = SplitVersion(arg);
                    var t = Find("schema." + name, version);
                    used.Add(t.Id);
                    var node = JsonNode.Parse(t.Content) as JsonObject ?? throw new DomainException($"Schema '{name}' is not valid JSON.");
                    if (name == "flow") node = SchemaTools.PatchCategories(node, categories.Select(c => c.Code));
                    return node.ToJsonString();
                }
                case "example":
                {
                    var t = Find("example." + arg, null);
                    used.Add(t.Id);
                    return t.Content.TrimEnd();
                }
                default:
                    throw new DomainException($"Unknown prompt placeholder '{{{{{kind}}}}}'.", "prompt_invalid");
            }
        }

        for (var pass = 0; pass < 5 && PlaceholderRegex().IsMatch(text); pass++)
        {
            text = PlaceholderRegex().Replace(text, m =>
            {
                var kind = m.Groups[1].Value;
                if (kind == "category_definitions")
                    return string.Join("; ", categories.Select(c => $"{c.Code} = {c.Name} ({c.Definition})"));
                return Resolve(kind, m.Groups[2].Value);
            });
        }
        if (PlaceholderRegex().IsMatch(text))
            throw new DomainException("Prompt placeholders are nested too deeply.", "prompt_invalid");

        var whole = taskKey == PromptKeys.WholeAnalysis;
        var schema = whole ? await GetProjectSchemaAsync(ct) : await GetFlowSchemaAsync(ct);
        return new ComposedPrompt(
            text,
            used.ToList(),
            Hashing.Sha256Hex(text),
            SchemaTools.InlineRefs(schema),
            whole ? ProjectToolName : FlowToolName,
            whole
                ? "Submit the cross-department analysis result. Call this tool exactly once with the complete result."
                : "Submit the structured flow analysis result. Call this tool exactly once with the complete result.");
    }

    /// <summary>flow.v1 schema with the category enum patched to the active categories. Used for validation.</summary>
    public async Task<JsonObject> GetFlowSchemaAsync(CancellationToken ct)
    {
        var node = await LoadSchemaAsync(PromptKeys.FlowSchema, ct);
        var categories = await ActiveCategoriesAsync(ct);
        return SchemaTools.PatchCategories(node, categories.Select(c => c.Code));
    }

    public Task<JsonObject> GetProjectSchemaAsync(CancellationToken ct) => LoadSchemaAsync(PromptKeys.ProjectSchema, ct);

    public async Task<IReadOnlyList<string>> ActiveCategoryCodesAsync(CancellationToken ct) =>
        (await ActiveCategoriesAsync(ct)).Select(c => c.Code).ToList();

    public async Task<string> BuildRetryFeedbackAsync(IReadOnlyList<string> errors, CancellationToken ct)
    {
        var template = await db.PromptTemplates.AsNoTracking()
            .Where(t => t.Key == PromptKeys.RetryFeedback && t.IsActive).OrderByDescending(t => t.Version)
            .FirstOrDefaultAsync(ct)
            ?? throw new DomainException("The retry prompt template is not available.", "prompt_missing");
        var list = string.Join("\n", errors.Select(e => "- " + e));
        return template.Content.Replace("{{errors}}", list, StringComparison.Ordinal);
    }

    private async Task<JsonObject> LoadSchemaAsync(string key, CancellationToken ct)
    {
        var t = await db.PromptTemplates.AsNoTracking()
            .Where(x => x.Key == key && x.IsActive).OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct)
            ?? throw new DomainException($"Schema '{key}' is not available.", "prompt_missing");
        return JsonNode.Parse(t.Content) as JsonObject ?? throw new DomainException($"Schema '{key}' is not valid JSON.");
    }

    private Task<List<RequirementCategory>> ActiveCategoriesAsync(CancellationToken ct) =>
        db.RequirementCategories.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.SortOrder).ThenBy(c => c.Code).ToListAsync(ct);

    private static (string Key, int Version) SplitVersion(string arg)
    {
        var m = Regex.Match(arg, @"^(.+)\.v(\d+)$");
        if (!m.Success) throw new DomainException($"Placeholder argument '{arg}' must end in .vN.", "prompt_invalid");
        return (m.Groups[1].Value, int.Parse(m.Groups[2].Value));
    }
}
