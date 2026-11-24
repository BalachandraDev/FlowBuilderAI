using FlowCapture.Application.Documents;
using FlowCapture.Shared.Content;
using FluentAssertions;
using Xunit;

namespace FlowCapture.UnitTests;

public class DocumentBuilderTests
{
    private static FlowContent Example() => FlowJson.Deserialize(TestData.Example().ToJsonString());

    private static FlowDocumentInput Input(FlowContent? content = null, IReadOnlyList<OpenIssue>? issues = null, ImageBlock? diagram = null) => new(
        content ?? Example(), "MRD-003", "MRD", "Palavesaraj P", new DateTime(2025, 3, 18, 0, 0, 0, DateTimeKind.Utc), "Test Hospital", "Asha Lead",
        3, new DateTime(2025, 3, 17, 0, 0, 0, DateTimeKind.Utc), issues ?? [], diagram);

    private static ImageBlock Diagram() => new([1, 2, 3], 400, 300, "Figure 1: Main flow");

    [Fact]
    public void The_cover_carries_the_number_module_assignee_and_a_dd_MM_yyyy_date()
    {
        var doc = RgdDocumentBuilder.Build(Input());
        doc.Cover.Should().Be(new RgdCover("MRD-003", "MRD", "Palavesaraj P", "18-03-2025"));
        doc.Title.Should().Be("Default Department/Ward Selection in IP Case Sheet Receive Menu");
    }

    [Fact]
    public void The_sections_follow_the_RGD_template_order_and_then_the_development_sections()
    {
        var doc = RgdDocumentBuilder.Build(Input(diagram: Diagram()));
        var headings = doc.Blocks.OfType<HeadingBlock>().Select(h => (h.Level, h.Text)).ToList();

        headings.Select(h => h.Text).Should().ContainInOrder(
            "Functional Requirement Document", "1. Purpose", "2. Scope", "3. Functional Requirement", "Requirements",
            "User Roles and Responsibilities", "Use Case Description", "4. Flow diagram", "5. Detailed flow", "Main flow", "Decisions",
            "Alternate flows", "Exception flows", "6. Business rules and validations", "7. Screens and fields",
            "8. Data entities and suggested table changes", "9. Integration points", "10. Reports and prints", "11. Non-functional notes",
            "12. Acceptance criteria and test scenarios", "13. Open issues and clarifications", "14. Sign-off");
        headings.Single(h => h.Text == "1. Purpose").Level.Should().Be(1);
        headings.Single(h => h.Text == "Requirements").Level.Should().Be(2);
    }

    [Fact]
    public void The_requirement_user_role_and_use_case_tables_match_the_sample_RGD_layout()
    {
        var doc = RgdDocumentBuilder.Build(Input());
        var tables = doc.Blocks.OfType<TableBlock>().ToList();

        tables[0].Header.Should().Equal("Req. ID", "Requirements Description", "Category");
        tables[0].Rows.Single()[0].Should().Be("REQ001");
        tables[0].Rows.Single()[2].Should().Be("CR");

        tables[1].Header.Should().Equal("User/Role", "Frequency", "Module", "Menu Name");
        tables[1].Rows.Single().Should().Equal("All MRD users", "Frequent", "MRD", "IP Case Sheet Receive in Department");

        var useCase = tables[2];
        useCase.Header.Should().BeEmpty();
        useCase.Rows.Select(r => r[0]).Should().Equal("Primary Actor(s)", "Trigger", "Pre-conditions", "Post-conditions", "Priority", "Main Scenario");
        useCase.Rows[1][1].Should().Contain("opens the IP Case Sheet Receive in Department menu");       // the sample left this blank
        useCase.Rows[2][1].Should().Contain("• ").And.Contain("user mapping");                            // the sample said "Nill"
        useCase.Rows[4][1].Should().Be("High");
    }

    [Fact]
    public void The_detailed_flow_uses_step_numbers_not_internal_ids()
    {
        var doc = RgdDocumentBuilder.Build(Input());
        var steps = doc.Blocks.OfType<TableBlock>().First(t => t.Header.Contains("Screen / menu"));
        steps.Rows.Should().HaveCount(5);
        steps.Rows[0][6].Should().Be("Decision D1");
        steps.Rows[1][6].Should().Be("Step 4");
        steps.Rows[4][6].Should().Be("End of flow");

        var decisions = doc.Blocks.OfType<TableBlock>().First(t => t.Header.Contains("If yes"));
        decisions.Rows.Single()[2].Should().Contain("go to Step 2");
        decisions.Rows.Single()[3].Should().Contain("go to Step 3");
        var alternate = doc.Blocks.OfType<TableBlock>().First(t => t.Header.Contains("Branches from"));
        alternate.Rows.Single()[3].Should().Be("Step 2");
        alternate.Rows.Single()[5].Should().Be("Step 4");
    }

