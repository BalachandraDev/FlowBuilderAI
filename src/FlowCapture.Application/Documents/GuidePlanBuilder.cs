using System.Text.Json.Nodes;
using FlowCapture.Shared.Content;

namespace FlowCapture.Application.Documents;

/// <summary>
/// Writes the "plan" of the implementation document straight from the analysed workflow, with no AI call: who does what, the flow stage
/// by stage with each decision where it is made, and the screens it touches. This is what makes the document appear in seconds. The
/// administrator can switch on an AI rewrite of the wording (slower, uses tokens); the page is built from the same plan shape either way.
/// Nothing is invented: every sentence comes from the workflow, and anything unknown is left out.
/// </summary>
public static class GuidePlanBuilder
{
    private static bool Has(string? s) => !string.IsNullOrWhiteSpace(s)
        && !s.Trim().Equals("To be confirmed", StringComparison.OrdinalIgnoreCase) && !s.Trim().Equals("Not applicable", StringComparison.OrdinalIgnoreCase);

    private static string Sentence(string s) { s = s.Trim(); return s.EndsWith('.') || s.EndsWith('?') || s.EndsWith('!') ? s : s + "."; }

    /// <summary>The text as it is, or cut at a word boundary with an ellipsis when it is longer than <paramref name="max"/>.</summary>
    private static string Short(string s, int max)
    {
        s = s.Trim().TrimEnd('.');
        if (s.Length <= max) return s;
        var cut = s[..(max - 1)];
        var space = cut.LastIndexOf(' ');
        return (space > max / 2 ? cut[..space] : cut).TrimEnd(' ', ',', ';') + "\u2026";
    }

    private const int TitleMax = 110;

    /// <summary>What a step, decision or flow is, in its own words (not cut short like the labels in the rules table).</summary>
    private static string Named(FlowContent f, string id)
    {
        var text = f.Steps.FirstOrDefault(s => s.Id == id)?.Action ?? f.Decisions.FirstOrDefault(d => d.Id == id)?.Condition
                   ?? f.AlternateFlows.FirstOrDefault(a => a.Id == id)?.Name ?? f.ExceptionFlows.FirstOrDefault(x => x.Id == id)?.Name;
        return Has(text) ? text!.Trim().TrimEnd('.') : "";
    }

    private static JsonArray Arr(IEnumerable<string> items) => new(items.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());

    public static JsonObject FromFlow(FlowContent f)
    {
        var plan = new JsonObject
        {
            ["summary"] = Has(f.Purpose) ? f.Purpose.Trim() : Has(f.UseCase.MainScenario) ? f.UseCase.MainScenario.Trim() : "",
            ["scopeIn"] = new JsonArray(), ["scopeOut"] = new JsonArray(),
            ["roles"] = Roles(f), ["journey"] = Journey(f), ["touchpoints"] = Touchpoints(f),
            ["existingAreasAffected"] = new JsonArray(), ["handoffs"] = new JsonArray(),
            ["assumptions"] = new JsonArray(), ["openQuestions"] = new JsonArray(),
        };
        return plan;
    }

    private static JsonArray Roles(FlowContent f)
    {
        var roles = new JsonArray();
        foreach (var a in f.Actors.Where(a => Has(a.Role)))
        {
            var mine = f.Steps.Where(s => s.Actor == a.Role || s.Actor == a.Id).ToList();
            roles.Add(new JsonObject
            {
                ["role"] = a.Role.Trim(),
                ["responsibility"] = string.Join(" ", mine.Where(s => Has(s.Action)).Take(3).Select(s => Sentence(s.Action))),
                ["usesAreas"] = Arr(mine.Where(s => Has(s.Screen)).Select(s => s.Screen.Trim()).Distinct()),
            });
        }
        return roles;
    }

