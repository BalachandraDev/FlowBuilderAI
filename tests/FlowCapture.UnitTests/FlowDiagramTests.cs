using System.Text.RegularExpressions;
using System.Xml.Linq;
using FlowCapture.Application.Documents;
using FlowCapture.Shared.Content;
using FluentAssertions;
using Xunit;

namespace FlowCapture.UnitTests;

public class FlowDiagramTests
{
    // ------------------------------------------------------------------ flows to draw

    /// <summary>The BMW masters flow as the AI first modelled it: five yes/no questions in a row, one master page behind each.</summary>
    internal static FlowContent BmwChain()
    {
        var f = new FlowContent
        {
            FlowTitle = "BMW Masters configuration", Trigger = "The user navigates to the BMW Masters section to configure or update waste management master data.",
            Actors = [new ActorItem { Id = "ACT1", Role = "BMW Administrator" }],
        };
        f.Steps.Add(Step("S1", "Opens the BMW Masters section.", "BMW Masters", ["D1"]));
        f.Steps.Add(Step("S2", "Configures how waste is to be destroyed or treated.", "Treatment Method", ["END"]));
        f.Steps.Add(Step("S3", "Configures waste categories based on color codes including Yellow, Red, Blue, White, and Green.", "Waste Category", ["END"]));
        f.Steps.Add(Step("S4", "Defines operational error or issue types that can occur during work.", "Exception Type", ["END"]));
        f.Steps.Add(Step("S5", "Defines reasons for occurrences during waste dumping or waste collection.", "Reason", ["END"]));
        f.Steps.Add(Step("S6", "Defines units of measurement for waste weight, such as kilograms or grams.", "Weight Unit", ["END"]));
        f.Steps.Add(Step("S7", "Configures corporate companies to which waste is sent and sets their status using the active/inactive buttons.", "Corporate", ["END"]));
        f.Decisions.Add(Decision("D1", "Which master page does the administrator select?", "S2", "D2"));
        f.Decisions.Add(Decision("D2", "Did the administrator select Waste Category?", "S3", "D3"));
        f.Decisions.Add(Decision("D3", "Did the administrator select Exception Type?", "S4", "D4"));
        f.Decisions.Add(Decision("D4", "Did the administrator select Reason?", "S5", "D5"));
        f.Decisions.Add(Decision("D5", "Did the administrator select Weight Unit?", "S6", "S7"));
        return f;
    }

    /// <summary>The same flow modelled the way it should be: one menu step with a route to each master page.</summary>
    internal static FlowContent BmwChoice()
    {
        var f = BmwChain();
        f.Decisions.Clear();
        f.Steps[0].Next = ["S2", "S3", "S4", "S5", "S6", "S7"];
        f.Steps[0].Action = "Opens the BMW Masters section and chooses the master to configure.";
        return f;
    }

