using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Common;
using FlowCapture.Application.Flows;
using FlowCapture.Application.Services;
using FlowCapture.Domain;
using FlowCapture.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FlowCapture.Application.Analysis;

/// <summary>Executes one queued AnalysisRun (called from a background job). Idempotent: only a Queued run is processed.</summary>
public sealed class AnalysisRunner(
    IAppDbContext db,
    IClock clock,
    ISettingsService settings,
    PromptComposer composer,
    AnalysisInputBuilder inputs,
    IFlowAnalysisService ai,
    AiUsageRecorder usage,
    FlowVersionWriter writer,
    IPushSender push,
    ILogger<AnalysisRunner> log)
{
    private sealed class RunFailure(string code, string message, string? raw = null) : Exception(message)
    {
        public string Code { get; } = code;
        public string? Raw { get; } = raw;
    }

    /// <summary>Accumulates what happened so it can be saved even when the run fails half way.</summary>
    private sealed class RunState
    {
        public int Attempts;
        public int InputTokens, CacheCreation, CacheRead, OutputTokens;
        public string Model = "";
        public string PromptIds = "[]";
        public string PromptSha = "";
        public List<string> Warnings = [];
        public string? LastRaw;
        public int? ResultVersionNo;
        public Guid? ResultVersionId;
        public string? ResultJson;

        public void Add(AiCallResult r)
        {
            Attempts++;
            InputTokens += r.InputTokens;
            CacheCreation += r.CacheCreationTokens;
            CacheRead += r.CacheReadTokens;
            OutputTokens += r.OutputTokens;
            Model = r.Model;
            LastRaw = r.ToolInputJson;
        }
    }

    public async Task ExecuteAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.AnalysisRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null || run.Status != AnalysisStatus.Queued) return;

        run.Status = AnalysisStatus.Running;
        run.StartedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        var requestedBy = run.RequestedBy;
        var flowId = run.FlowId;
        var scope = run.Scope;
        var state = new RunState { Model = run.Model };
        var clockStart = Stopwatch.StartNew();
        string? errorCode = null, errorDetail = null;

        try
        {
            var cfg = await settings.GetAiAsync(ct);
            state.Model = cfg.Model;
            if (scope == AnalysisScope.Part) await RunPartAsync(run, cfg, state, ct);
            else await RunWholeAsync(run, cfg, state, ct);
        }
        catch (RunFailure f) { errorCode = f.Code; errorDetail = f.Message; state.LastRaw = f.Raw ?? state.LastRaw; }
        catch (AiServiceException e) { errorCode = e.Code; errorDetail = e.Message; }
        catch (DomainException e) { errorCode = e.Code; errorDetail = e.Message; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { errorCode = "cancelled"; errorDetail = "The job was cancelled."; }
        catch (Exception e)
        {
            log.LogError(e, "Analysis run {RunId} failed unexpectedly", runId);
            errorCode = "unexpected_error";
            errorDetail = "An unexpected error occurred while analysing. The run can be retried.";
        }

        // Always reload the run so a failed SaveChanges above cannot leave a half-tracked graph behind.
        if (errorCode is not null) db.ChangeTracker.Clear();
        run = await db.AnalysisRuns.FirstAsync(r => r.Id == runId, CancellationToken.None);
        var cfgForCost = await settings.GetAiAsync(CancellationToken.None);

        run.Attempts = state.Attempts;
        run.Model = state.Model;
        run.InputTokens = state.InputTokens + state.CacheCreation + state.CacheRead;
        run.CachedInputTokens = state.CacheRead;
        run.OutputTokens = state.OutputTokens;
        run.EstimatedCost = EstimateCost(cfgForCost, state);
        run.PromptTemplateIds = state.PromptIds;
        run.SystemPromptSha256 = state.PromptSha;
        run.Warnings = JsonSerializer.Serialize(state.Warnings);
        run.DurationMs = (int)clockStart.ElapsedMilliseconds;
        run.FinishedAt = clock.UtcNow;
        if (errorCode is null)
        {
            run.Status = AnalysisStatus.Succeeded;
            run.ResultFlowVersionId = state.ResultVersionId;
            run.ResultVersionNo = state.ResultVersionNo;
            run.ResultJson = state.ResultJson;
        }
        else
        {
            run.Status = AnalysisStatus.Failed;
            run.ErrorCode = errorCode;
            run.ErrorDetail = errorDetail;
            run.RawOutput = state.LastRaw;
        }
        await db.SaveChangesAsync(CancellationToken.None);

        if (state.Attempts > 0)
            await usage.RecordAsync(AiUsageKind.Analysis, requestedBy, run.ProjectId, flowId, null, state.Model,
                state.InputTokens + state.CacheCreation + state.CacheRead, state.OutputTokens, run.DurationMs, errorCode is null, CancellationToken.None);

        await NotifyAsync(requestedBy, flowId, scope, errorCode, runId);
    }

    // ------------------------------------------------------------------ part analysis

    private async Task RunPartAsync(AnalysisRun run, AiSettings cfg, RunState state, CancellationToken ct)
    {
        var flowId = run.FlowId ?? throw new RunFailure("invalid_run", "A part analysis needs a flow.");
        var entryIds = ReadGuids(run.ParametersJson, "entryIds");
        var input = await inputs.BuildPartAsync(flowId, run.Mode, entryIds, ct);

        var composed = await composer.ComposeAsync(run.Mode == AnalysisMode.Initial ? PromptKeys.PartAnalysis : PromptKeys.Reanalysis, ct);
        state.PromptIds = JsonSerializer.Serialize(composed.TemplateIds);
        state.PromptSha = composed.Sha256;

        var validator = new FlowContentValidator(await composer.GetFlowSchemaAsync(ct));
        var vctx = new FlowValidationContext(input.Department, input.ModuleCode, input.AllowedRefs, true, input.ExemptIds);

        FlowValidationResult? valid = null;
        var userMessage = input.UserMessage;
        List<string> errors = [];
        var onlyCompleteness = false;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var result = await ai.CallAsync(new AiCallRequest(
                cfg.Model, composed.SystemPrompt, userMessage, composed.ToolName, composed.ToolDescription,
                composed.ToolSchema, cfg.MaxTokens, cfg.Effort, cfg.DisableThinking, Careful: attempt == 2), ct);
            state.Add(result);

            var node = TryParseObject(result.ToolInputJson);
            if (node is null)
            {
                errors = ["The tool input was not a JSON object."];
            }
            else
            {
                if (run.Mode == AnalysisMode.Reanalyse && input.Previous is not null)
                    state.Warnings.AddRange(FlowLockEnforcer.Enforce(input.Previous, node, input.LockedIds));
                var vr = validator.Validate(node, vctx);
                if (vr.IsValid)
                {
                    // a list in the notes must not come back as one squashed step: ask once more (never more than once: it costs tokens)
                    var squashed = AnalysisCompleteness.Check(input.UserMessage, vr.Content!);
                    if (squashed.Count == 0) { valid = vr; break; }
                    if (attempt == 2)
                    {
                        state.Warnings.Add("The notes list several items, but they were not all written as steps of their own. Read the workflow and add the missing steps if they are separate pages.");
                        valid = vr;
                        break;
                    }
                    errors = [.. squashed];
                    onlyCompleteness = true;
                }
                else errors = vr.Errors;
            }

            if (attempt == 1)
            {
                var feedback = await composer.BuildRetryFeedbackAsync(errors, ct);
                // a missing step is told to the AI in words and it writes the whole answer again from the notes: the first answer is not sent back
                // (it would cost thousands of tokens for nothing). A broken answer is sent back, so the AI can repair it.
                if (onlyCompleteness) userMessage = $"{input.UserMessage}\n\n{feedback}";
                else
                {
                    var previous = result.ToolInputJson.Length > 30_000 ? result.ToolInputJson[..30_000] : result.ToolInputJson;
                    userMessage = $"{input.UserMessage}\n\n{feedback}\n\n<previous_output>{System.Security.SecurityElement.Escape(previous)}</previous_output>";
                }
            }
        }

        if (valid is null)
            throw new RunFailure("validation_failed",
                "The AI result did not pass validation after a retry: " + string.Join(" | ", errors.Take(8)), state.LastRaw);

        var content = valid.Content!;
        if (string.IsNullOrWhiteSpace(content.Mermaid) || content.Mermaid.Trim().Split('\n').Length <= 2)
            content.Mermaid = MermaidBuilder.Build(content);              // asked to write only the first line: the server writes the rest (the picture itself is drawn by FlowDiagramBuilder)
        else if (!MermaidSyntaxChecker.IsAcceptable(content.Mermaid, content))
        {
            content.Mermaid = MermaidBuilder.Build(content);
            state.Warnings.Add("The AI diagram failed a structural check and was rebuilt from the steps.");
        }

        var flow = await db.Flows.Include(f => f.ProjectDepartment).FirstAsync(f => f.Id == flowId, ct);
        if (flow.CurrentVersionNo != input.BaseVersionNo)
            throw new RunFailure("stale_base", "The flow was edited while the analysis was running. Run the analysis again so your edits are kept.");

        var summary = content.ChangeLog.Count == 0
            ? "Initial analysis"
            : string.Join("; ", content.ChangeLog.Take(6).Select(c => $"{c.Change} {c.ItemId}"));
        var version = await writer.AddAsync(flow, content, VersionSource.Analysis, run.Id, input.LockedIds, summary, run.RequestedBy, ct);
        state.ResultVersionNo = version.VersionNo;
        state.ResultVersionId = version.Id;
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ whole-project analysis

    private async Task RunWholeAsync(AnalysisRun run, AiSettings cfg, RunState state, CancellationToken ct)
    {
        var flowIds = ReadGuids(run.ParametersJson, "flowIds");
        var input = await inputs.BuildWholeAsync(run.ProjectId, flowIds, ct);

        var composed = await composer.ComposeAsync(PromptKeys.WholeAnalysis, ct);
        state.PromptIds = JsonSerializer.Serialize(composed.TemplateIds);
        state.PromptSha = composed.Sha256;
        var validator = new ProjectAnalysisValidator(await composer.GetProjectSchemaAsync(ct));

        JsonObject? valid = null;
        var userMessage = input.UserMessage;
        List<string> errors = [];

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var result = await ai.CallAsync(new AiCallRequest(
                cfg.Model, composed.SystemPrompt, userMessage, composed.ToolName, composed.ToolDescription,
                composed.ToolSchema, cfg.MaxTokens, cfg.Effort, cfg.DisableThinking, Careful: attempt == 2), ct);
            state.Add(result);

            var node = TryParseObject(result.ToolInputJson);
            errors = node is null ? ["The tool input was not a JSON object."] : validator.Validate(node, input);
            if (errors.Count == 0) { valid = node; break; }

            if (attempt == 1)
            {
                var feedback = await composer.BuildRetryFeedbackAsync(errors, ct);
                userMessage = $"{input.UserMessage}\n\n{feedback}";
            }
        }

        if (valid is null)
            throw new RunFailure("validation_failed",
                "The AI result did not pass validation after a retry: " + string.Join(" | ", errors.Take(8)), state.LastRaw);

        await StoreCrossFlowResultAsync(run, input, valid, ct);
        state.ResultJson = valid.ToJsonString();
        await db.SaveChangesAsync(ct);
    }

    private async Task StoreCrossFlowResultAsync(AnalysisRun run, WholeAnalysisInput input, JsonObject result, CancellationToken ct)
    {
        var flowIds = input.FlowRefs.Values.ToHashSet();
        var existing = await db.CrossFlowLinks.Where(l => l.ProjectId == run.ProjectId).ToListAsync(ct);
        var kept = new HashSet<Guid>();

        foreach (var h in (result["handoffs"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var fromFlow = input.FlowRefs[h["from"]!["flowRef"]!.GetValue<string>()];
            var toFlow = input.FlowRefs[h["to"]!["flowRef"]!.GetValue<string>()];
            var fromItem = h["from"]!["itemId"]!.GetValue<string>();
            var toItem = h["to"]!["itemId"]!.GetValue<string>();
            var data = JsonSerializer.Serialize((h["dataPassed"] as JsonArray ?? []).Select(x => x!.GetValue<string>()).ToArray());

            var match = existing.FirstOrDefault(l => l.FromFlowId == fromFlow && l.FromItemId == fromItem && l.ToFlowId == toFlow && l.ToItemId == toItem);
            if (match is null)
            {
                match = new CrossFlowLink
                {
                    ProjectId = run.ProjectId, FromFlowId = fromFlow, FromItemId = fromItem, ToFlowId = toFlow, ToItemId = toItem,
                    Status = LinkStatus.Proposed,
                };
                db.CrossFlowLinks.Add(match);
            }
            if (match.Status == LinkStatus.Proposed || match.AnalysisRunId == Guid.Empty)
            {
                match.Description = h["description"]!.GetValue<string>();
                match.DataPassedJson = data;
                match.Basis = h["basis"]!.GetValue<string>();
                match.AnalysisRunId = run.Id;
            }
            kept.Add(match.Id);
        }

        // Proposed links between these flows that the new analysis no longer supports are dropped. Decided links stay.
        foreach (var stale in existing.Where(l => l.Status == LinkStatus.Proposed && !kept.Contains(l.Id)
                                                  && flowIds.Contains(l.FromFlowId) && flowIds.Contains(l.ToFlowId)))
            db.CrossFlowLinks.Remove(stale);

        // Project-level questions are attached to the first flow they mention, with keys X1, X2 ...
        var questions = new List<(string Text, string? FlowRef, string Section)>();
        foreach (var q in (result["clarificationQuestions"] as JsonArray ?? []).OfType<JsonObject>())
            questions.Add((q["question"]!.GetValue<string>(), FirstRef(q["flowRefs"]), "general"));
        foreach (var c in (result["conflicts"] as JsonArray ?? []).OfType<JsonObject>())
            questions.Add((c["suggestedQuestion"]!.GetValue<string>(), FirstRef(c["flowRefs"]), "general"));

        foreach (var (text, flowRef, section) in questions)
        {
            if (flowRef is null || !input.FlowRefs.TryGetValue(flowRef, out var targetFlow)) continue;
            var already = await db.ClarificationItems.AnyAsync(c => c.FlowId == targetFlow && c.Question == text, ct)
                          || db.ChangeTracker.Entries<ClarificationItem>().Any(e => e.Entity.FlowId == targetFlow && e.Entity.Question == text);
            if (already) continue;
            var next = (await db.ClarificationItems.CountAsync(c => c.FlowId == targetFlow && c.QuestionKey.StartsWith("X"), ct))
                       + db.ChangeTracker.Entries<ClarificationItem>().Count(e => e.State == EntityState.Added && e.Entity.FlowId == targetFlow && e.Entity.QuestionKey.StartsWith("X"))
                       + 1;
            db.ClarificationItems.Add(new ClarificationItem
            {
                FlowId = targetFlow, QuestionKey = $"X{next}", FirstSeenVersionNo = 0, Section = section, Question = text,
                Status = ClarificationStatus.Open,
            });
        }
    }

    // ------------------------------------------------------------------ helpers

    private async Task NotifyAsync(Guid userId, Guid? flowId, AnalysisScope scope, string? errorCode, Guid runId)
    {
        try
        {
            var title = errorCode is null ? "Analysis ready" : "Analysis failed";
            var body = scope == AnalysisScope.Whole
                ? (errorCode is null ? "The whole-project analysis has finished." : "The whole-project analysis could not be completed.")
                : (errorCode is null ? "Your flow analysis has finished. Open it to review." : "Your flow analysis could not be completed.");
            await push.SendToUserAsync(userId, title, body,
                new Dictionary<string, string> { ["type"] = "analysis", ["runId"] = runId.ToString(), ["flowId"] = flowId?.ToString() ?? "" },
                CancellationToken.None);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Push notification for run {RunId} failed", runId);
        }
    }

    private static decimal EstimateCost(AiSettings c, RunState s) =>
        (s.InputTokens * c.PricePerMTokInput
         + s.CacheCreation * c.PricePerMTokInput * c.CacheWriteMultiplier
         + s.CacheRead * c.PricePerMTokCacheRead
         + s.OutputTokens * c.PricePerMTokOutput) / 1_000_000m;

    private static JsonObject? TryParseObject(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static Guid[]? ReadGuids(string json, string property)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node?[property] is JsonArray arr && arr.Count > 0)
                return arr.Select(x => Guid.Parse(x!.GetValue<string>())).ToArray();
        }
        catch (Exception e) when (e is JsonException or FormatException) { }
        return null;
    }

    private static string? FirstRef(JsonNode? refs) =>
        (refs as JsonArray)?.Select(x => x?.GetValue<string>()).FirstOrDefault(x => x is not null);
}
