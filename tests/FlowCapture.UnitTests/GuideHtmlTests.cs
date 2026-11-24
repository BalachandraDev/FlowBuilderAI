using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlowCapture.Application.Documents;
using FlowCapture.Shared.Content;
using FluentAssertions;
using Xunit;

namespace FlowCapture.UnitTests;

public class GuideHtmlTests
{
    private static readonly JsonObject Plan = (JsonObject)JsonNode.Parse("""
        {"summary":"This flow lets the MRD team receive case sheets.\nA second paragraph.",
         "scopeIn":["Receiving case sheets"],"scopeOut":["How wards send them"],
         "roles":[{"role":"MRD user","responsibility":"Receives and saves the sheets.","usesAreas":["Receive menu","Ward list"]}],
         "journey":[
           {"title":"Open the menu","who":"MRD user","where":"Receive menu","description":"The user opens the menu <b>now</b>.","outcome":"The ward list appears.","branches":[{"condition":"A super user","result":"Own ward selected"}]},
           {"title":"Save","who":"MRD user","where":"Receive menu","description":"Saves the sheet.","outcome":"Recorded.","branches":[]}],
         "touchpoints":[{"name":"Receive menu","type":"Menu","whatHappens":"Pick the ward and save.","dataIn":["Ward"],"dataOut":["Received status"]}],
         "existingAreasAffected":[{"area":"Ward dropdown","effect":"Pre-selected for super users."}],
         "handoffs":[{"from":"MRD","to":"Billing","whatPasses":"Received sheets","when":"On save"}],
         "assumptions":["Super users are mapped in user settings."],"openQuestions":["How long may a sheet stay pending?","To be confirmed"]}
        """)!;

    private static GuideInput Input(JsonObject? plan = null, byte[]? png = null, bool demo = false, FlowContent? flow = null, byte[]? logo = null) => new(
        "IMPL-T-001", "Test Hospital", "IP Case Sheet Receive", "MRD", "Asha Lead", new DateTime(2026, 11, 17), 3,
        flow ?? FlowJson.Deserialize(TestData.Example().ToJsonString()), plan ?? Plan, png ?? [137, 80, 78, 71],
        [new GuideOpenQuestion("Q1", "Which ward is pre-selected?", "Open")], demo, logo);

    private static string Visible(string html) => Regex.Replace(Regex.Replace(html, "<style>.*?</style>", "", RegexOptions.Singleline), "<[^>]+>", " ");

    [Fact]
    public void The_cover_looks_like_the_company_requirement_documents()
    {
        var html = GuideHtmlBuilder.Build(Input());

        html.Should().Contain("AOSTA INDIA PRIVATE LIMITED").And.Contain("BACKBONE").And.Contain("Implementation Document");
        html.Should().Contain("IP Case Sheet Receive");
        foreach (var row in new[] { "Document ID", "IMPL-T-001", "Project", "Test Hospital", "Prepared by", "Asha Lead", "Created date", "17-11-2026", "Workflow version" })
            html.Should().Contain(row);
        html.Should().Contain("Department").And.Contain("MRD");
        html.IndexOf("class=\"cover\"", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("Purpose", StringComparison.Ordinal));
    }

    [Fact]
    public void The_cover_shows_the_logo_only_when_one_was_supplied_and_hides_the_placeholder_department()
    {
        GuideHtmlBuilder.Build(Input()).Should().NotContain("class=\"logo\"");
        GuideHtmlBuilder.Build(Input(logo: [1, 2, 3])).Should().Contain("class=\"logo\"").And.Contain("data:image/png;base64,AQID");
        GuideHtmlBuilder.Build(Input() with { Department = "General" }).Should().NotContain("<td>Department</td>");
    }

    [Fact]
    public void The_sections_explain_the_flow_and_its_touch_points_in_order()
    {
        var html = GuideHtmlBuilder.Build(Input());

        var order = new[] { "Purpose", "Functional requirements", "User roles and responsibilities", "Use case description", "The flow at a glance", "How it works, stage by stage", "Where it touches the system", "Assumptions and open points" }
            .Select(h => html.IndexOf(h, StringComparison.Ordinal)).ToList();
        order.Should().OnlyContain(i => i >= 0).And.BeInAscendingOrder();
        html.Should().Contain("data:image/png;base64,iVBORw==");
        html.Should().Contain("Open the menu").And.Contain("Own ward selected").And.Contain("Screens, menus and masters").And.Contain("Existing BACKBONE areas affected")
            .And.Contain("Hand-offs to other departments and systems").And.Contain("MRD &rarr; Billing").And.Contain("From &rarr; to, or with");
    }

