using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Common;
using FlowCapture.Application.Flows;
using FlowCapture.Domain;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Analysis;

public sealed record PartAnalysisInput(
    string UserMessage,
    string InputHash,
    IReadOnlySet<string> AllowedRefs,
    IReadOnlyList<string> LockedIds,
    IReadOnlySet<string> ExemptIds,
    JsonObject? Previous,
    string Department,
    string ModuleCode,
    Guid ProjectId,
    int EntryCount,
    int BaseVersionNo);

public sealed record WholeAnalysisInput(
    string UserMessage,
    string InputHash,
    Guid ProjectId,
    IReadOnlyDictionary<string, Guid> FlowRefs,
    IReadOnlyDictionary<string, IReadOnlySet<string>> ItemIdsByFlowRef);

/// <summary>Builds the XML-ish user message described in prompts/README.md. Only masked text is ever used.</summary>
public sealed class AnalysisInputBuilder(IAppDbContext db, IPhiMasker masker)
{
    public async Task<PartAnalysisInput> BuildPartAsync(
        Guid flowId, AnalysisMode mode, IReadOnlyCollection<Guid>? entryIds, CancellationToken ct)
    {
        var flow = await db.Flows.AsNoTracking()
            .Include(f => f.ProjectDepartment!).ThenInclude(pd => pd.Department)
            .FirstOrDefaultAsync(f => f.Id == flowId, ct) ?? throw new NotFoundException("Flow", flowId);
        var pd = flow.ProjectDepartment!;

        FlowVersion? previous = null;
        JsonObject? previousNode = null;
        string[] locked = [];
        if (mode == AnalysisMode.Reanalyse)
        {
            previous = await db.FlowVersions.AsNoTracking()
                .FirstOrDefaultAsync(v => v.FlowId == flowId && v.VersionNo == flow.CurrentVersionNo, ct)
                ?? throw new DomainException("There is no analysed version to re-analyse yet.");
            previousNode = JsonNode.Parse(previous.Content) as JsonObject;
            locked = JsonSerializer.Deserialize<string[]>(previous.LockedIds) ?? [];
        }

        var allEntries = await db.CaptureEntries.AsNoTracking().Include(e => e.Transcript)
            .Where(e => e.FlowId == flowId && !e.IsDeleted).OrderBy(e => e.RefNo).ToListAsync(ct);
        var selected = entryIds is { Count: > 0 } ? allEntries.Where(e => entryIds.Contains(e.Id)).ToList() : allEntries;

        var pending = selected.Where(e => e.Kind != EntryKind.Text && string.IsNullOrWhiteSpace(e.Transcript?.EffectiveText)).ToList();
        if (pending.Count > 0)
            throw new DomainException(
                $"{pending.Count} recording(s) or photo(s) are not turned into text yet ({string.Join(", ", pending.Select(e => e.Ref()))}). Wait for the text or remove them from this analysis.",
                "transcript_pending");

        var answered = await db.ClarificationItems.AsNoTracking()
            .Where(c => c.FlowId == flowId && c.Status == ClarificationStatus.Answered && c.Answer != null)
            .OrderBy(c => c.QuestionKey).ToListAsync(ct);

        var withText = selected.Where(e => e.Kind == EntryKind.Text ? !string.IsNullOrWhiteSpace(e.RawText) : true).ToList();
        if (mode == AnalysisMode.Initial && withText.Count == 0)
            throw new DomainException("There is no captured input to analyse. Add text or a recording first.", "no_input");

        var sb = new StringBuilder();
        var type = mode == AnalysisMode.Initial ? "part" : "reanalysis";
        var promptName = mode == AnalysisMode.Initial ? "part-analysis.v1" : "reanalysis.v1";
        sb.Append($"<analysis_request type=\"{type}\" prompt=\"{promptName}\">\n<context>\n");
        sb.Append($"<department>{Esc(pd.DisplayName())}</department>\n");
        sb.Append($"<module_code>{Esc(pd.ModuleCode)}</module_code>\n");
        sb.Append($"<flow_name>{Esc(flow.MenuName)}</flow_name>\n");
        var nextReq = previousNode is null ? 1 : NextRequirementNumber(previousNode);
        sb.Append($"<next_requirement_number>{nextReq}</next_requirement_number>\n</context>\n");

        if (previousNode is not null)
        {
            sb.Append("<previous_result>").Append(previousNode.ToJsonString()).Append("</previous_result>\n");
            sb.Append("<locked_items>").Append(JsonSerializer.Serialize(locked)).Append("</locked_items>\n");
        }

        if (answered.Count > 0 && mode == AnalysisMode.Reanalyse)
        {
            sb.Append("<user_answers>\n");
            foreach (var a in answered)
            {
                var masked = await masker.MaskAsync(a.Answer!, ct);
                sb.Append($"<answer ref=\"A{Mapping.NumberOf(a.QuestionKey)}\" question_id=\"{Esc(a.QuestionKey)}\">{Esc(masked.Text)}</answer>\n");
            }
            sb.Append("</user_answers>\n");
        }

        sb.Append("<entries>\n");
        foreach (var e in withText)
        {
            var body = e.Kind != EntryKind.Text ? e.Transcript!.EffectiveText! : (await masker.MaskAsync(e.RawText!, ct)).Text;
            if (!string.IsNullOrWhiteSpace(e.Notes))
                body += "\n\nNotes: " + (await masker.MaskAsync(e.Notes, ct)).Text;
            var role = string.IsNullOrWhiteSpace(e.IntervieweeRole) ? "" : (await masker.MaskAsync(e.IntervieweeRole, ct)).Text;
            var isNew = previous is not null && e.UpdatedAt > previous.CreatedAt;
            sb.Append($"<entry ref=\"{e.Ref()}\" kind=\"{e.Kind switch { EntryKind.Voice => "voice_transcript", EntryKind.Image => "photo_of_notes_text", _ => "text" }}\" language=\"{Esc(e.Language)}\"");
            if (role.Length > 0) sb.Append($" interviewee_role=\"{Esc(role)}\"");
            sb.Append($" captured_on=\"{e.CapturedAt:yyyy-MM-dd}\"");
            if (isNew) sb.Append(" new=\"true\"");
            sb.Append($">{Esc(body)}</entry>\n");
        }
        sb.Append("</entries>\n</analysis_request>");

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in allEntries) allowed.Add(e.Ref());
        var answeredAll = await db.ClarificationItems.AsNoTracking()
            .Where(c => c.FlowId == flowId && c.Status == ClarificationStatus.Answered).Select(c => c.QuestionKey).ToListAsync(ct);
        foreach (var k in answeredAll) allowed.Add($"A{Mapping.NumberOf(k)}");

