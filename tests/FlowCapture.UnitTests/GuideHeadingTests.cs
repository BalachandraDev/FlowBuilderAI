using System.Text.RegularExpressions;
using FlowCapture.Application.Documents;
using FlowCapture.Domain;
using FlowCapture.Shared.Content;
using FluentAssertions;
using Xunit;

namespace FlowCapture.UnitTests;

/// <summary>The person can give a heading of the document their own wording before the PDF is made.</summary>
public class GuideHeadingTests
{
    private static GuideInput Input(IReadOnlySet<string>? sections = null, IReadOnlyDictionary<string, string>? titles = null)
    {
        var content = FlowJson.Deserialize(TestData.Example().ToJsonString());
        return new GuideInput("IMPL-TH-001", "Test Hospital", "Bed allocation", "Ward", "Asha Lead", new DateTime(2026, 11, 19), 2, content, GuidePlanBuilder.FromFlow(content), null,
            [new GuideOpenQuestion("Q1", "Which ward is pre-selected?", "Open")], false, Diagram: FlowDiagramBuilder.Build(content)) { RequestCode = "NR-001", Sections = sections, SectionTitles = titles };
    }

    private static string Headings(string html) => string.Join("|", Regex.Matches(html, @"<h2[^>]*><span>(\d+)</span>([^<]+)</h2>").Select(m => m.Groups[1].Value + ":" + m.Groups[2].Value));
    private static IReadOnlyDictionary<string, string> Titles(params (string Key, string Title)[] t) => t.ToDictionary(x => x.Key, x => x.Title);

    [Fact]
    public void A_renamed_heading_is_printed_under_its_new_wording_and_the_numbers_do_not_change()
    {
        var standard = Headings(GuideHtmlBuilder.Build(Input()));
        var renamed = Headings(GuideHtmlBuilder.Build(Input(titles: Titles(("purpose", "Why we are doing this"), ("stages", "Step by step")))));

        standard.Should().Contain("Purpose").And.Contain("How it works, stage by stage");
        renamed.Should().Contain("Why we are doing this").And.Contain("Step by step");
        renamed.Should().NotContain("|2:Purpose|").And.NotContain("How it works, stage by stage");
        renamed.Replace("Why we are doing this", "Purpose").Replace("Step by step", "How it works, stage by stage").Should().Be(standard, "only the words change, never the order or the numbers");
    }

    [Fact]
    public void Every_section_that_can_be_renamed_is_renamed_in_the_page()
    {
        var all = GuideSections.All.ToDictionary(s => s.Key, s => "Mine: " + s.Title);
        var html = GuideHtmlBuilder.Build(Input(titles: all));
        var headings = Headings(html);
        foreach (var s in GuideHtmlBuilder.Outline(Input(), standalone: true))
            headings.Should().Contain("Mine: " + s.Title, $"the {s.Key} heading is renamed");
    }

    [Fact]
    public void A_new_wording_is_written_safely_and_the_outline_still_shows_the_standard_headings()
    {
        var input = Input(titles: Titles(("scope", "<script>alert(1)</script> & \"Scope\"")));
        var html = GuideHtmlBuilder.Build(input);
        html.Should().NotContain("<script>alert").And.Contain("&lt;script&gt;alert(1)&lt;/script&gt;");
        html.Should().Contain("&amp; &quot;Scope&quot;");
        GuideHtmlBuilder.Outline(input, standalone: true).Select(s => s.Title).Should().Contain("Scope", "the outline always shows the standard heading, so the person can see what they renamed")
            .And.NotContain(t => t.Contains("<script"));
    }

    [Fact]
    public void A_heading_that_is_not_printed_needs_no_new_name_and_the_others_keep_their_numbers()
    {
        var html = GuideHtmlBuilder.Build(Input(Only("purpose", "stages"), Titles(("purpose", "Aim"), ("control", "Not printed"))));
        Headings(html).Should().Be("1:Aim|2:How it works, stage by stage");
        html.Should().NotContain("Not printed");
    }

    [Fact]
    public void Titles_are_checked_tidied_and_the_unchanged_ones_dropped()
    {
        GuideSections.ParseTitles(null).Should().BeNull();
        GuideSections.ParseTitles(new Dictionary<string, string>()).Should().BeNull();
        GuideSections.ParseTitles(Titles(("purpose", "Purpose"), ("scope", "   "))).Should().BeNull("the standard wording, or nothing, is no change");
        GuideSections.ParseTitles(Titles(("purpose", "  What   this\n is for  "))).Should().Equal(new Dictionary<string, string> { ["purpose"] = "What this is for" });

        var unknown = () => GuideSections.ParseTitles(Titles(("nonsense", "x")));
        unknown.Should().Throw<DomainException>().Which.Code.Should().Be("unknown_section");
        var tooLong = () => GuideSections.ParseTitles(Titles(("purpose", new string('x', GuideSections.MaxTitleLength + 1))));
        tooLong.Should().Throw<DomainException>().Which.Code.Should().Be("heading_too_long");
        GuideSections.ParseTitles(Titles(("purpose", new string('x', GuideSections.MaxTitleLength)))).Should().ContainKey("purpose");
    }

    [Fact]
    public void Titles_stored_with_a_job_are_read_back_and_a_damaged_value_is_not_trusted()
    {
        GuideSections.ReadTitles(null).Should().BeNull();
        GuideSections.ReadTitles("").Should().BeNull();
        GuideSections.ReadTitles("{\"purpose\":\"Aim\"}").Should().Equal(new Dictionary<string, string> { ["purpose"] = "Aim" });
        var damaged = () => GuideSections.ReadTitles("{\"nonsense\":\"x\"}");
        damaged.Should().Throw<DomainException>();
    }

    [Fact]
    public void A_chapter_of_a_bigger_document_uses_the_renamed_headings_too()
    {
        var input = Input(titles: Titles(("purpose", "Aim of the request")));
        var html = GuideHtmlBuilder.BuildCombined(new CombinedGuideInput("IMPL-TH-FULL", "Test Hospital: whole project", "Test Hospital", "Client", null, "Asha Lead", new DateTime(2026, 11, 19),
            [new CombinedModule("Ward", [new CombinedRequest("r-1", "NR-001", "Bed allocation", input with { RequestCode = "NR-001" })])], false));
        Headings(html).Should().Contain("Aim of the request");
    }

    private static IReadOnlySet<string> Only(params string[] keys) => keys.ToHashSet();
}
