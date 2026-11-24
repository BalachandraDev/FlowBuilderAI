using System.Text.RegularExpressions;
using FlowCapture.Application.Documents;
using FlowCapture.Domain;
using FlowCapture.Shared.Content;
using FluentAssertions;
using Xunit;

namespace FlowCapture.UnitTests;

/// <summary>The person chooses which headings go into the PDF; only those are written, numbered 1, 2, 3 without gaps.</summary>
public class GuideSectionChoiceTests
{
    private static GuideInput Input(IReadOnlySet<string>? sections = null)
    {
        var content = FlowJson.Deserialize(TestData.Example().ToJsonString());
        return new GuideInput("IMPL-TH-001", "Test Hospital", "Bed allocation", "Ward", "Asha Lead", new DateTime(2026, 11, 19), 2, content, GuidePlanBuilder.FromFlow(content), null,
            [new GuideOpenQuestion("Q1", "Which ward is pre-selected?", "Open")], false, Diagram: FlowDiagramBuilder.Build(content)) { RequestCode = "NR-001", Sections = sections };
    }

    private static IReadOnlySet<string> Only(params string[] keys) => keys.ToHashSet();
    private static string Headings(string html) => string.Join("|", Regex.Matches(html, @"<h2[^>]*><span>(\d+)</span>([^<]+)</h2>").Select(m => m.Groups[1].Value + ":" + m.Groups[2].Value));

    [Fact]
    public void The_outline_lists_the_headings_that_have_something_under_them_with_what_is_in_each()
    {
        var outline = GuideHtmlBuilder.Outline(Input(), standalone: true);

        outline.Select(s => s.Key).Should().StartWith("control", "purpose").And.EndWith("signoff");
        outline.Should().OnlyContain(s => s.Title.Length > 0 && s.Detail.Length > 0, "every heading says what is under it");
        outline.Select(s => s.Key).Should().OnlyHaveUniqueItems().And.OnlyContain(k => GuideSections.IsKnown(k));
        // what the outline lists is exactly what is printed when nothing is switched off
        var printed = Regex.Matches(GuideHtmlBuilder.Build(Input()), @"<h2[^>]*><span>\d+</span>([^<]+)</h2>").Select(m => m.Groups[1].Value).ToList();
        outline.Select(s => s.Title).Should().Equal(printed);
    }

    [Fact]
    public void Each_heading_comes_with_the_page_markup_that_is_printed_so_the_preview_is_the_document_itself()
    {
        var input = Input();
        var outline = GuideHtmlBuilder.Outline(input, standalone: true);

        outline.Should().OnlyContain(s => s.Html.Contains("<h2") && s.Html.Contains('\u0001'), "every section has its own heading, with a marker where its number goes");
        outline.Should().OnlyContain(s => !s.Html.Contains("<script") && !s.Html.Contains(" onerror") && !s.Html.Contains("javascript:"));
        outline.First(s => s.Key == "control").Html.Should().Contain("IMPL-TH-001").And.Contain("Test Hospital");
        outline.First(s => s.Key == "requirements").Html.Should().Contain("<table");
        outline.First(s => s.Key == "diagram").Html.Should().Contain("<svg", "the whole flow picture is shown");

        // what the preview holds is what the printed page holds: every section's markup (with its number put in) is inside the printed document
        var printed = GuideHtmlBuilder.Build(input);
        var number = 0;
        foreach (var s in outline) printed.Should().Contain(s.Html.Replace("\u0001", (++number).ToString()), $"the {s.Title} section is printed exactly as previewed");
    }

    [Fact]
    public void The_preview_of_a_big_document_leaves_out_pictures_and_the_flow_drawing_to_stay_small()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var step = FlowJson.Deserialize(TestData.Example().ToJsonString()).Steps[0].Id;
        var input = Input() with { Figures = [new GuideFigure(1, "Bed screen", step, "image/png", png)] };

        var full = GuideHtmlBuilder.Outline(input, standalone: false);
        var slim = GuideHtmlBuilder.Outline(input, standalone: false, slim: true);

