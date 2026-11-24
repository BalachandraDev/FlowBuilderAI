using System.Globalization;
using FlowCapture.Shared;
using FlowCapture.Shared.Content;

namespace FlowCapture.Application.Documents;

public sealed record OpenIssue(string Key, string Section, string Text);

public sealed record FlowDocumentInput(
    FlowContent Content,
    string DocNumber,
    string ModuleCode,
    string AssignedBy,
    DateTime Created,
    string HospitalName,
    string PreparedBy,
    int FlowVersionNo,
    DateTime? ConfirmedAt,
    IReadOnlyList<OpenIssue> OpenIssues,
    ImageBlock? Diagram);

/// <summary>
/// Turns a confirmed flow into the requirement document: the sections of the RGD template (title, purpose, scope, functional
/// requirement, requirements, user roles, use case) followed by the development-ready sections. No field is ever left empty:
/// unknown values read "To be confirmed" and sections with nothing to say read "Not applicable".
/// </summary>
public static class RgdDocumentBuilder
{
    public const string Tbc = Shared.Tbc.ToBeConfirmed;
    public const string NotApplicable = Shared.Tbc.NotApplicable;

    public static string Date(DateTime utc) => utc.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);

    public static RgdDocument Build(FlowDocumentInput input)
    {
        var c = input.Content;
        var blocks = new List<Block>();
        var cover = new RgdCover(input.DocNumber, input.ModuleCode, Fill(input.AssignedBy), Date(input.Created));
        new FlowSections(blocks, input, prefix: "", headingBase: 1).Write(includeTitleBlock: true);
        return new RgdDocument(cover, Fill(c.FlowTitle), blocks);
    }

    /// <summary>The sections of one flow, appended to <paramref name="blocks"/>. Used for a single document and for each chapter of a project document.</summary>
    public static void AppendFlow(List<Block> blocks, FlowDocumentInput input, string chapterNumber) =>
        new FlowSections(blocks, input, prefix: chapterNumber, headingBase: 2).Write(includeTitleBlock: false);

    public static string Fill(string? text) => string.IsNullOrWhiteSpace(text) ? Tbc : text.Trim();

    /// <summary>One line for a cell: nothing becomes the given fallback, several lines become a bulleted list.</summary>
    public static string Lines(IEnumerable<string?> items, string fallback = NotApplicable)
    {
        var list = items.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i!.Trim()).ToList();
        return list.Count switch { 0 => fallback, 1 => list[0], _ => string.Join("\n", list.Select(i => "• " + i)) };
    }

    private sealed class FlowSections(List<Block> blocks, FlowDocumentInput input, string prefix, int headingBase)
    {
        private readonly FlowContent _c = input.Content;
        private readonly Dictionary<string, int> _stepNo = input.Content.Steps.Select((s, i) => (s.Id, No: i + 1)).ToDictionary(x => x.Id, x => x.No);
        private int _section;

        public void Write(bool includeTitleBlock)
        {
            if (includeTitleBlock)
            {
                blocks.Add(new HeadingBlock(0, "Functional Requirement Document"));
                blocks.Add(new ParagraphBlock($"Title: {Fill(_c.FlowTitle)}", Bold: true));
                blocks.Add(new ParagraphBlock(Source(), Italic: true));
            }
            else
            {
                blocks.Add(new ParagraphBlock(Source(), Italic: true));
            }

            Section("Purpose"); blocks.Add(new ParagraphBlock(Fill(_c.Purpose)));
            Section("Scope"); blocks.Add(new ParagraphBlock(Fill(_c.Scope)));

            Section("Functional Requirement");
            var functional = _c.BusinessRules.Select(r => r.Rule)
                .Concat(_c.Requirements.Select(r => r.Description).Where(d => !_c.BusinessRules.Any(b => string.Equals(b.Rule, d, StringComparison.OrdinalIgnoreCase)))).ToList();
            if (functional.Count > 0) blocks.Add(new BulletsBlock(functional.Select(Fill).ToList()));
            else blocks.Add(new ParagraphBlock(Tbc));

            Caption("Requirements");
            blocks.Add(Table(["Req. ID", "Requirements Description", "Category"], [1, 6, 1],
                _c.Requirements.Select(r => (IReadOnlyList<string>)[Fill(r.ReqId), Fill(r.Description), Fill(r.Category)]).ToList(),
                emptyRow: [Tbc, Tbc, Tbc]));

            Caption("User Roles and Responsibilities");
            blocks.Add(Table(["User/Role", "Frequency", "Module", "Menu Name"], [3, 2, 2, 4],
                _c.UserRoles.Select(u => (IReadOnlyList<string>)[Fill(u.UserRole), Fill(u.Frequency), Fill(u.Module), Fill(u.MenuName)]).ToList(),
                emptyRow: [Lines(_c.UseCase.PrimaryActors, Tbc), Tbc, input.ModuleCode, Fill(_c.Screens.FirstOrDefault()?.MenuName)]));

            Caption("Use Case Description");
            var conditionsOpen = input.OpenIssues.Any(i => i.Section == "conditions");
            blocks.Add(new TableBlock([], [
                ["Primary Actor(s)", Lines(_c.UseCase.PrimaryActors, Tbc)],
                ["Trigger", Fill(_c.Trigger)],
                ["Pre-conditions", Lines(_c.PreConditions.Select(p => p.Text), conditionsOpen ? Tbc : NotApplicable)],
                ["Post-conditions", Lines(_c.PostConditions.Select(p => p.Text), conditionsOpen ? Tbc : NotApplicable)],
                ["Priority", Fill(_c.UseCase.Priority)],
                ["Main Scenario", Fill(_c.UseCase.MainScenario)],
            ], [2, 7]));

            // ---- development-ready sections
            if (input.Diagram is { } diagram)
            {
                Section("Flow diagram");
                blocks.Add(diagram);
            }

            Section("Detailed flow");
            Caption("Main flow");
            blocks.Add(Table(["No.", "Actor", "Action", "Screen / menu", "Inputs", "Outputs", "Next"], [1, 2, 4, 3, 3, 3, 2],
                _c.Steps.Select((s, i) => (IReadOnlyList<string>)[(i + 1).ToString(), Fill(s.Actor), Fill(s.Action), Fill(s.Screen),
                    Lines(s.Inputs), Lines(s.Outputs), Lines(s.Next.Select(Target), "End of flow")]).ToList(),
                emptyRow: ["1", Tbc, Tbc, Tbc, NotApplicable, NotApplicable, "End of flow"]));

            Caption("Decisions");
            WriteOrNote(_c.Decisions.Count > 0,
                () => Table(["ID", "Decision", "If yes", "If no"], [1, 4, 4, 4],
                    _c.Decisions.Select(d => (IReadOnlyList<string>)[d.Id, Fill(d.Condition), $"{Fill(d.YesPath.Summary)} (go to {Target(d.YesPath.Next)})", $"{Fill(d.NoPath.Summary)} (go to {Target(d.NoPath.Next)})"]).ToList()));

            Caption("Alternate flows");
            WriteOrNote(_c.AlternateFlows.Count > 0,
                () => Table(["ID", "Name", "When it applies", "Branches from", "Steps", "Rejoins at"], [1, 3, 4, 2, 4, 2],
                    _c.AlternateFlows.Select(a => (IReadOnlyList<string>)[a.Id, Fill(a.Name), Fill(a.Condition), Target(a.BranchFrom), Lines(a.Steps), Target(a.RejoinAt)]).ToList()));

            Caption("Exception flows");
            WriteOrNote(_c.ExceptionFlows.Count > 0,
                () => Table(["ID", "Name", "When it happens", "Handling", "Outcome"], [1, 3, 4, 4, 3],
                    _c.ExceptionFlows.Select(e => (IReadOnlyList<string>)[e.Id, Fill(e.Name), Fill(e.Condition), Lines(e.Handling), Fill(e.Outcome)]).ToList()));

            Section("Business rules and validations");
            Caption("Business rules");
            WriteOrNote(_c.BusinessRules.Count > 0,
                () => Table(["ID", "Rule", "Applies to"], [1, 7, 2],
                    _c.BusinessRules.Select(r => (IReadOnlyList<string>)[r.Id, Fill(r.Rule), r.AppliesTo.Count == 0 ? NotApplicable : string.Join(", ", r.AppliesTo.Select(Target))]).ToList()));
            Caption("Validations");
            WriteOrNote(_c.Validations.Count > 0,
                () => Table(["ID", "Field", "Rule", "Error message"], [1, 3, 5, 4],
                    _c.Validations.Select(v => (IReadOnlyList<string>)[v.Id, Fill(v.Field), Fill(v.Rule), Fill(v.ErrorMessage)]).ToList()));

            Section("Screens and fields");
            if (_c.Screens.Count == 0) blocks.Add(new ParagraphBlock(Tbc));
            foreach (var s in _c.Screens)
            {
                Caption(Fill(s.MenuName));
                WriteOrNote(s.Fields.Count > 0,
                    () => Table(["Field", "Type", "Mandatory", "Default", "Source / master"], [3, 2, 2, 4, 3],
                        s.Fields.Select(f => (IReadOnlyList<string>)[Fill(f.Name), Fill(f.Type), Fill(f.Mandatory), Fill(f.DefaultValue), Fill(f.Source)]).ToList()),
                    Tbc);
            }

            Section("Data entities and suggested table changes");
            WriteOrNote(_c.DataEntities.Count > 0,
                () => Table(["Entity", "Fields", "Suggested change"], [3, 7, 3],
                    _c.DataEntities.Select(e => (IReadOnlyList<string>)[Fill(e.Name),
                        Lines(e.Fields.Select(f => $"{Fill(f.Name)} (type: {Fill(f.Type)}; mandatory: {Fill(f.Mandatory)}; source: {Fill(f.Source)})")), Fill(e.SuggestedChange)]).ToList()));

            Section("Integration points");
            WriteOrNote(_c.Integrations.Count > 0,
                () => Table(["ID", "Module / system", "Direction", "Description"], [1, 3, 2, 6],
                    _c.Integrations.Select(i => (IReadOnlyList<string>)[i.Id, Fill(i.System), Fill(i.Direction), Fill(i.Description)]).ToList()));

            Section("Reports and prints");
            WriteOrNote(_c.ReportsAndPrints.Count > 0,
                () => Table(["ID", "Name", "Type", "Description"], [1, 3, 2, 6],
                    _c.ReportsAndPrints.Select(r => (IReadOnlyList<string>)[r.Id, Fill(r.Name), Fill(r.Kind), Fill(r.Description)]).ToList()));

            Section("Non-functional notes");
            WriteOrNote(_c.NonFunctionalNotes.Count > 0,
                () => Table(["ID", "Category", "Note"], [1, 3, 9],
                    _c.NonFunctionalNotes.Select(n => (IReadOnlyList<string>)[n.Id, Fill(n.Category), Fill(n.Note)]).ToList()));

            Section("Acceptance criteria and test scenarios");
            WriteOrNote(_c.AcceptanceCriteria.Count > 0,
                () => Table(["ID", "Requirement", "Given", "When", "Then"], [1, 2, 4, 4, 4],
                    _c.AcceptanceCriteria.Select(a => (IReadOnlyList<string>)[a.Id, Fill(a.ReqId), Fill(a.Given), Fill(a.When), Fill(a.Then)]).ToList()),
                Tbc);

            Section("Open issues and clarifications");
            var issues = input.OpenIssues.Select(i => (IReadOnlyList<string>)[i.Key, UiName(i.Section), Fill(i.Text), "Open"]).ToList();
            var covered = _c.ClarificationQuestions.Where(q => q.RelatedGapId is not null).Select(q => q.RelatedGapId).ToHashSet();
            foreach (var g in _c.Gaps.Where(g => !covered.Contains(g.Id)))
                issues.Add([g.Id, UiName(g.Section), Fill(g.Description), "Open"]);
            if (issues.Count == 0) blocks.Add(new ParagraphBlock("None"));
            else blocks.Add(Table(["Ref.", "Section", "Question or gap", "Status"], [1, 3, 8, 1], issues));

            Section("Sign-off");
            blocks.Add(Table(["Role", "Name", "Signature", "Date"], [3, 4, 4, 3], [
                ["Prepared by", Fill(input.PreparedBy), "________________", Date(input.Created)],
                ["Reviewed by", Tbc, "________________", "__ - __ - ____"],
                ["Approved by", Tbc, "________________", "__ - __ - ____"],
            ]));
        }

        private string Source() =>
            $"Based on confirmed flow version {input.FlowVersionNo}{(input.ConfirmedAt is { } at ? $", confirmed on {Date(at)}" : "")}. Hospital: {Fill(input.HospitalName)}.";

        private void Section(string title)
        {
            _section++;
            var number = prefix.Length == 0 ? $"{_section}." : $"{prefix}.{_section}";
            blocks.Add(new HeadingBlock(headingBase, $"{number} {title}"));
        }

        private void Caption(string title) => blocks.Add(new HeadingBlock(headingBase + 1, title));

        private void WriteOrNote(bool any, Func<Block> table, string note = NotApplicable) =>
            blocks.Add(any ? table() : new ParagraphBlock(note));

        private static TableBlock Table(string[] header, int[] widths, IReadOnlyList<IReadOnlyList<string>> rows, string[]? emptyRow = null) =>
            new(header, rows.Count > 0 ? rows : [emptyRow ?? header.Select(_ => Tbc).ToArray()], widths);

        /// <summary>"S2" becomes "Step 2", "D1" stays "Decision D1", "END" becomes "End of flow".</summary>
        private string Target(string id) =>
            id == "END" ? "End of flow"
            : _stepNo.TryGetValue(id, out var n) ? $"Step {n}"
            : id.StartsWith('D') ? $"Decision {id}"
            : id;

        private static string UiName(string section) => string.IsNullOrWhiteSpace(section) ? "General"
            : char.ToUpperInvariant(section[0]) + System.Text.RegularExpressions.Regex.Replace(section[1..], "([A-Z])", " $1").ToLowerInvariant();
    }
}