    /// <summary>What the analysis should now produce for the BMW notes: six master pages, each its own step and screen with the fields the notes name, the shared rules, and the questions the notes leave open.</summary>
    internal static FlowContent BmwComplete()
    {
        var f = BmwChoice();
        f.Purpose = "Define the masters used by the Biomedical Waste module so that waste details are entered once and read by every other page and report.";
        f.Scope = "The six master pages of the BMW module: Treatment Method, Waste Category, Exception Type, Reason, Weight Unit and CBWTF.";
        f.UserRoles.Add(new UserRoleItem { Id = "UR1", UserRole = "BMW Administrator", Frequency = "Occasional" });
        void Screen(string id, string name, params string[] fields)
        {
            var s = new ScreenItem { Id = id, MenuName = name };
            foreach (var x in fields) s.Fields.Add(new ScreenFieldItem { Name = x.Split('|')[0], Type = x.Contains('|') ? x.Split('|')[1] : "", Source = x.Contains('|') && x.Split('|').Length > 2 ? x.Split('|')[2] : "" });
            f.Screens.Add(s);
        }
        f.Screens.Clear();
        Screen("SC1", "BMW Masters", "List of the six masters|Menu");
        Screen("SC2", "Treatment Method", "Treatment method|Text|How the waste is to be destroyed", "Active|Switch");
        Screen("SC3", "Waste Category", "Category name|Text", "Bag colour|Selection|Yellow, Red, Blue, White or Green", "Active|Switch");
        Screen("SC4", "Exception Type", "Exception type|Text|The kind of problem that can happen during the work", "Active|Switch");
        Screen("SC5", "Reason", "Reason|Text|Why a record was changed", "Active|Switch");
        Screen("SC6", "Weight Unit", "Unit|Selection|Kilogram or gram", "Active|Switch");
        Screen("SC7", "Corporate", "Company name|Text|The outside company that collects the waste", "Contact number|Text", "Licence details|Text", "Active|Switch");
        f.BusinessRules.Add(new BusinessRuleItem { Id = "BR1", Rule = "A master that is no longer needed is switched off with the Active button, never deleted.", AppliesTo = ["S2", "S3", "S4", "S5", "S6", "S7"] });
        f.BusinessRules.Add(new BusinessRuleItem { Id = "BR2", Rule = "Other pages and reports read their values from these masters, so each value is entered only once.", AppliesTo = ["S1"] });
        f.Requirements.Add(new RequirementItem { ReqId = "REQ001", Description = "Provide six master pages (Treatment Method, Waste Category, Exception Type, Reason, Weight Unit and CBWTF) whose values are used by all other BMW pages and reports.", Category = "NR", Priority = "High" });
        f.AcceptanceCriteria.Add(new AcceptanceCriterionItem { Id = "AC1", ReqId = "REQ001", Given = "a master record exists", When = "the user turns its Active button off", Then = "the record stays in the system and is no longer offered on other pages" });
        f.Gaps.Add(new GapItem { Id = "G1", Description = "Which fields each of the Treatment Method, Exception Type, Reason and Weight Unit pages has besides its name is not stated." });
        f.Gaps.Add(new GapItem { Id = "G2", Description = "Who may maintain the masters is not stated." });
        return f;
    }
    internal static FlowContent Discharge()
    {
        var f = new FlowContent
        {
            FlowTitle = "Patient discharge", Trigger = "The doctor advises discharge.",
            Actors = [new ActorItem { Id = "A1", Role = "Doctor" }, new ActorItem { Id = "A2", Role = "Nurse" }, new ActorItem { Id = "A3", Role = "Billing" }],
        };
        f.Steps.Add(Step("S1", "Doctor enters the discharge advice with the diagnosis and medicines.", "Discharge Advice", ["S2"], "A1"));
        f.Steps.Add(Step("S2", "Nurse checks the advice and informs the patient party.", "Discharge Advice", ["D1"], "A2"));
        f.Steps.Add(Step("S3", "Billing prepares the final bill from the posted charges.", "Final Bill", ["D2"], "A3"));
        f.Steps.Add(Step("S4", "Patient party pays at the cashier counter.", "Cashier", ["S5"], "A3"));
        f.Steps.Add(Step("S5", "Nurse gives the discharge summary and the patient leaves the ward.", "Discharge Summary", ["END"], "A2"));
        f.Steps.Add(Step("S6", "Nurse sends the file to the medical records department.", "Case Sheet Send", ["END"], "A2"));
        f.Decisions.Add(Decision("D1", "Are all pending investigations and medicines billed?", "S3", "S2"));      // a loop back
        f.Decisions.Add(Decision("D2", "Is the patient insured?", "S6", "S4"));
        f.AlternateFlows.Add(new AlternateFlowItem { Id = "ALT1", Name = "Discharge against medical advice", Condition = "The patient party insists on leaving.", BranchFrom = "S2", Steps = ["Take a signature"], RejoinAt = "S3" });
        return f;
    }

    private static StepItem Step(string id, string action, string screen, List<string> next, string actor = "ACT1") =>
        new() { Id = id, Actor = actor, Action = action, Screen = screen, Next = next };

    private static DecisionItem Decision(string id, string q, string yes, string no) =>
        new() { Id = id, Condition = q, YesPath = new DecisionPath { Summary = "Yes", Next = yes }, NoPath = new DecisionPath { Summary = "No", Next = no } };

    // ------------------------------------------------------------------ reading the drawing back

    private sealed record Box(string Kind, double Left, double Top, double Right, double Bottom);

    private static XDocument Parse(FlowDiagram d) => XDocument.Parse(d.Svg);

    private static IEnumerable<string> Texts(XDocument doc) => doc.Descendants().Where(e => e.Name.LocalName == "text").Select(e => e.Value);