    [Fact]
    public void It_is_not_a_development_plan()
    {
        var text = Visible(GuideHtmlBuilder.Build(Input()));

        foreach (var word in new[] { "development plan", "Effort", "Test scenarios", "Phase", "Suggested", "suggested", "Screens and fields", "Boolean", "REST", "migration" })
            text.Should().NotContain(word);
    }

    [Fact]
    public void Nothing_unknown_is_printed_as_To_be_confirmed()
    {
        var flow = FlowJson.Deserialize(TestData.Example().ToJsonString());
        flow.Steps[0].Screen = "To be confirmed";
        flow.Requirements[0].Priority = "To be confirmed";
        flow.Purpose = "To be confirmed";
        flow.Validations.Add(new ValidationItem { Id = "V9", Field = "To be confirmed", Rule = "Must be a number", ErrorMessage = "To be confirmed" });

        var text = Visible(GuideHtmlBuilder.Build(Input(flow: flow)));

        text.Should().NotContain("To be confirmed");
        text.Should().Contain("Not stated", "an unknown priority is said plainly");
        text.Should().Contain("How long may a sheet stay pending?").And.NotContain("To be confirmed", "an unknown in the AI's list is dropped, not shown");
    }

    [Fact]
    public void Open_points_gather_the_AIs_questions_the_departments_open_questions_and_the_gaps_without_repeats()
    {
        var flow = FlowJson.Deserialize(TestData.Example().ToJsonString());
        flow.Gaps.Add(new GapItem { Id = "G1", Description = "Who may change it is not stated." });
        flow.Gaps.Add(new GapItem { Id = "G2", Description = "who may change it is not stated." });

        var text = Visible(GuideHtmlBuilder.Build(Input(flow: flow)));

        foreach (var q in new[] { "How long may a sheet stay pending?", "Which ward is pre-selected?", "Who may change it is not stated." }) text.Should().Contain(q);
        Regex.Matches(text, "who may change it is not stated", RegexOptions.IgnoreCase).Count.Should().Be(1);
        text.Should().Contain("Super users are mapped in user settings.");
    }

    [Fact]
    public void Rules_say_which_step_they_apply_to_in_words_not_in_ids()
    {
        var flow = FlowJson.Deserialize(TestData.Example().ToJsonString());
        var html = GuideHtmlBuilder.Build(Input(flow: flow));
        var rulesPart = html[html.IndexOf("What the solution must do and follow", StringComparison.Ordinal)..html.IndexOf("Requirements", html.IndexOf("What the solution must do and follow", StringComparison.Ordinal), StringComparison.Ordinal)];

        rulesPart.Should().NotContainAny("&gt;S2&lt;", ">S2<", ">D1<", ">ALT1<");
        GuideHtmlBuilder.Label(flow, flow.Steps[0].Id).Should().Be("Opens the IP Case Sheet Receive in Depa...");
        GuideHtmlBuilder.Label(flow, "ZZ9").Should().Be("ZZ9", "an unknown id is shown as it is rather than hidden");
    }

    [Fact]
    public void Sections_are_numbered_without_gaps()
    {
        var numbers = Regex.Matches(GuideHtmlBuilder.Build(Input()), @"<h2[^>]*><span>(\d+)</span>").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        numbers.Should().Equal(Enumerable.Range(1, numbers.Count));
    }

    [Fact]
    public void The_diagram_is_on_a_page_of_its_own_the_same_size_as_every_other_page()
    {
        var flow = FlowDiagramTests.BmwComplete();
        var html = GuideHtmlBuilder.Build(Input(plan: GuidePlanBuilder.FromFlow(flow), flow: flow) with { Diagram = FlowDiagramBuilder.Build(flow) });
        html.Should().Contain("class=\"diagram-page portrait").And.NotContain("page: wide", "no landscape page: the diagram is the same page size as the rest");
        html.Should().NotContain("@page wide");
        System.Text.RegularExpressions.Regex.Matches(html, "size: A4").Count.Should().Be(1, "one page size for the whole document");
    }