        full.First(s => s.Key == "stages").Html.Should().Contain("data:image/png");
        slim.First(s => s.Key == "stages").Html.Should().NotContain("data:image").And.Contain("Bed screen", "the caption stays");
        slim.First(s => s.Key == "diagram").Html.Should().NotContain("<svg").And.Contain("The flow at a glance");
        slim.Select(s => s.Key).Should().Equal(full.Select(s => s.Key));
        GuideHtmlBuilder.PreviewStyle().Should().Contain(".paper").And.Contain(".stage");
        GuideHtmlBuilder.PreviewCover(input).Should().Contain("Implementation Document").And.Contain("Test Hospital");
    }
    [Fact]
    public void A_chapter_of_a_bigger_document_never_offers_document_control_or_sign_off()
    {
        var keys = GuideHtmlBuilder.Outline(Input(), standalone: false).Select(s => s.Key).ToList();

        keys.Should().NotContain(["control", "signoff"]).And.Contain("purpose");
        GuideSections.For(ownDocument: false).Should().NotContain(["control", "signoff"]);
        GuideSections.For(ownDocument: true).Should().Contain(["control", "signoff"]);
    }

    [Fact]
    public void Only_the_chosen_sections_are_printed_and_numbered_without_gaps()
    {
        var html = GuideHtmlBuilder.Build(Input(Only("purpose", "stages", "open")));

        Headings(html).Should().Be("1:Purpose|2:How it works, stage by stage|3:Assumptions and open points");
        html.Should().NotContain("Document control").And.NotContain("Approval and sign-off").And.NotContain("Where it touches the system").And.NotContain("The flow at a glance");
        html.Should().Contain("Asha Lead", "the cover is always there");
    }

    [Fact]
    public void Leaving_out_a_section_does_not_remove_the_others_and_choosing_everything_is_the_same_as_choosing_nothing()
    {
        var all = GuideSections.All.Select(s => s.Key).ToHashSet();
        Headings(GuideHtmlBuilder.Build(Input(all))).Should().Be(Headings(GuideHtmlBuilder.Build(Input())));

        var without = Headings(GuideHtmlBuilder.Build(Input(all.Where(k => k != "touchpoints").ToHashSet())));
        without.Should().NotContain("Where it touches the system").And.Contain("Purpose").And.Contain("Approval and sign-off");
        Regex.Matches(without, @"(\d+):").Select(m => int.Parse(m.Groups[1].Value)).Should().Equal(Enumerable.Range(1, Regex.Matches(without, @"(\d+):").Count));
    }

    [Fact]
    public void A_picture_that_belonged_to_a_left_out_stage_section_is_still_printed_with_the_attachments()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var content = FlowJson.Deserialize(TestData.Example().ToJsonString());
        var step = content.Steps[0].Id;
        var withFigure = Input() with { Figures = [new GuideFigure(1, "Bed screen", step, "image/png", png)] };

        var printedWithStep = GuideHtmlBuilder.Build(withFigure);
        printedWithStep.Should().Contain("Bed screen").And.NotContain("Attached pictures", "it is shown under its step");

        var noStages = GuideHtmlBuilder.Build(withFigure with { Sections = Only("purpose", "pictures") });
        Headings(noStages).Should().Be("1:Purpose|2:Attached pictures");
        noStages.Should().Contain("Bed screen");
    }

    [Fact]
    public void A_chosen_list_is_checked_an_unknown_section_and_an_empty_choice_are_refused_and_nothing_sent_means_everything()
    {
        GuideSections.Parse(null).Should().BeNull();
        GuideSections.Parse(["purpose", " scope "]).Should().BeEquivalentTo(["purpose", "scope"]);
        ((Action)(() => GuideSections.Parse(["purpose", "nonsense"]))).Should().Throw<DomainException>().Which.Code.Should().Be("unknown_section");
        ((Action)(() => GuideSections.Parse([]))).Should().Throw<DomainException>().Which.Code.Should().Be("nothing_chosen");
        ((Action)(() => GuideSections.Parse([" ", ""]))).Should().Throw<DomainException>().Which.Code.Should().Be("nothing_chosen");
    }

    [Fact]
    public void In_a_combined_document_every_chapter_keeps_only_the_chosen_sections()
    {
        var request = Input(Only("purpose", "open"));
        var doc = new CombinedGuideInput("IMPL-TH-FULL", "Test Hospital: whole project", "Test Hospital", null, null, "Asha Lead", new DateTime(2026, 11, 19),
            [new CombinedModule("Ward", [new CombinedRequest("r-aaa", "NR-001", "Bed allocation", request), new CombinedRequest("r-bbb", "NR-002", "Visitor pass", request)])], false);

        var html = GuideHtmlBuilder.BuildCombined(doc);

        Regex.Matches(html, @"<h2[^>]*><span>1</span>Purpose").Count.Should().Be(2);
        Regex.Matches(html, @"<h2[^>]*><span>2</span>Assumptions and open points").Count.Should().Be(2);
        html.Should().NotContain("How it works, stage by stage").And.NotContain("Where it touches the system");
    }
}