    private static List<Box> Boxes(XDocument doc)
    {
        var list = new List<Box>();
        foreach (var r in doc.Descendants().Where(e => e.Name.LocalName == "rect" && e.Attribute("height")?.Value != "18" && (e.Attribute("rx") is not null && e.Attribute("stroke") is not null || e.Attribute("fill")?.Value == "#12203d")))
        {
            double N(string a) => double.Parse(r.Attribute(a)!.Value, System.Globalization.CultureInfo.InvariantCulture);
            list.Add(new Box("rect", N("x"), N("y"), N("x") + N("width"), N("y") + N("height")));
        }
        foreach (var p in doc.Descendants().Where(e => e.Name.LocalName == "polygon" && e.Attribute("fill")?.Value == "#fff6e3"))
        {
            var pts = p.Attribute("points")!.Value.Split(' ').Select(t => t.Split(',')).Select(t => (X: double.Parse(t[0], System.Globalization.CultureInfo.InvariantCulture), Y: double.Parse(t[1], System.Globalization.CultureInfo.InvariantCulture))).ToList();
            list.Add(new Box("diamond", pts.Min(t => t.X), pts.Min(t => t.Y), pts.Max(t => t.X), pts.Max(t => t.Y)));
        }
        return list;
    }

    // ------------------------------------------------------------------ tests

    [Theory]
    [InlineData("chain")]
    [InlineData("choice")]
    [InlineData("discharge")]
    public void The_drawing_is_valid_svg_and_every_step_and_question_is_in_it_in_words(string which)
    {
        var f = which switch { "chain" => BmwChain(), "choice" => BmwChoice(), _ => Discharge() };
        var d = FlowDiagramBuilder.Build(f);
        var doc = Parse(d);
        var all = string.Join(" ", Texts(doc));

        doc.Root!.Name.LocalName.Should().Be("svg");
        d.Width.Should().BeGreaterThan(300);
        d.Height.Should().BeGreaterThan(200);
        for (var i = 1; i <= f.Steps.Count; i++) Texts(doc).Should().Contain(i.ToString(), "each step carries its number");
        foreach (var s in f.Steps)
        {
            var firstWords = string.Join(" ", s.Action.Split(' ').Take(2));
            all.Should().Contain(firstWords, "the step's own words are in the picture");
            all.Should().Contain("Screen: " + s.Screen.Replace("Discharge Advice", "Discharge Advice"), "the touch point is on the step");
        }
        foreach (var q in f.Decisions) all.Should().Contain(q.Condition.Split(' ')[0]);
        all.Should().Contain("START").And.Contain("END");
    }

    [Theory]
    [InlineData("chain")]
    [InlineData("choice")]
    [InlineData("discharge")]
    public void No_two_boxes_overlap_and_all_of_them_are_inside_the_picture(string which)
    {
        var f = which switch { "chain" => BmwChain(), "choice" => BmwChoice(), _ => Discharge() };
        var d = FlowDiagramBuilder.Build(f);
        var boxes = Boxes(Parse(d));

        boxes.Should().HaveCountGreaterThanOrEqualTo(f.Steps.Count + f.Decisions.Count + 2);
        foreach (var b in boxes) { b.Left.Should().BeGreaterThanOrEqualTo(0); b.Right.Should().BeLessThanOrEqualTo(d.Width); b.Top.Should().BeGreaterThanOrEqualTo(0); b.Bottom.Should().BeLessThanOrEqualTo(d.Height); }
        for (var i = 0; i < boxes.Count; i++)
            for (var j = i + 1; j < boxes.Count; j++)
            {
                var a = boxes[i]; var b = boxes[j];
                var overlap = a.Left < b.Right - 1 && b.Left < a.Right - 1 && a.Top < b.Bottom - 1 && b.Top < a.Bottom - 1;
                overlap.Should().BeFalse($"boxes {i} and {j} must not overlap");
            }
    }

    [Fact]
    public void Text_never_runs_wider_than_its_box()
    {
        var d = FlowDiagramBuilder.Build(BmwChain());
        var doc = Parse(d);
        var steps = doc.Descendants().Where(e => e.Name.LocalName == "rect" && e.Attribute("width")?.Value == "236").ToList();
        steps.Should().NotBeEmpty();
        foreach (var t in doc.Descendants().Where(e => e.Name.LocalName == "text" && e.Attribute("font-size")?.Value == "13" && e.Attribute("text-anchor") is null))
            t.Value.Length.Should().BeLessThanOrEqualTo(28, "lines are wrapped to the box width");
    }