    [Fact]
    public void Each_page_carries_the_document_number_and_a_page_number_but_the_cover_does_not()
    {
        var html = GuideHtmlBuilder.Build(Input());
        html.Should().Contain("content: \"IMPL-T-001\"").And.Contain("counter(page)").And.Contain("@page :first");
    }

    [Fact]
    public void Nothing_the_AI_or_a_user_wrote_can_put_a_tag_or_script_into_the_page()
    {
        var flow = FlowJson.Deserialize(TestData.Example().ToJsonString());
        flow.FlowTitle = "Bad <img src=x onerror=alert(1)> title";
        flow.Steps[0].Action = "<script>steal()</script>";

        var html = GuideHtmlBuilder.Build(Input(flow: flow) with { FlowTitle = flow.FlowTitle, ProjectName = "<script>x</script>" });

        html.Should().NotContain("<script").And.NotContain("<img src=x").And.NotContain("<b>now</b>");
        html.Should().Contain("&lt;b&gt;now&lt;/b&gt;");
        Regex.Matches(html, "<img ").Count.Should().Be(1, "the only picture is the diagram");
    }

    [Fact]
    public void A_document_whose_explanation_is_missing_is_still_complete_from_the_workflow_itself()
    {
        var html = GuideHtmlBuilder.Build(Input(plan: new JsonObject { ["summary"] = "Only a summary." }));

        html.Should().Contain("Only a summary.").And.Contain("How it works, stage by stage").And.Contain("Requirements");
        Visible(html).Should().Contain("Opens the IP Case Sheet Receive in Department menu", "the stages come from the workflow's own steps");
        Visible(html).Should().NotContain(".;");
    }

    // ------------------------------------------------------------------ the diagram as SVG, and the plan built without the AI

    private const string Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"300\" height=\"120\" viewBox=\"0 0 300 120\"><g><rect width=\"100\" height=\"40\"/><text>Start</text></g></svg>";

    [Fact]
    public void The_diagram_is_printed_as_vector_svg_when_one_is_given_and_the_png_is_not_needed()
    {
        var html = GuideHtmlBuilder.Build(Input() with { DiagramPng = null, DiagramSvg = Svg });

        html.Should().Contain("<div class=\"diagram\"><svg").And.NotContain("The diagram could not be drawn.").And.NotContain("data:image/png;base64,iVBORw");
        Regex.Matches(html, "<img ").Count.Should().Be(0);
    }

    [Theory]
    [InlineData("<svg><script>alert(1)</script></svg>")]
    [InlineData("<svg onload=\"x()\"></svg>")]
    [InlineData("<svg><foreignObject><div>hi</div></foreignObject></svg>")]
    [InlineData("<svg><a href=\"https://evil.example/\"><text>x</text></a></svg>")]
    [InlineData("<svg><image href=\"data:image/png;base64,AAAA\"/></svg>")]
    [InlineData("<div>not an svg</div>")]
    [InlineData("")]
    public void An_svg_that_is_not_a_plain_drawing_is_never_put_into_the_page(string svg)
    {
        GuideHtmlBuilder.IsSafeSvg(svg).Should().BeFalse();
        var html = GuideHtmlBuilder.Build(Input() with { DiagramSvg = svg });
        html.Should().NotContain("<script").And.NotContain("onload").And.NotContain("evil.example").And.NotContain("foreignObject");
        html.Should().Contain("data:image/png;base64,iVBORw==", "the picture is used instead");
    }

    [Fact]
    public void The_footer_says_whether_the_wording_is_the_AIs_or_from_the_workflow()
    {
        Visible(GuideHtmlBuilder.Build(Input())).Should().Contain("It was written from the analysed workflow");
        Visible(GuideHtmlBuilder.Build(Input() with { AiWritten = true })).Should().Contain("The wording is the AI's explanation");
    }

