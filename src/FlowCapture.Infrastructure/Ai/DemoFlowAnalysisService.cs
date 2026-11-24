using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlowCapture.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FlowCapture.Infrastructure.Ai;

/// <summary>
/// A stand-in for Claude for trying the whole product without an Anthropic key (set Ai:Provider=Demo). It does NOT analyse anything: it answers
/// every request with the worked MRD-003 example from the prompt pack, with the department, module and source references matched to the request,
/// so the review screens, versions and documents can be shown end to end. The health check reports this mode, and it must never be used for real work.
/// </summary>
public sealed partial class DemoFlowAnalysisService : IFlowAnalysisService
{
    private readonly string _examplePath;

    public DemoFlowAnalysisService(IConfiguration config, ILogger<DemoFlowAnalysisService> log)
    {
        var root = config["Prompts:Path"] is { Length: > 0 } p ? p : Path.Combine(AppContext.BaseDirectory, "prompts");
        _examplePath = Path.Combine(root, "examples", "mrd-003.expected.json");
        log.LogWarning("AI is in DEMO mode (Ai:Provider=Demo): analyses return a fixed example, not a real analysis of the notes.");
    }

    public Task<AiCallResult> CallAsync(AiCallRequest request, CancellationToken ct)
    {
        var json = request.ToolName switch
        {
            "submit_project_analysis" => ProjectExample(),
            "submit_implementation_plan" => ImplementationExample,
            _ => FlowExample(request.UserMessage),
        };
        return Task.FromResult(new AiCallResult(json, 0, 0, 0, 0, "tool_use", "demo", 5));
    }

    private const string ImplementationExample = """
        {
          "summary": "This flow lets the medical records (MRD) team receive inpatient case sheets that wards send to the department. The user chooses the ward, checks each sheet against the list, scans it and saves it. A sheet with missing pages is held back until it is complete. This is a fixed demo example, not an analysis of real notes.",
          "scopeIn": ["Receiving case sheets sent from a ward", "Checking each sheet for missing pages", "Choosing the ward, with a default for super users"],
          "scopeOut": ["How the ward prepares and sends the sheets"],
          "roles": [
            { "role": "MRD user", "responsibility": "Receives, checks, scans and saves the case sheets.", "usesAreas": ["IP Case Sheet Receive in Department"] },
            { "role": "Super user", "responsibility": "Does the same, and starts with their own ward already selected.", "usesAreas": ["IP Case Sheet Receive in Department"] }
          ],
          "journey": [
            { "title": "Open the receive menu", "who": "MRD user", "where": "IP Case Sheet Receive in Department", "description": "The user opens the menu when case sheets arrive from a ward.", "outcome": "The ward selection is shown.", "branches": [] },
            { "title": "Pick the ward", "who": "MRD user", "where": "IP Case Sheet Receive in Department", "description": "The ward is chosen from the list. For a super user the ward they are mapped to is already selected, and they can change it. Other users start with nothing selected.", "outcome": "The case sheets sent from that ward are listed.", "branches": [ { "condition": "The user is a super user", "result": "Their own ward is pre-selected" }, { "condition": "The user is not a super user", "result": "They choose the ward themselves" } ] },
            { "title": "Check and scan each sheet", "who": "MRD user", "where": "IP Case Sheet Receive in Department", "description": "Each sheet is compared with the list, scanned and checked for missing pages.", "outcome": "The sheet is either complete or held back.", "branches": [ { "condition": "Pages are missing", "result": "A warning is shown and the sheet stays pending" }, { "condition": "The sheet is complete", "result": "It can be saved as received" } ] },
            { "title": "Save as received", "who": "MRD user", "where": "IP Case Sheet Receive in Department", "description": "Complete sheets are saved and handed on to the billing department.", "outcome": "The sheet is recorded as received and billing can see it.", "branches": [] }
          ],
          "touchpoints": [
            { "name": "IP Case Sheet Receive in Department", "type": "Menu", "whatHappens": "Where the user picks the ward, checks the sheets, scans and saves.", "dataIn": ["Ward", "Case sheet list"], "dataOut": ["Received or pending status"] },
            { "name": "User to ward mapping", "type": "Master", "whatHappens": "Read to find a super user's default ward.", "dataIn": ["User", "Mapped ward"], "dataOut": ["Default ward"] }
          ],
          "existingAreasAffected": [ { "area": "Department/ward selection on the receive menu", "effect": "Starts with the super user's ward instead of empty." } ],
          "handoffs": [ { "from": "MRD", "to": "Billing", "whatPasses": "Case sheets recorded as received", "when": "When a complete sheet is saved" } ],
          "assumptions": ["A user is a super user when they are mapped as one in the existing user settings."],
          "openQuestions": ["Which ward is pre-selected when a super user is mapped to more than one?", "How long may a sheet stay pending?"]
        }
        """;

    private string FlowExample(string message)
    {
        var previous = Between(message, "<previous_result>", "</previous_result>");
        var node = JsonNode.Parse(previous ?? File.ReadAllText(_examplePath))!;
        if (previous is null)
        {
            node["department"] = Between(message, "<department>", "</department>") ?? "Department";
            node["moduleCode"] = Between(message, "<module_code>", "</module_code>") ?? "MOD";
            var refs = EntryRef().Matches(message).Select(m => m.Groups[1].Value).ToList();
            FixRefs(node, refs);
        }
        return node.ToJsonString();
    }

    /// <summary>The example cites entries E1 and E2; keep only citations that exist in this flow, so the result passes the server's checks.</summary>
    private static void FixRefs(JsonNode? node, List<string> refs)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o.ToList())
                {
                    if (key == "sourceEntryIds" && value is JsonArray arr)
                    {
                        var keep = arr.Select(x => x!.GetValue<string>()).Where(refs.Contains).ToList();
                        if (keep.Count == 0 && refs.Count > 0) keep.Add(refs[0]);
                        o[key] = new JsonArray(keep.Select(k => (JsonNode)k).ToArray());
                    }
                    else FixRefs(value, refs);
                }
                break;
            case JsonArray a:
                foreach (var item in a) FixRefs(item, refs);
                break;
        }
    }

    private static string ProjectExample() => """
        {"schemaVersion":"1.0","summary":"Demo mode: a fixed example hand-off between the first two flows.",
         "handoffs":[{"id":"H1","from":{"flowRef":"F1","itemId":"S5"},"to":{"flowRef":"F2","itemId":"S1"},
           "description":"Demo example: the case sheets received in the first department go on to the next one.","dataPassed":["Case sheet"],"basis":"Demo example","evidence":"Demo example."}],
         "duplicates":[],"conflicts":[],"gaps":[],"clarificationQuestions":[],"combinedMermaid":"flowchart TD\n  A[\"Demo\"]",
         "confidence":{"handoffs":0.5,"conflictsAndDuplicates":0.5}}
        """;

    private static string? Between(string text, string open, string close)
    {
        var a = text.IndexOf(open, StringComparison.Ordinal);
        if (a < 0) return null;
        a += open.Length;
        var b = text.IndexOf(close, a, StringComparison.Ordinal);
        return b < 0 ? null : text[a..b];
    }

    [GeneratedRegex("<entry ref=\"(E\\d+)\"")] private static partial Regex EntryRef();
}