    [Fact]
    public void A_menu_of_choices_is_one_step_with_a_labelled_route_to_each_option_and_far_smaller_than_the_chain()
    {
        var chain = FlowDiagramBuilder.Build(BmwChain());
        var choice = FlowDiagramBuilder.Build(BmwChoice());
        var all = string.Join("|", Texts(Parse(choice)));

        foreach (var screen in new[] { "Treatment Method", "Waste Category", "Exception Type", "Reason", "Weight Unit", "Corporate" })
            Parse(choice).Descendants().Where(e => e.Name.LocalName == "text" && e.Value == screen).Should().NotBeEmpty($"a route is labelled {screen}");
        all.Should().NotContain("Did the administrator select");
        choice.Height.Should().BeLessThan(chain.Height, "a menu is two rows, not a staircase of five questions");
        choice.NodeCount.Should().Be(BmwChoice().Steps.Count + 2);
    }

    [Fact]
    public void Questions_have_labelled_yes_and_no_routes()
    {
        var doc = Parse(FlowDiagramBuilder.Build(Discharge()));
        Texts(doc).Count(t => t == "Yes").Should().Be(2);
        Texts(doc).Count(t => t == "No" || t.StartsWith("No ")).Should().Be(2, "the loop back to an earlier step is also a No");
    }

    [Fact]
    public void A_loop_back_and_an_alternative_way_are_drawn_without_hanging_or_losing_a_box()
    {
        var d = FlowDiagramBuilder.Build(Discharge());
        var all = string.Join(" ", Texts(Parse(d)));
        all.Should().Contain("ALTERNATIVE").And.Contain("Discharge against medical advice");
        all.Should().Contain("↺", "the way back to an earlier step is marked");
        d.NodeCount.Should().Be(6 + 2 + 2 + 1 + 2 - 2 + 0, "6 steps, 2 questions, 1 alternative, start and end");
    }

    [Fact]
    public void The_trigger_is_shown_at_the_start_and_an_unknown_trigger_is_not()
    {
        Texts(Parse(FlowDiagramBuilder.Build(BmwChain()))).Should().Contain("STARTS WHEN");
        var f = BmwChain();
        f.Trigger = "To be confirmed";
        var doc = Parse(FlowDiagramBuilder.Build(f));
        Texts(doc).Should().NotContain("STARTS WHEN").And.NotContain(t => t.Contains("To be confirmed"));
    }

    [Fact]
    public void Roles_are_shown_by_name_not_by_id()
    {
        var all = string.Join("|", Texts(Parse(FlowDiagramBuilder.Build(Discharge()))));
        all.Should().Contain("Doctor").And.Contain("Nurse").And.Contain("Billing").And.NotContain("A1").And.NotContain("A2");
    }

    [Fact]
    public void Nothing_in_the_notes_can_put_markup_or_script_into_the_picture()
    {
        var f = BmwChoice();
        f.Steps[1].Action = "<script>alert(1)</script> & \"quoted\" <img src=x onerror=y>";
        f.Steps[1].Screen = "<b>Screen</b>";
        f.Trigger = "</text><script>x</script>";
        var d = FlowDiagramBuilder.Build(f);

        d.Svg.Should().NotContain("<script").And.NotContain("<img").And.NotContain("onerror=y>").And.NotContain("<b>");
        Parse(d).Should().NotBeNull("it is still valid XML");
        GuideHtmlBuilder.IsSafeSvg(d.Svg).Should().BeTrue("the document accepts it as a plain drawing");
    }

    [Fact]
    public void Long_text_is_wrapped_and_cut_with_an_ellipsis_rather_than_overflowing()
    {
        var f = BmwChoice();
        f.Steps[1].Action = string.Join(" ", Enumerable.Repeat("configures the extremely detailed waste category records", 12));
        var doc = Parse(FlowDiagramBuilder.Build(f));
        Texts(doc).Count(t => t.EndsWith('…')).Should().BeGreaterThan(0);
        FlowDiagramBuilder.Wrap("Supercalifragilisticexpialidocious_and_more_than_28_chars", 28, 5).Should().OnlyContain(l => l.Length <= 28);
        FlowDiagramBuilder.Wrap("", 28, 5).Should().Equal("");
    }