        var exempt = new HashSet<string>(locked, StringComparer.Ordinal);
        if (previousNode is not null) exempt.UnionWith(FlowLockEnforcer.ItemsWithoutSources(previousNode));

        var message = sb.ToString();
        return new PartAnalysisInput(message, Hashing.Sha256Hex($"{mode}|{message}"), allowed, locked, exempt,
            previousNode, pd.DisplayName(), pd.ModuleCode, pd.ProjectId, withText.Count, flow.CurrentVersionNo);
    }

    public async Task<WholeAnalysisInput> BuildWholeAsync(Guid projectId, IReadOnlyCollection<Guid>? flowIds, CancellationToken ct)
    {
        var flows = await db.Flows.AsNoTracking()
            .Include(f => f.ProjectDepartment!).ThenInclude(pd => pd.Department)
            .Where(f => f.ProjectDepartment!.ProjectId == projectId && f.Status != FlowStatus.Draft && f.CurrentVersionNo > 0)
            .OrderBy(f => f.ProjectDepartment!.ModuleCode).ThenBy(f => f.MenuName).ToListAsync(ct);
        if (flowIds is { Count: > 0 }) flows = flows.Where(f => flowIds.Contains(f.Id)).ToList();
        if (flows.Count < 2)
            throw new DomainException("Whole-project analysis needs at least two analysed flows.", "not_enough_flows");

        var refs = new Dictionary<string, Guid>();
        var itemIds = new Dictionary<string, IReadOnlySet<string>>();
        var sb = new StringBuilder("<analysis_request type=\"whole\" prompt=\"whole-analysis.v1\">\n");
        var n = 0;
        foreach (var f in flows)
        {
            n++;
            var key = $"F{n}";
            var version = await db.FlowVersions.AsNoTracking()
                .FirstAsync(v => v.FlowId == f.Id && v.VersionNo == f.CurrentVersionNo, ct);
            var node = (JsonObject)JsonNode.Parse(version.Content)!;
            foreach (var strip in new[] { "mermaid", "changeLog", "confidence", "schemaVersion" }) node.Remove(strip);

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var collection in FlowContentValidator.Collections)
                if (node[collection] is JsonArray arr)
                    foreach (var item in arr.OfType<JsonObject>())
                        if (item[FlowLockEnforcer.IdProperty(collection)]?.GetValue<string>() is { } id) ids.Add(id);

            refs[key] = f.Id;
            itemIds[key] = ids;
            sb.Append($"<flow ref=\"{key}\" status=\"{f.Status}\" department=\"{Esc(f.ProjectDepartment!.DisplayName())}\" title=\"{Esc(f.MenuName)}\">")
              .Append(node.ToJsonString()).Append("</flow>\n");
        }
        sb.Append("</analysis_request>");
        var message = sb.ToString();
        return new WholeAnalysisInput(message, Hashing.Sha256Hex("whole|" + message), projectId, refs, itemIds);
    }

    private static int NextRequirementNumber(JsonObject previous)
    {
        var max = 0;
        if (previous["requirements"] is JsonArray reqs)
            foreach (var r in reqs.OfType<JsonObject>())
                max = Math.Max(max, Mapping.NumberOf(r["reqId"]?.GetValue<string>() ?? ""));
        return max + 1;
    }

    private static string Esc(string s) => SecurityElement.Escape(s) ?? "";
}
