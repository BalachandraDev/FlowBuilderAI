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

    // ---- the text under a heading --------------------------------------------------------------------------------------

    private static GuideInput WithText(IReadOnlySet<string>? sections, IReadOnlyDictionary<string, string>? texts, IReadOnlyDictionary<string, string>? titles = null) =>
        Input(sections, titles) with { SectionTexts = texts };
    private static IReadOnlyDictionary<string, string> Texts(params (string Key, string Text)[] t) => t.ToDictionary(x => x.Key, x => x.Text);

    [Fact]
    public void Text_the_person_wrote_is_printed_instead_of_the_generated_text_under_the_same_heading_and_number()
    {
        var generated = GuideHtmlBuilder.Build(Input(Only("purpose", "scope")));
        var html = GuideHtmlBuilder.Build(WithText(Only("purpose", "scope"), Texts(("purpose", "We want one bed screen for every ward.\n- Faster allocation\n- Fewer phone calls\nSigned off by the CMO."))));

        Headings(html).Should().Be(Headings(generated), "only the text under the heading changes");
        html.Should().Contain("<p>We want one bed screen for every ward.</p><ul><li>Faster allocation</li><li>Fewer phone calls</li></ul><p>Signed off by the CMO.</p>");
        var purpose = Regex.Match(html, @"<h2[^>]*><span>1</span>Purpose</h2>.*?</section>", RegexOptions.Singleline).Value;
        purpose.Should().NotBe(Regex.Match(generated, @"<h2[^>]*><span>1</span>Purpose</h2>.*?</section>", RegexOptions.Singleline).Value);
        html.Should().Contain("Scope", "a section with no text of the person's own is still the generated one");
    }

    [Fact]
    public void Written_text_and_a_renamed_heading_work_together_and_the_text_is_written_safely()
    {
        var html = GuideHtmlBuilder.Build(WithText(Only("purpose"), Texts(("purpose", "<script>alert(1)</script> & \"quoted\"")), Titles(("purpose", "Aim"))));
        Headings(html).Should().Be("1:Aim");
        html.Should().NotContain("<script>alert").And.Contain("&lt;script&gt;alert(1)&lt;/script&gt; &amp; &quot;quoted&quot;");
    }

    [Fact]
    public void Text_for_a_section_that_is_not_printed_is_ignored_and_the_outline_always_shows_the_generated_text()
    {
        var input = WithText(Only("scope"), Texts(("purpose", "Never printed")));
        GuideHtmlBuilder.Build(input).Should().NotContain("Never printed");
        var outline = GuideHtmlBuilder.Outline(WithText(null, Texts(("purpose", "Written by the person"))), standalone: true);
        outline.Single(s => s.Key == "purpose").Html.Should().NotContain("Written by the person");
    }

    [Fact]
    public void Written_text_is_checked_tidied_and_empty_text_dropped()
    {
        GuideSections.ParseTexts(null).Should().BeNull();
        GuideSections.ParseTexts(Texts(("purpose", "  \n  "))).Should().BeNull("empty text keeps the generated text");
        GuideSections.ParseTexts(Texts(("purpose", "  First   line \r\n\r\n\r\n- a  bullet\u0007 \n"))).Should().Equal(new Dictionary<string, string> { ["purpose"] = "First line\n- a bullet" });

        var request = Guid.NewGuid();
        GuideSections.ParseTexts(Texts(($"{request:N}/scope", "Mine"))).Should().Equal(new Dictionary<string, string> { [$"{request:D}/scope"] = "Mine" }, "a request in a bigger document is named in the key");

        foreach (var (key, code) in new[] { ("nonsense", "unknown_section"), ("signoff", "section_not_editable"), ("diagram", "section_not_editable"), ("control", "section_not_editable"), ("not-a-guid/purpose", "unknown_section") })
        {
            var bad = () => GuideSections.ParseTexts(Texts((key, "x")));
            bad.Should().Throw<DomainException>($"'{key}' cannot be written").Which.Code.Should().Be(code);
        }
        var tooLong = () => GuideSections.ParseTexts(Texts(("purpose", new string('x', GuideSections.MaxTextLength + 1))));
        tooLong.Should().Throw<DomainException>().Which.Code.Should().Be("text_too_long");
        GuideSections.ParseTexts(Texts(("purpose", new string('x', GuideSections.MaxTextLength)))).Should().ContainKey("purpose");
    }

    [Fact]
    public void Text_stored_with_a_job_is_read_back_and_picked_out_for_each_request()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var stored = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { [$"{a:D}/purpose"] = "For A", [$"{b:D}/purpose"] = "For B", [$"{a:D}/scope"] = "Scope of A" });
        var all = GuideSections.ReadTexts(stored);
        GuideSections.TextsOf(all, a).Should().Equal(new Dictionary<string, string> { ["purpose"] = "For A", ["scope"] = "Scope of A" });
        GuideSections.TextsOf(all, b).Should().Equal(new Dictionary<string, string> { ["purpose"] = "For B" });
        GuideSections.TextsOf(all, Guid.NewGuid()).Should().BeNull();
        GuideSections.TextsOf(all, null).Should().BeNull("a request's own document has no requests in front of its keys");
        GuideSections.TextsOf(GuideSections.ReadTexts("{\"purpose\":\"Mine\"}"), null).Should().Equal(new Dictionary<string, string> { ["purpose"] = "Mine" });
        GuideSections.ReadTexts(null).Should().BeNull();
        var damaged = () => GuideSections.ReadTexts("{\"signoff\":\"x\"}");
        damaged.Should().Throw<DomainException>();
    }

    [Fact]
    public void Each_request_of_a_bigger_document_prints_its_own_text()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var texts = GuideSections.ParseTexts(new Dictionary<string, string> { [$"{a:D}/purpose"] = "Words for request A", [$"{b:D}/purpose"] = "Words for request B" });
        var html = GuideHtmlBuilder.BuildCombined(new CombinedGuideInput("IMPL-TH-FULL", "Test Hospital: whole project", "Test Hospital", "Client", null, "Asha Lead", new DateTime(2026, 11, 19),
            [new CombinedModule("Ward", [
                new CombinedRequest("r-a", "NR-001", "Bed allocation", Input(Only("purpose")) with { SectionTexts = GuideSections.TextsOf(texts, a) }),
                new CombinedRequest("r-b", "NR-002", "Bed transfer", Input(Only("purpose")) with { SectionTexts = GuideSections.TextsOf(texts, b) })])], false));
        html.Should().Contain("Words for request A").And.Contain("Words for request B");
        html.IndexOf("Words for request A", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("Words for request B", StringComparison.Ordinal));
    }
}
