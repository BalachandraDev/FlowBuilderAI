using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlowCapture.Application.Documents;
using FlowCapture.Shared.Content;
using FluentAssertions;
using Xunit;

namespace FlowCapture.UnitTests;

/// <summary>The PDF of a whole project or of one module: one cover, a contents page, and every analysed request as a chapter.</summary>
public class CombinedGuideHtmlTests
{
    private static GuideInput Request(string title, string module, string code) => new(
        "", "Test Hospital", title, module, "Asha Lead", new DateTime(2026, 11, 19), 2,
        FlowJson.Deserialize(TestData.Example().ToJsonString()), GuidePlanBuilder.FromFlow(FlowJson.Deserialize(TestData.Example().ToJsonString())), null,
        [new GuideOpenQuestion("Q1", "Which ward is pre-selected?", "Open")], false)
    { RequestCode = code };

    private static CombinedGuideInput Project(bool demo = false, byte[]? logo = null) => new("IMPL-TH-FULL", "Test Hospital: whole project", "Test Hospital", "Kauvery Group", null, "Asha Lead", new DateTime(2026, 11, 19),
    [
        new CombinedModule("Ward",
        [
            new CombinedRequest("r-aaa", "NR-001", "Bed allocation", Request("Bed allocation", "Ward", "NR-001")),
            new CombinedRequest("r-bbb", "NR-002", "Visitor pass", null),                                                // not analysed yet
            new CombinedRequest("r-ccc", "CR-001", "Discharge print", Request("Discharge print", "Ward", "CR-001")),
        ]),
        new CombinedModule("Pharmacy", [new CombinedRequest("r-ddd", "NR-001", "Returns", Request("Returns", "Pharmacy", "NR-001"))]),
    ], demo, logo);

    private static string Visible(string html) => Regex.Replace(Regex.Replace(html, "<style>.*?</style>", "", RegexOptions.Singleline), "<[^>]+>", " ");

    [Fact]
    public void One_cover_one_contents_page_and_a_chapter_for_each_analysed_request()
    {
        var html = GuideHtmlBuilder.BuildCombined(Project());
        Regex.Matches(html, "class=\"cover\"").Count.Should().Be(1, "a combined document has one cover, not one per request");
        Regex.Matches(html, "<section class=\"chapter\"").Count.Should().Be(3, "three requests are analysed");
        var text = Visible(html);
        foreach (var s in new[] { "IMPL-TH-FULL", "Kauvery Group", "Test Hospital", "Requests included", "Contents", "Ward", "Pharmacy", "Bed allocation", "Discharge print", "Returns" })
            text.Should().Contain(s);
        html.IndexOf("class=\"cover\"", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("Contents", StringComparison.Ordinal));
        html.IndexOf("Contents", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("class=\"chapter\"", StringComparison.Ordinal));
        Regex.Match(html, @"Requests included</td><td>(\d+)</td>").Groups[1].Value.Should().Be("3");
        Regex.Match(html, @"Modules</td><td>(\d+)</td>").Groups[1].Value.Should().Be("2");
    }

    [Fact]
    public void A_request_without_an_analysis_is_listed_but_not_printed_and_the_contents_link_to_the_chapters()
    {
        var html = GuideHtmlBuilder.BuildCombined(Project());
        var toc = html[html.IndexOf("class=\"toc\"", StringComparison.Ordinal)..html.IndexOf("class=\"chapter\"", StringComparison.Ordinal)];
        toc.Should().Contain("Visitor pass").And.Contain("Not analysed yet, so not included");
        html.Should().NotContain("id=\"r-bbb\"");
        foreach (var id in new[] { "r-aaa", "r-ccc", "r-ddd" })
        {
            toc.Should().Contain($"href=\"#{id}\"");
            html.Should().Contain($"id=\"{id}\"");
        }
    }

    [Fact]
    public void A_chapter_has_the_sections_of_the_request_but_not_its_own_cover_document_control_or_sign_off()
    {
        var html = GuideHtmlBuilder.BuildCombined(Project());
        html.Should().Contain("Purpose").And.Contain("Where it touches the system");
        html.Should().NotContain("Document control").And.NotContain("Approval and sign-off", "those belong to the whole document, not to each chapter");
        var chapter = html[html.IndexOf("id=\"r-aaa\"", StringComparison.Ordinal)..];
        chapter.Should().Contain("NR-001").And.Contain("Bed allocation").And.Contain("Ward");
        // the numbers of the sections start again at 1 in each chapter
        Regex.Matches(html, @"<h2[^>]*><span>1</span>Purpose").Count.Should().Be(3);
    }

    [Fact]
    public void A_module_document_names_its_module_and_a_demo_notice_and_the_logo_are_shown_when_they_apply()
    {
        var module = Project() with { ModuleName = "Ward", Modules = [Project().Modules[0]], Title = "Ward: all requests", DocNumber = "IMPL-WARD-MOD" };
        var text = Visible(GuideHtmlBuilder.BuildCombined(module));
        text.Should().Contain("Module").And.Contain("IMPL-WARD-MOD");
        text.Should().NotContain("Pharmacy");
        GuideHtmlBuilder.BuildCombined(Project(demo: true)).Should().Contain("Demo document");
        GuideHtmlBuilder.BuildCombined(Project()).Should().NotContain("Demo document").And.NotContain("class=\"logo\"");
        GuideHtmlBuilder.BuildCombined(Project(logo: [1, 2, 3])).Should().Contain("class=\"logo\"");
    }

    [Fact]
    public void Everything_typed_by_people_is_escaped()
    {
        var evil = Project() with
        {
            ProjectName = "<script>alert(1)</script>",
            Modules = [new CombinedModule("<img src=x onerror=1>", [new CombinedRequest("r-x", "NR-001", "<b>Title</b>", Request("<b>Title</b>", "<img src=x onerror=1>", "NR-001"))])],
        };
        var html = GuideHtmlBuilder.BuildCombined(evil);
        html.Should().NotContain("<script>alert(1)</script>").And.NotContain("<img src=x onerror=1>").And.NotContain("<b>Title</b>");
    }
}