    private static JsonArray Journey(FlowContent f)
    {
        var journey = new JsonArray();
        var shown = new HashSet<string>();
        foreach (var s in f.Steps.Where(s => Has(s.Action)))
        {
            var branches = new JsonArray();
            foreach (var d in f.Decisions.Where(d => s.Next.Contains(d.Id) && Has(d.Condition)))
            {
                shown.Add(d.Id);
                branches.Add(Branch(f, d.Condition, true, d.YesPath));
                branches.Add(Branch(f, d.Condition, false, d.NoPath));
            }
            // a step that leads to several steps is a menu of choices: say which routes there are
            var onward = s.Next.Where(id => f.Steps.Any(t => t.Id == id)).ToList();
            if (onward.Count > 1)
                foreach (var id in onward)
                {
                    var target = f.Steps.First(t => t.Id == id);
                    branches.Add(new JsonObject
                    {
                        ["condition"] = Has(target.Screen) ? "Choose " + target.Screen.Trim() : "Choose: " + Short(target.Action, 60),
                        ["result"] = Sentence(Named(f, id)),
                    });
                }
            journey.Add(new JsonObject
            {
                ["stepId"] = s.Id,                                      // so a picture attached to this step is printed with it
                ["title"] = Short(s.Action, TitleMax),
                ["who"] = Has(Actor(f, s.Actor)) ? Actor(f, s.Actor) : "",
                ["where"] = Has(s.Screen) ? s.Screen.Trim() : "",
                // the title already says it, unless it had to be shortened: then the full sentence goes here
                ["description"] = ((s.Action.Trim().TrimEnd('.').Length > TitleMax ? Sentence(s.Action) + " " : "")
                                   + (s.Inputs.Any(Has) ? "Uses: " + string.Join(", ", s.Inputs.Where(Has).Select(i => i.Trim())) + "." : "")).Trim(),
                ["outcome"] = string.Join(", ", s.Outputs.Where(Has).Select(o => o.Trim())),
                ["branches"] = branches,
            });
        }
        // a decision no step leads to is still part of the flow: show it as its own stage
        foreach (var d in f.Decisions.Where(d => Has(d.Condition) && !shown.Contains(d.Id)))
            journey.Add(new JsonObject
            {
                ["title"] = Short(d.Condition, 70), ["who"] = "", ["where"] = "", ["description"] = Sentence(d.Condition), ["outcome"] = "",
                ["branches"] = new JsonArray { Branch(f, d.Condition, true, d.YesPath), Branch(f, d.Condition, false, d.NoPath) },
            });
        return journey;
    }

    private static string Actor(FlowContent f, string actor) => f.Actors.FirstOrDefault(a => a.Id == actor)?.Role ?? actor ?? "";

    private static JsonObject Branch(FlowContent f, string condition, bool yes, DecisionPath path)
    {
        var next = Has(path.Next) ? Named(f, path.Next) : "";
        var result = (Has(path.Summary) ? Sentence(path.Summary) : "") + (next.Length > 0 ? " Continues with: " + next + "." : "");
        return new JsonObject { ["condition"] = Short(condition, 90) + (yes ? " - yes" : " - no"), ["result"] = result.Trim().Length > 0 ? result.Trim() : (yes ? "The flow continues." : "The other path is taken.") };
    }

    private static JsonArray Touchpoints(FlowContent f)
    {
        var points = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in f.Steps.Where(s => Has(s.Screen)).GroupBy(s => s.Screen.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            seen.Add(group.Key);
            points.Add(new JsonObject
            {
                ["name"] = group.Key, ["type"] = "Screen",
                ["whatHappens"] = string.Join(" ", group.Where(s => Has(s.Action)).Take(3).Select(s => Sentence(s.Action))),
                ["dataIn"] = Arr(group.SelectMany(s => s.Inputs).Where(Has).Select(i => i.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)),
                ["dataOut"] = Arr(group.SelectMany(s => s.Outputs).Where(Has).Select(o => o.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)),
            });
        }
        foreach (var screen in f.Screens.Where(s => Has(s.MenuName) && !seen.Contains(s.MenuName.Trim())))
            points.Add(new JsonObject { ["name"] = screen.MenuName.Trim(), ["type"] = "Screen", ["whatHappens"] = "", ["dataIn"] = new JsonArray(), ["dataOut"] = new JsonArray() });
        return points;
    }
}