    [Fact]
    public void Nothing_in_the_document_is_ever_blank_for_the_example()
    {
        RgdDocumentBuilder.Build(Input(diagram: Diagram())).FindBlankText().Should().BeEmpty();
    }

    [Fact]
    public void An_almost_empty_flow_still_produces_a_complete_document_with_not_applicable_and_to_be_confirmed()
    {
        var c = new FlowContent
        {
            FlowTitle = "Bare flow", Department = "MRD", ModuleCode = "MRD", Purpose = "To be confirmed", Scope = "To be confirmed", Trigger = "To be confirmed",
            Steps = [new StepItem { Id = "S1", Actor = "To be confirmed", Action = "To be confirmed", Screen = "To be confirmed", Next = ["END"] }],
            UseCase = new UseCaseItem { Priority = "To be confirmed", MainScenario = "To be confirmed" },
        };

        var doc = RgdDocumentBuilder.Build(Input(c));

        doc.FindBlankText().Should().BeEmpty();
        var text = doc.PlainText();
        text.Should().Contain("Not applicable").And.Contain("To be confirmed");
        doc.Blocks.OfType<ParagraphBlock>().Count(p => p.Text == "Not applicable").Should().BeGreaterThan(4);   // decisions, flows, rules, ...
        var open = doc.Blocks.OfType<HeadingBlock>().Single(h => h.Text.Contains("Open issues"));
        doc.Blocks[doc.Blocks.ToList().IndexOf(open) + 1].Should().Be(new ParagraphBlock("None"));            // "None" is only used here
    }

    [Fact]
    public void Pre_conditions_say_to_be_confirmed_when_a_question_about_them_is_open_and_not_applicable_otherwise()
    {
        var c = Example();
        c.PreConditions = [];
        var withoutQuestion = RgdDocumentBuilder.Build(Input(c)).Blocks.OfType<TableBlock>().First(t => t.Header.Count == 0).Rows.Single(r => r[0] == "Pre-conditions");
        withoutQuestion[1].Should().Be("Not applicable");

        var withQuestion = RgdDocumentBuilder.Build(Input(c, [new OpenIssue("Q4", "conditions", "What must be true before the menu is opened?")]))
            .Blocks.OfType<TableBlock>().First(t => t.Header.Count == 0).Rows.Single(r => r[0] == "Pre-conditions");
        withQuestion[1].Should().Be("To be confirmed");
    }

    [Fact]
    public void Open_questions_and_gaps_without_a_question_are_listed_with_their_status()
    {
        var c = Example();
        c.Gaps.Add(new GapItem { Id = "G9", Section = "validations", Description = "No validation was described for the ward." });
        var doc = RgdDocumentBuilder.Build(Input(c, [new OpenIssue("Q1", "businessRules", "Which ward is pre-selected?")]));

        var table = doc.Blocks.OfType<TableBlock>().Single(t => t.Header.Contains("Question or gap"));
        table.Rows.Should().Contain(r => r[0] == "Q1" && r[1] == "Business rules" && r[3] == "Open");
        table.Rows.Should().Contain(r => r[0] == "G9" && r[2].Contains("No validation"));
        table.Rows.Select(r => r[0]).Should().NotContain("G1");          // G1 has question Q1 in the content, so it is not repeated
    }

    [Fact]
    public void The_sign_off_table_has_prepared_reviewed_and_approved_rows_with_room_to_sign()
    {
        var table = RgdDocumentBuilder.Build(Input()).Blocks.OfType<TableBlock>().Single(t => t.Header.Contains("Signature"));
        table.Rows.Select(r => r[0]).Should().Equal("Prepared by", "Reviewed by", "Approved by");
        table.Rows[0][1].Should().Be("Asha Lead");
        table.Rows.Should().OnlyContain(r => r[2].StartsWith("____"));
    }

    [Fact]
    public void Leaving_the_diagram_out_leaves_the_numbering_continuous()
    {
        var headings = RgdDocumentBuilder.Build(Input()).Blocks.OfType<HeadingBlock>().Where(h => h.Level == 1).Select(h => h.Text).ToList();
        headings.Should().NotContain(h => h.Contains("Flow diagram"));
        headings.Should().ContainInOrder("3. Functional Requirement", "4. Detailed flow", "5. Business rules and validations");
    }

