using System.Text;
using System.Text.RegularExpressions;
using FlowCapture.Shared.Content;

namespace FlowCapture.Application.Flows;

/// <summary>
/// Deterministic Mermaid generation from structured steps and decisions. The server uses it after every manual edit and
/// whenever the model's diagram fails <see cref="MermaidSyntaxChecker"/>, so the diagram always matches the data.
/// </summary>
public static class MermaidBuilder
{
    private const int MaxLabel = 80;

    public static string Build(FlowContent c)
    {
        if (c.Steps.Count == 0)
            return "flowchart TD\n  START([\"No steps captured yet\"])";

        // lanes: one per actor in order of first appearance
        var laneOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var lanes = new List<string>();
        foreach (var s in c.Steps)
        {
            var actor = string.IsNullOrWhiteSpace(s.Actor) ? "Unassigned" : s.Actor.Trim();
            var idx = lanes.FindIndex(l => string.Equals(l, actor, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) { lanes.Add(actor); idx = lanes.Count - 1; }
            laneOf[s.Id] = idx;
        }

        // a decision sits in the lane of the first node that leads to it
        var decisionLane = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var d in c.Decisions)
        {
            var pred = c.Steps.FirstOrDefault(s => s.Next.Contains(d.Id));
            decisionLane[d.Id] = pred is not null ? laneOf[pred.Id] : 0;
        }

        var nodesByLane = lanes.Select(_ => new List<string>()).ToList();
        var stepNumber = 0;
        foreach (var s in c.Steps)
        {
            stepNumber++;
            nodesByLane[laneOf[s.Id]].Add($"{s.Id}[\"{stepNumber}. {Label(s.Action)}\"]");
            foreach (var d in c.Decisions.Where(d => s.Next.Contains(d.Id) && decisionLane[d.Id] == laneOf[s.Id]))
                if (!nodesByLane[laneOf[s.Id]].Any(n => n.StartsWith(d.Id + "{", StringComparison.Ordinal)))
                    nodesByLane[laneOf[s.Id]].Add($"{d.Id}{{\"{Label(d.Condition)}\"}}");
        }
        foreach (var d in c.Decisions)
            if (!nodesByLane[decisionLane[d.Id]].Any(n => n.StartsWith(d.Id + "{", StringComparison.Ordinal)))
                nodesByLane[decisionLane[d.Id]].Add($"{d.Id}{{\"{Label(d.Condition)}\"}}");

        var sb = new StringBuilder("flowchart TD");
        for (var i = 0; i < lanes.Count; i++)
        {
            sb.Append($"\n  subgraph L{i + 1}[\"{Label(lanes[i])}\"]");
            foreach (var n in nodesByLane[i]) sb.Append("\n    ").Append(n);
            sb.Append("\n  end");
        }

        var usesEnd = false;
        string Target(string id)
        {
            if (id != "END") return id;
            usesEnd = true;
            return "END1";
        }

        foreach (var s in c.Steps)
            foreach (var n in s.Next)
                sb.Append($"\n  {s.Id} --> {Target(n)}");
        foreach (var d in c.Decisions)
        {
            sb.Append($"\n  {d.Id} -->|Yes| {Target(d.YesPath.Next)}");
            sb.Append($"\n  {d.Id} -->|No| {Target(d.NoPath.Next)}");
        }
        if (usesEnd) sb.Append("\n  END1([\"End\"])");
        return sb.ToString();
    }

    /// <summary>Makes text safe inside a double-quoted Mermaid label.</summary>
    public static string Label(string text)
    {
        var t = Regex.Replace(text ?? "", @"[\r\n\t]+", " ");
        t = t.Replace('"', '\'').Replace('`', '\'');
        t = Regex.Replace(t, @"[\[\]{}|<>]", " ");
        t = Regex.Replace(t, @"\s{2,}", " ").Trim();
        if (t.Length == 0) t = "-";
        return t.Length <= MaxLabel ? t : t[..(MaxLabel - 3)].TrimEnd() + "...";
    }
}

/// <summary>
/// Lightweight structural check of a model-produced diagram. Full parsing (mermaid-cli) happens in the render
/// job in Phase 4; this check decides whether to trust the model string or rebuild it.
/// </summary>
public static class MermaidSyntaxChecker
{
    public static bool IsAcceptable(string? mermaid, FlowContent c)
    {
        if (string.IsNullOrWhiteSpace(mermaid)) return false;
        var lines = mermaid.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0 || !Regex.IsMatch(lines[0], @"^flowchart\s+(TD|TB|LR|RL|BT)\b")) return false;
        if (mermaid.Contains("<script", StringComparison.OrdinalIgnoreCase) || mermaid.Contains("```")) return false;

        var subgraphs = lines.Count(l => l.StartsWith("subgraph ", StringComparison.Ordinal));
        var ends = lines.Count(l => l == "end");
        if (subgraphs != ends) return false;

        foreach (var s in c.Steps)
            if (!Regex.IsMatch(mermaid, $@"\b{Regex.Escape(s.Id)}\s*\[")) return false;
        foreach (var d in c.Decisions)
            if (!Regex.IsMatch(mermaid, $@"\b{Regex.Escape(d.Id)}\s*\{{")) return false;

        // every edge endpoint must be a declared node or END1
        var declared = new HashSet<string>(c.Steps.Select(s => s.Id).Concat(c.Decisions.Select(d => d.Id)).Append("END1"), StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(mermaid, @"^\s*([A-Za-z0-9_]+)\s*-->(?:\|[^|]*\|)?\s*([A-Za-z0-9_]+)", RegexOptions.Multiline))
            if (!declared.Contains(m.Groups[1].Value) || !declared.Contains(m.Groups[2].Value)) return false;
        return true;
    }
}