    [Fact]
    public void An_empty_workflow_a_missing_target_and_a_self_loop_do_not_break_the_drawing()
    {
        FlowDiagramBuilder.Build(new FlowContent()).Svg.Should().Contain("START").And.Contain("END");

        var f = new FlowContent();
        f.Steps.Add(Step("S1", "Does it.", "Menu", ["S1", "NOWHERE", "S2"]));
        f.Steps.Add(Step("S2", "Then this.", "", []));
        f.Decisions.Add(new DecisionItem { Id = "D1", Condition = "Orphan question?" });
        var d = FlowDiagramBuilder.Build(f);
        Parse(d).Should().NotBeNull();
        d.NodeCount.Should().Be(5);
    }

    [Fact]
    public void The_same_workflow_always_gives_the_same_picture()
    {
        FlowDiagramBuilder.Build(Discharge()).Svg.Should().Be(FlowDiagramBuilder.Build(Discharge()).Svg);
    }

    [Fact]
    public void A_tall_picture_can_be_cut_into_pages_between_rows_and_each_piece_is_a_valid_svg_of_that_height()
    {
        var f = new FlowContent();
        for (var i = 1; i <= 14; i++) f.Steps.Add(Step("S" + i, $"Step number {i} of a long process that keeps going.", "Screen " + i, [i == 14 ? "END" : "S" + (i + 1)]));
        var d = FlowDiagramBuilder.Build(f);

        d.CutPoints.Should().HaveCountGreaterThan(5).And.BeInAscendingOrder();
        var piece = d.Slice(0, d.CutPoints[4]);
        XDocument.Parse(piece).Root!.Attribute("viewBox")!.Value.Should().Be($"0 0 {d.Width} {d.CutPoints[4]}");
        // a cut falls in the gap between two rows: no box crosses it
        foreach (var cut in d.CutPoints)
            Boxes(Parse(d)).Should().NotContain(b => b.Top < cut && b.Bottom > cut);
    }

    // ------------------------------------------------------------------ for looking at: only when asked

    [Fact]
    public void Samples_are_written_for_a_look_when_FC_SAMPLES_is_set()
    {
        var dir = Environment.GetEnvironmentVariable("FC_SAMPLES");
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "bmw-chain.svg"), FlowDiagramBuilder.Build(BmwChain()).Svg);
        File.WriteAllText(Path.Combine(dir, "bmw-choice.svg"), FlowDiagramBuilder.Build(BmwChoice()).Svg);
        File.WriteAllText(Path.Combine(dir, "discharge.svg"), FlowDiagramBuilder.Build(Discharge()).Svg);
        foreach (var (name, flow) in new[] { ("bmw-complete", BmwComplete()), ("bmw-choice", BmwChoice()), ("discharge", Discharge()), ("bmw-chain", BmwChain()), ("big", GuideHtmlTests.BigFlow()) })
        {
            var plan = GuidePlanBuilder.FromFlow(flow);
            var html = GuideHtmlBuilder.Build(new GuideInput("IMPL-SAMPLE-001", "Sample Hospital", flow.FlowTitle, "General", "Asha Kumar", new DateTime(2026, 11, 17), 1, flow, plan, null, [], false, null, null, false, FlowDiagramBuilder.Build(flow)));
            File.WriteAllText(Path.Combine(dir, name + ".html"), html);
        }

        // a document with two real pictures, one tied to a step and one with the attachments (FC_FIGURE is a PNG to use)
        var picture = Environment.GetEnvironmentVariable("FC_FIGURE");
        if (!string.IsNullOrWhiteSpace(picture) && File.Exists(picture))
        {
            var flow = Discharge();
            var bytes = File.ReadAllBytes(picture);
            var html = GuideHtmlBuilder.Build(new GuideInput("IMPL-WARD-CR-001", "Sample Hospital", flow.FlowTitle, "Ward", "Asha Kumar", new DateTime(2026, 11, 18), 1, flow, GuidePlanBuilder.FromFlow(flow), null, [], false, null, null, false, FlowDiagramBuilder.Build(flow))
            {
                RequestCode = "CR-001",
                Figures = [new GuideFigure(1, "The discharge advice screen as it is today", "S1", "image/png", bytes), new GuideFigure(2, "The printed discharge summary that must change", null, "image/png", bytes)],
            });
            File.WriteAllText(Path.Combine(dir, "figures.html"), html);
        }
    }
}