    [Fact]
    public void A_plan_built_from_the_workflow_gives_every_step_its_own_stage_and_puts_each_decision_where_it_is_made()
    {
        var flow = FlowJson.Deserialize(TestData.Example().ToJsonString());
        var plan = GuidePlanBuilder.FromFlow(flow);

        GuideHtmlBuilder.Arr(plan, "journey").Where(j => GuideHtmlBuilder.Str(j, "title").Length > 0).Should().HaveCount(flow.Steps.Count + flow.Decisions.Count(d => !flow.Steps.Any(s => s.Next.Contains(d.Id))));
        var withBranches = GuideHtmlBuilder.Arr(plan, "journey").Where(j => GuideHtmlBuilder.Arr(j, "branches").Count > 0).ToList();
        withBranches.Should().NotBeEmpty("the example flow has a decision after a step");
        GuideHtmlBuilder.Arr(withBranches[0], "branches").Should().HaveCount(2).And.OnlyContain(b => GuideHtmlBuilder.Str(b, "result").Length > 0);
        GuideHtmlBuilder.Arr(plan, "roles").Select(r => GuideHtmlBuilder.Str(r, "role")).Should().BeEquivalentTo(flow.Actors.Select(a => a.Role));
        GuideHtmlBuilder.Str(plan, "summary").Should().Be(flow.Purpose);
    }

    [Fact]
    public void A_document_from_a_built_plan_is_complete_has_no_development_content_and_no_To_be_confirmed()
    {
        var flow = FlowJson.Deserialize(TestData.Example().ToJsonString());
        flow.Steps[0].Screen = "To be confirmed";
        var html = GuideHtmlBuilder.Build(Input(plan: GuidePlanBuilder.FromFlow(flow), flow: flow) with { DiagramPng = null, DiagramSvg = Svg });
        var text = System.Net.WebUtility.HtmlDecode(Visible(html));

        foreach (var heading in new[] { "Purpose", "Functional requirements", "User roles and responsibilities", "Use case description", "The flow at a glance", "How it works, stage by stage", "Where it touches the system" })
            text.Should().Contain(heading);
        text.Should().Contain(flow.Steps[1].Action.TrimEnd('.')).And.NotContain("To be confirmed").And.NotContain(".;");
        foreach (var word in new[] { "Effort", "Test scenarios", "Phase", "Boolean", "migration" }) text.Should().NotContain(word);
        flow.Screens.SelectMany(s => s.Fields).Select(f => f.Name).Where(n => n.Length > 3).Should().OnlyContain(n => !text.Contains(n + " (" ), "screen field lists are development detail");
    }

    [Fact]
    public void A_stage_does_not_repeat_its_title_and_a_branch_names_where_it_continues_in_full()
    {
        var longAction = "The nurse checks the wristband against the discharge slip, records any difference in the ward register, informs the doctor on duty and waits for the written reply";
        var flow = new FlowContent
        {
            FlowTitle = "Discharge", Actors = [new ActorItem { Id = "A1", Role = "Nurse" }],
            Steps =
            [
                new StepItem { Id = "S1", Actor = "A1", Action = "Opens the menu.", Screen = "Discharge", Next = ["D1"] },
                new StepItem { Id = "S2", Actor = "Nurse", Action = longAction, Screen = "Discharge", Inputs = ["Wristband"], Outputs = ["Register entry"] },
            ],
            Decisions = [new DecisionItem { Id = "D1", Condition = "Is the slip signed?", YesPath = new DecisionPath { Summary = "Go on.", Next = "S2" }, NoPath = new DecisionPath { Summary = "Stop.", Next = "" } }],
        };
        var journey = GuideHtmlBuilder.Arr(GuidePlanBuilder.FromFlow(flow), "journey");

        GuideHtmlBuilder.Str(journey[0], "description").Should().BeEmpty("the title already says it");
        GuideHtmlBuilder.Str(journey[0], "who").Should().Be("Nurse", "an actor id is shown as the role");
        var title = GuideHtmlBuilder.Str(journey[1], "title");
        title.Should().EndWith("\u2026").And.NotContain("  ");
        title.Length.Should().BeLessThanOrEqualTo(110, "a long sentence is cut at a word, with the whole sentence given underneath");
        title.Should().NotEndWith(" \u2026");
        GuideHtmlBuilder.Str(journey[1], "description").Should().StartWith(longAction).And.Contain("Uses: Wristband.");
        GuideHtmlBuilder.Arr(journey[0], "branches").Select(b => GuideHtmlBuilder.Str(b, "result")).Should().Contain(r => r.Contains("Continues with: " + longAction + "."));
    }