    [Fact]
    public void Blank_text_is_found_wherever_it_hides()
    {
        var doc = new RgdDocument(new RgdCover("X-001", "X", " ", "01-01-2026"), "T", [
            new HeadingBlock(1, ""), new ParagraphBlock("ok"), new BulletsBlock(["a", "  "]),
            new TableBlock(["H"], [["v", ""]], [1]), new TableBlock(["H"], [], [1]),
        ]);
        var problems = doc.FindBlankText();
        problems.Should().Contain(["cover.assignedBy", "block[0] heading", "block[2] bullet 1", "block[3] table row 0 cell 1", "block[4] table has no rows"]);
    }

    // ------------------------------------------------------------------ project document

    private static ChapterInput Chapter(string number, string module, string dept, string menu)
    {
        var c = Example();
        c.FlowTitle = menu;
        return new ChapterInput(Input(c) with { DocNumber = number, ModuleCode = module }, dept, menu);
    }

    [Fact]
    public void The_project_document_has_an_overview_then_one_chapter_per_flow_with_chapter_numbered_sections()
    {
        var doc = ProjectDocumentBuilder.Build(new ProjectDocumentInput("PRJ-KMC-C01", "Kauvery", "KMC", "Chennai", "Asha Lead", DateTime.UtcNow, "Asha Lead",
            [Chapter("MRD-001", "MRD", "MRD", "Case sheet receive"), Chapter("BILLING-001", "BILLING", "Billing", "Final bill")], Diagram(),
            [new HandoffRow("Case sheet receive", "Step 5", "Final bill", "Step 1", "Case sheets go to billing", "Case sheet")]));

        doc.Cover.Module.Should().Be("All modules");
        doc.FindBlankText().Should().BeEmpty();
        var headings = doc.Blocks.OfType<HeadingBlock>().Select(h => h.Text).ToList();
        headings.Should().ContainInOrder("Consolidated Requirement Gathering Document", "Contents", "Cross-department overview", "Hand-offs between departments",
            "Chapter 1. MRD-001: Case sheet receive", "1.1 Purpose", "1.2 Scope");
        headings.Should().Contain("Chapter 2. BILLING-001: Final bill").And.Contain("2.1 Purpose");
        doc.Blocks.OfType<PageBreakBlock>().Should().HaveCount(2);
        doc.Blocks.OfType<TableBlock>().First().Rows.Single()[0].Should().Be("Case sheet receive, Step 5");
    }

    [Fact]
    public void The_project_document_says_so_when_no_hand_offs_were_accepted_and_refuses_to_be_empty()
    {
        var doc = ProjectDocumentBuilder.Build(new ProjectDocumentInput("PRJ-KMC-C01", "Kauvery", "KMC", "", "Lead", DateTime.UtcNow, "Lead",
            [Chapter("MRD-001", "MRD", "MRD", "Receive")], null, []));
        doc.Blocks.OfType<ParagraphBlock>().Should().Contain(p => p.Text.StartsWith("No hand-offs"));
        doc.FindBlankText().Should().BeEmpty();

        FluentActions.Invoking(() => ProjectDocumentBuilder.Build(new ProjectDocumentInput("PRJ-X-C01", "H", "X", "", "L", DateTime.UtcNow, "L", [], null, [])))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void The_combined_diagram_has_a_box_per_department_and_flow_and_dashed_arrows_for_hand_offs()
    {
        var a = Example();
        var b = Example();
        var mermaid = CombinedMermaidBuilder.Build(
            [new(1, "MRD", "MRD", "Receive", a), new(2, "BILLING", "Billing", "Final bill", b)],
            [new(1, "S5", 2, "S1", "Case sheet")]);

        mermaid.Should().StartWith("flowchart TD");
        mermaid.Should().Contain("subgraph DEP_MRD[\"MRD\"]").And.Contain("subgraph DEP_BILLING[\"Billing\"]");
        mermaid.Should().Contain("subgraph FL1[\"Receive\"]").And.Contain("subgraph FL2[\"Final bill\"]");
        mermaid.Should().Contain("F1_S1[\"1.").And.Contain("F2_D1{");
        mermaid.Should().Contain("F1_D1 -->|Yes| F1_S2");
        mermaid.Should().Contain("F1_S5 -.->|\"Case sheet\"| F2_S1");
        mermaid.Should().Contain("F1_END([\"End\"])");
        var opens = mermaid.Split('\n').Count(l => l.Trim().StartsWith("subgraph "));
        var ends = mermaid.Split('\n').Count(l => l.Trim() == "end");
        opens.Should().Be(ends);
    }
}
