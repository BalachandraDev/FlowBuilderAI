using FlowCapture.Shared.Content;

namespace FlowCapture.Application.Documents;

public sealed record ChapterInput(FlowDocumentInput Flow, string DepartmentName, string MenuName);

public sealed record HandoffRow(string FromFlow, string FromStep, string ToFlow, string ToStep, string Description, string DataPassed);

public sealed record ProjectDocumentInput(
    string DocNumber,
    string HospitalName,
    string ProjectCode,
    string Location,
    string AssignedBy,
    DateTime Created,
    string PreparedBy,
    IReadOnlyList<ChapterInput> Chapters,
    ImageBlock? CombinedDiagram,
    IReadOnlyList<HandoffRow> AcceptedHandoffs);

/// <summary>The consolidated project document: an overview across departments, then every confirmed flow as a chapter.</summary>
public static class ProjectDocumentBuilder
{
    public static RgdDocument Build(ProjectDocumentInput input)
    {
        if (input.Chapters.Count == 0) throw new InvalidOperationException("A project document needs at least one confirmed flow.");
        var blocks = new List<Block>
        {
            new HeadingBlock(0, "Consolidated Requirement Gathering Document"),
            new ParagraphBlock($"Hospital: {RgdDocumentBuilder.Fill(input.HospitalName)}", Bold: true),
            new ParagraphBlock($"Project {RgdDocumentBuilder.Fill(input.ProjectCode)}, {RgdDocumentBuilder.Fill(input.Location)}. {input.Chapters.Count} confirmed flow(s) in {input.Chapters.Select(c => c.Flow.ModuleCode).Distinct().Count()} module(s)."),
            new HeadingBlock(1, "Contents"),
            new BulletsBlock(input.Chapters.Select((c, i) => $"Chapter {i + 1}: {c.Flow.DocNumber}, {RgdDocumentBuilder.Fill(c.Flow.Content.FlowTitle)} ({RgdDocumentBuilder.Fill(c.DepartmentName)})").ToList()),
            new HeadingBlock(1, "Cross-department overview"),
        };

        if (input.CombinedDiagram is { } diagram) blocks.Add(diagram);

        blocks.Add(new HeadingBlock(2, "Hand-offs between departments"));
        blocks.Add(input.AcceptedHandoffs.Count == 0
            ? new ParagraphBlock("No hand-offs between departments have been accepted yet.")
            : new TableBlock(["From", "To", "What is handed over", "Data"], input.AcceptedHandoffs.Select(h => (IReadOnlyList<string>)
                [$"{RgdDocumentBuilder.Fill(h.FromFlow)}, {RgdDocumentBuilder.Fill(h.FromStep)}", $"{RgdDocumentBuilder.Fill(h.ToFlow)}, {RgdDocumentBuilder.Fill(h.ToStep)}",
                 RgdDocumentBuilder.Fill(h.Description), RgdDocumentBuilder.Fill(h.DataPassed)]).ToList(), [3, 3, 5, 3]));

        for (var i = 0; i < input.Chapters.Count; i++)
        {
            var chapter = input.Chapters[i];
            blocks.Add(new PageBreakBlock());
            blocks.Add(new HeadingBlock(1, $"Chapter {i + 1}. {chapter.Flow.DocNumber}: {RgdDocumentBuilder.Fill(chapter.Flow.Content.FlowTitle)}"));
            RgdDocumentBuilder.AppendFlow(blocks, chapter.Flow, (i + 1).ToString());
        }

        var cover = new RgdCover(input.DocNumber, "All modules", RgdDocumentBuilder.Fill(input.AssignedBy), RgdDocumentBuilder.Date(input.Created));
        return new RgdDocument(cover, $"{RgdDocumentBuilder.Fill(input.HospitalName)}: consolidated requirements", blocks);
    }
}

/// <summary>Builds one Mermaid chart for a whole project: a box per department, a box per flow inside it, and dashed arrows for accepted hand-offs.</summary>
public static class CombinedMermaidBuilder
{
    public sealed record FlowNode(int Index, string ModuleCode, string DepartmentName, string MenuName, FlowContent Content);
    public sealed record Link(int FromIndex, string FromItem, int ToIndex, string ToItem, string Label);

    public static string Build(IReadOnlyList<FlowNode> flows, IReadOnlyList<Link> links)
    {
        var sb = new System.Text.StringBuilder("flowchart TD");
        foreach (var dept in flows.GroupBy(f => f.ModuleCode))
        {
            sb.Append($"\n  subgraph DEP_{Safe(dept.Key)}[\"{Flows.MermaidBuilder.Label(dept.First().DepartmentName)}\"]");
            foreach (var f in dept)
            {
                sb.Append($"\n    subgraph FL{f.Index}[\"{Flows.MermaidBuilder.Label(f.MenuName)}\"]");
                var stepNo = 0;
                foreach (var s in f.Content.Steps)
                {
                    stepNo++;
                    sb.Append($"\n      F{f.Index}_{s.Id}[\"{stepNo}. {Flows.MermaidBuilder.Label(s.Action)}\"]");
                }
                foreach (var d in f.Content.Decisions) sb.Append($"\n      F{f.Index}_{d.Id}{{\"{Flows.MermaidBuilder.Label(d.Condition)}\"}}");
                if (UsesEnd(f.Content)) sb.Append($"\n      F{f.Index}_END([\"End\"])");
                sb.Append("\n    end");
            }
            sb.Append("\n  end");
        }

        foreach (var f in flows)
        {
            string T(string id) => id == "END" ? $"F{f.Index}_END" : $"F{f.Index}_{id}";
            foreach (var s in f.Content.Steps)
                foreach (var n in s.Next) sb.Append($"\n  {T(s.Id)} --> {T(n)}");
            foreach (var d in f.Content.Decisions)
            {
                sb.Append($"\n  {T(d.Id)} -->|Yes| {T(d.YesPath.Next)}");
                sb.Append($"\n  {T(d.Id)} -->|No| {T(d.NoPath.Next)}");
            }
        }
        foreach (var l in links)
        {
            var label = Flows.MermaidBuilder.Label(l.Label);
            sb.Append($"\n  F{l.FromIndex}_{l.FromItem} -.->|\"{label}\"| F{l.ToIndex}_{l.ToItem}");
        }
        return sb.ToString();
    }

    private static bool UsesEnd(FlowContent c) =>
        c.Steps.Any(s => s.Next.Contains("END")) || c.Decisions.Any(d => d.YesPath.Next == "END" || d.NoPath.Next == "END");

    private static string Safe(string text) => System.Text.RegularExpressions.Regex.Replace(text, "[^A-Za-z0-9]", "_");
}