    [Fact]
    public void A_condition_that_ends_with_a_full_stop_does_not_leave_two()
    {
        var flow = FlowJson.Deserialize(TestData.Example().ToJsonString());
        flow.AlternateFlows.Add(new AlternateFlowItem { Id = "ALT9", Name = "Another way", Condition = "The super user wants a different ward.", Steps = ["Picks it."] });
        var text = Visible(GuideHtmlBuilder.Build(Input(flow: flow)));
        text.Should().Contain("When The super user wants a different ward. Picks it.");
        Regex.IsMatch(text, @"[^.]\.\.(?!\.)").Should().BeFalse("two full stops in a row are a typing mistake (three dots are a deliberate cut)");
    }

    [Fact]
    public void A_plan_can_be_built_from_an_empty_workflow_without_failing()
    {
        var plan = GuidePlanBuilder.FromFlow(new FlowContent());
        GuideHtmlBuilder.Arr(plan, "journey").Should().BeEmpty();
        Visible(GuideHtmlBuilder.Build(Input(plan: plan, flow: new FlowContent { FlowTitle = "Empty" }) with { DiagramSvg = Svg })).Should().Contain("Purpose");
    }

    // ------------------------------------------------------------------ a complete document: control, screens, sign-off

    private static string Complete(GuideInput? over = null) =>
        GuideHtmlBuilder.Build((over ?? Input(plan: GuidePlanBuilder.FromFlow(FlowDiagramTests.BmwComplete()), flow: FlowDiagramTests.BmwComplete())) with { Diagram = FlowDiagramBuilder.Build(FlowDiagramTests.BmwComplete()), DiagramPng = null });

    [Fact]
    public void Document_control_lists_every_version_of_the_document_with_who_made_it_and_from_which_workflow_version()
    {
        var html = Complete(Input(plan: GuidePlanBuilder.FromFlow(FlowDiagramTests.BmwComplete()), flow: FlowDiagramTests.BmwComplete()) with
        {
            History = [new GuideVersionRow(1, new DateTime(2026, 11, 16), "Vignesh", 1), new GuideVersionRow(2, new DateTime(2026, 11, 17), "Asha Lead", 3)],
        });
        var text = Visible(html);

        text.Should().Contain("Document control").And.Contain("Versions of this document").And.Contain("16-11-2026").And.Contain("Vignesh").And.Contain("Asha Lead");
        html.IndexOf("Document control", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("Purpose", html.IndexOf("Document control", StringComparison.Ordinal), StringComparison.Ordinal));
        Regex.Matches(html, @"<tr><td class=""strong"">\d</td><td>\d\d-\d\d-\d{4}</td>").Count.Should().Be(2);
    }

    [Fact]
    public void Each_screen_is_shown_with_its_own_fields_and_the_step_it_belongs_to_and_columns_nobody_filled_in_are_left_out()
    {
        var html = Complete();
        var text = System.Net.WebUtility.HtmlDecode(Visible(html));

        text.Should().Contain("Screens and what they hold");
        foreach (var screen in new[] { "Treatment Method", "Waste Category", "Exception Type", "Reason", "Weight Unit", "Corporate" }) text.Should().Contain(screen);
        text.Should().Contain("Bag colour").And.Contain("Yellow, Red, Blue, White or Green").And.Contain("Licence details").And.Contain("Contact number");
        Regex.Matches(html, "class=\"screenbox\"").Count.Should().Be(7);
        html.Should().Contain("step 5", "each screen says which step of the flow it belongs to");
        var section = html[html.IndexOf("Screens and what they hold", StringComparison.Ordinal)..];
        section[..section.IndexOf("</section>", StringComparison.Ordinal)].Should().NotContain("<th>Required</th>").And.NotContain("<th>Default</th>", "nothing was said about them");
    }

    [Fact]
    public void The_screens_section_is_left_out_when_the_notes_named_no_fields_at_all()
    {
        var flow = FlowDiagramTests.BmwComplete();
        flow.Screens.Clear();
        Visible(GuideHtmlBuilder.Build(Input(plan: GuidePlanBuilder.FromFlow(flow), flow: flow))).Should().NotContain("Screens and what they hold");
    }

    [Fact]
    public void The_document_ends_with_an_approval_table_for_the_preparer_a_reviewer_and_an_approver()
    {
        var text = Visible(Complete());
        text.Should().Contain("Approval and sign-off").And.Contain("Prepared by").And.Contain("Reviewed by").And.Contain("Approved by").And.Contain("Signature");
        Visible(Complete()).IndexOf("Approval and sign-off", StringComparison.Ordinal).Should().BeGreaterThan(Visible(Complete()).IndexOf("Assumptions and open points", StringComparison.Ordinal));
    }

    [Fact]
    public void A_rule_says_which_steps_it_applies_to_by_number_and_page_and_the_whole_complete_document_is_numbered_without_gaps()
    {
        var html = Complete();
        var text = Visible(html);
        text.Should().Contain("Step 2: Treatment Method").And.Contain("Step 7: Corporate").And.Contain("Step 1: BMW Masters").And.NotContain("...");
        var numbers = Regex.Matches(html, @"<h2[^>]*><span>(\d+)</span>").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        numbers.Should().Equal(Enumerable.Range(1, numbers.Count));
        numbers.Count.Should().BeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public void A_complete_document_still_contains_no_development_plan_words_and_no_To_be_confirmed()
    {
        var text = Visible(Complete());
        foreach (var word in new[] { "Effort", "Test scenarios", "development plan", "Phase", "Boolean", "migration", "To be confirmed" }) text.Should().NotContain(word);
    }

    [Fact]
    public void A_small_picture_sits_with_the_text_and_a_big_one_gets_its_own_page()
    {
        var small = new FlowContent();
        small.Steps.Add(new StepItem { Id = "S1", Actor = "A", Action = "Does it.", Screen = "Menu", Next = ["END"] });
        Complete(Input(plan: GuidePlanBuilder.FromFlow(small), flow: small)).Should().NotBeNull();
        GuideHtmlBuilder.Build(Input(plan: GuidePlanBuilder.FromFlow(small), flow: small) with { Diagram = FlowDiagramBuilder.Build(small) }).Should().Contain("diagram-page portrait inline");
        Complete().Should().NotContain("portrait inline", "the BMW menu picture is wide: it has a landscape page");
    }

    /// <summary>A long process: thirty steps in a row, a question every fifth step with a way back, and a menu of eight choices.</summary>
    internal static FlowContent BigFlow()
    {
        var f = new FlowContent { FlowTitle = "Large process", Trigger = "The case starts.", Actors = [new ActorItem { Id = "A1", Role = "Nurse" }, new ActorItem { Id = "A2", Role = "Doctor" }] };
        for (var i = 1; i <= 30; i++)
            f.Steps.Add(new StepItem { Id = $"S{i}", Actor = i % 2 == 0 ? "A1" : "A2", Action = $"Step number {i} is done by the person in charge and recorded.", Screen = $"Screen {i}", Next = [i % 5 == 0 ? $"D{i / 5}" : i == 30 ? "END" : $"S{i + 1}"] });
        for (var d = 1; d <= 5; d++)
            f.Decisions.Add(new DecisionItem { Id = $"D{d}", Condition = $"Is check {d} complete?",
                YesPath = new DecisionPath { Summary = "Yes", Next = d == 6 ? "END" : $"S{d * 5 + 1}" }, NoPath = new DecisionPath { Summary = "No", Next = $"S{d * 5 - 4}" } });
        f.Steps[29].Next = ["END"];
        return f;
    }

    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    [Fact]
    public void A_picture_is_printed_under_its_step_and_the_others_in_a_section_of_attachments_each_with_its_caption()
    {
        var flow = FlowDiagramTests.Discharge();
        var plan = GuidePlanBuilder.FromFlow(flow);
        var html = GuideHtmlBuilder.Build(Input(plan: plan, flow: flow) with { Diagram = FlowDiagramBuilder.Build(flow) }
            with
            {
                Figures =
                [
                    new GuideFigure(1, "The advice screen <b>as it is today</b>", "S1", "image/png", Png),
                    new GuideFigure(2, "The printed discharge summary", null, "image/png", Png),
                    new GuideFigure(3, "Not a picture", "S2", "text/html", [1, 2, 3]),
                ]
            });

        var stage = html.IndexOf("Doctor enters the discharge advice", StringComparison.Ordinal);
        var firstFigure = html.IndexOf("Figure </b>", StringComparison.Ordinal);
        html.Should().Contain("<b>Figure 1.</b> The advice screen &lt;b&gt;as it is today&lt;/b&gt;", "a caption is text, never markup");
        html.IndexOf("<b>Figure 1.</b>", StringComparison.Ordinal).Should().BeGreaterThan(stage, "the picture of step S1 is printed after that step");
        html.IndexOf("<b>Figure 1.</b>", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("Attached pictures</h2>", StringComparison.Ordinal), "it is not repeated with the attachments");
        html.Should().Contain("Attached pictures</h2>").And.Contain("<b>Figure 2.</b> The printed discharge summary");
        html.Should().NotContain("Figure 3", "only pictures are printed, whatever the type says").And.NotContain("data:text/html");
        Regex.Matches(html, "data:image/png;base64,").Count.Should().Be(2);
        firstFigure.Should().Be(-1);
    }

    [Fact]
    public void Without_pictures_the_document_has_no_attachments_section()
    {
        var flow = FlowDiagramTests.Discharge();
        var html = GuideHtmlBuilder.Build(Input(plan: GuidePlanBuilder.FromFlow(flow), flow: flow) with { Diagram = FlowDiagramBuilder.Build(flow) });
        html.Should().NotContain("Attached pictures").And.NotContain("<figure");
    }

    [Fact]
    public void The_request_and_its_module_are_on_the_cover_and_in_the_document_control_table()
    {
        var html = GuideHtmlBuilder.Build(Input() with { RequestCode = "CR-012" });
        Regex.Matches(html, "CR-012 \\(Change Request\\)").Count.Should().BeGreaterThanOrEqualTo(2, "on the cover and in the document control table");
        GuideHtmlBuilder.Build(Input() with { RequestCode = "NR-003" }).Should().Contain("NR-003 (New Requirement)");
        GuideHtmlBuilder.Build(Input()).Should().NotContain("(New Requirement)", "a document made before requests were numbered has no request row");
    }
    [Fact]
    public void However_big_the_flow_is_the_diagram_takes_exactly_one_page()
    {
        foreach (var flow in new[] { BigFlow(), FlowDiagramTests.BmwComplete(), FlowDiagramTests.Discharge() })
        {
            var html = GuideHtmlBuilder.Build(Input(plan: GuidePlanBuilder.FromFlow(flow), flow: flow) with { Diagram = FlowDiagramBuilder.Build(flow) });
            Regex.Matches(html, "<section class=\"diagram-page").Count.Should().Be(1, "the whole diagram is on one page");
            html.Should().NotContain("continues on the next page").And.NotContain("The flow at a glance, continued");
            html.Should().Contain("<svg").And.Contain("Workflow diagram");
        }
    }
    [Fact]
    public void The_prompt_tells_the_AI_to_make_a_step_for_every_page_and_to_ask_what_the_notes_leave_open()
    {
        var rules = File.ReadAllText(Path.Combine(TestData.PromptsDir, "common-rules.v2.md"));
        rules.Should().Contain("SEPARATE step for each").And.Contain("NEVER merge a list into one step").And.Contain("`gaps`").And.Contain("At most 10 questions")
             .And.Contain("flowchart TD").And.NotContain("development-ready");
        rules.Should().Contain("Roles, Teams, Rates", "a worked example shows the pattern");
    }
    // ------------------------------------------------------------------ the requirement gathering layout

    [Fact]
    public void The_document_follows_the_requirement_gathering_layout_then_the_flow_in_detail_then_sign_off()
    {
        var html = Complete();
        var order = new[] { "Document control", "Purpose", "Scope", "Functional requirements", "User roles and responsibilities", "Use case description", "The flow at a glance",
                            "How it works, stage by stage", "Screens and what they hold", "Where it touches the system", "Assumptions and open points", "Approval and sign-off" }
            .Select(h => html.IndexOf("</span>" + h + "</h2>", StringComparison.Ordinal)).ToList();
        order.Should().OnlyContain(i => i >= 0, "every section is there").And.BeInAscendingOrder();
    }

    [Fact]
    public void Functional_requirements_hold_the_rules_the_requirement_table_with_its_category_and_how_each_is_accepted()
    {
        var text = System.Net.WebUtility.HtmlDecode(Visible(Complete()));
        text.Should().Contain("What the solution must do and follow").And.Contain("never deleted").And.Contain("Req. ID").And.Contain("Requirement description").And.Contain("Category")
            .And.Contain("REQ001").And.Contain("New requirement").And.Contain("How it will be accepted");
    }

    [Fact]
    public void User_roles_are_a_table_with_the_role_what_they_do_how_often_the_module_and_the_menu()
    {
        var flow = FlowDiagramTests.BmwComplete();
        flow.ModuleCode = "BMW";
        var html = GuideHtmlBuilder.Build(Input(plan: GuidePlanBuilder.FromFlow(flow), flow: flow));
        var section = html[html.IndexOf("User roles and responsibilities", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("</section>", StringComparison.Ordinal)];

        section.Should().Contain("<th>User / role</th>").And.Contain("<th>Frequency</th>").And.Contain("<th>Module</th>").And.Contain("<th>Menu / screen</th>");
        Visible(section).Should().Contain("BMW Administrator").And.Contain("Occasional").And.Contain("BMW");
    }

    [Fact]
    public void The_use_case_description_lists_actors_trigger_conditions_priority_and_the_main_scenario_when_they_are_known()
    {
        var flow = FlowDiagramTests.BmwComplete();
        flow.UseCase = new UseCaseItem { PrimaryActors = ["BMW Administrator"], Priority = "High", MainScenario = "The administrator opens the masters page and maintains one master at a time." };
        flow.PreConditions.Add(new TextItem { Id = "PRE1", Text = "The administrator is signed in." });
        var text = Visible(GuideHtmlBuilder.Build(Input(plan: GuidePlanBuilder.FromFlow(flow), flow: flow)));

        text.Should().Contain("Use case description").And.Contain("Primary actor(s)").And.Contain("BMW Administrator").And.Contain("Trigger").And.Contain("Pre-conditions")
            .And.Contain("The administrator is signed in.").And.Contain("Priority").And.Contain("Main scenario").And.Contain("maintains one master at a time");
        flow.UseCase = new UseCaseItem();
        flow.Trigger = "";
        flow.PreConditions.Clear();
        Visible(GuideHtmlBuilder.Build(Input(plan: GuidePlanBuilder.FromFlow(flow), flow: flow))).Should().NotContain("Pre-conditions", "rows nobody filled in are left out");
    }

    [Fact]
    public void With_a_logo_every_page_carries_it_at_the_top_right_and_the_document_number_moves_to_the_middle_and_without_one_nothing_changes()
    {
        var withLogo = GuideHtmlBuilder.Build(Input(logo: [137, 80, 78, 71]));
        withLogo.Should().Contain("@top-right { content: url(\"data:image/svg+xml;utf8,").And.Contain("@top-center { content: \"IMPL-T-001\"").And.Contain("@top-center { content: none; }");
        var without = GuideHtmlBuilder.Build(Input());
        without.Should().Contain("@top-right { content: \"IMPL-T-001\"").And.NotContain("svg+xml");
    }
    [Fact]
    public void A_missing_diagram_is_said_and_a_demo_document_says_so_on_the_cover()
    {
        GuideHtmlBuilder.Build(Input() with { DiagramPng = null }).Should().Contain("The diagram could not be drawn.");
        GuideHtmlBuilder.Build(Input(demo: true)).Should().Contain("Demo document");
        GuideHtmlBuilder.Build(Input()).Should().NotContain("Demo document");
    }
}
