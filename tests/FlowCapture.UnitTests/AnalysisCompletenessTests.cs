using FlowCapture.Application.Analysis;
using FlowCapture.Shared.Content;
using FluentAssertions;
using Xunit;

namespace FlowCapture.UnitTests;

public class AnalysisCompletenessTests
{
    private const string Note = "BMW Masters page. The client said all the basic waste details are entered once here.\n" +
        "Treatment Method: how the waste is destroyed.\nWaste Category: bag colours Yellow, Red, Blue, White, Green.\n" +
        "Exception Type: the kind of problem that can happen during the work.\nReason: why a record was changed.\n" +
        "Weight Unit: kilogram or gram.\nCBWTF: the outside company that collects the waste, its name, number and licence.\n" +
        "Priority: High\nAnything not needed is switched off with the Active button.";

    private static string Request(string body) =>
        "<analysis_request type=\"part\">\n<context><department>General</department></context>\n<entries>\n<entry ref=\"E1\" kind=\"text\" language=\"en-IN\">" +
        System.Security.SecurityElement.Escape(body) + "</entry>\n</entries>\n</analysis_request>";

    private static FlowContent Squashed()
    {
        var f = new FlowContent();
        f.Steps.Add(new StepItem { Id = "S1", Actor = "A", Action = "Opens the BMW Masters page.", Screen = "BMW Masters", Next = ["S2"] });
        f.Steps.Add(new StepItem { Id = "S2", Actor = "A", Action = "Configures or updates master records across the six categories (Treatment Method, Waste Category, Exception Type, Reason, Weight Unit, CBWTF).", Screen = "BMW Masters", Next = ["END"] });
        f.Screens.Add(new ScreenItem { Id = "SC1", MenuName = "BMW Masters", Fields = [new ScreenFieldItem { Name = "Treatment Method" }, new ScreenFieldItem { Name = "Waste Category" }] });
        return f;
    }

    private static FlowContent OneStepEach()
    {
        var f = new FlowContent();
        f.Steps.Add(new StepItem { Id = "S1", Actor = "A", Action = "Opens the page.", Screen = "BMW Masters", Next = ["S2", "S3", "S4", "S5", "S6", "S7"] });
        var names = new[] { "Treatment Method", "Waste Category", "Exception Type", "Reason", "Weight Unit", "CBWTF" };
        for (var i = 0; i < names.Length; i++)
        {
            f.Steps.Add(new StepItem { Id = "S" + (i + 2), Actor = "A", Action = "Maintains " + names[i] + ".", Screen = names[i], Next = ["END"] });
            f.Screens.Add(new ScreenItem { Id = "SC" + (i + 2), MenuName = names[i] });
        }
        return f;
    }

    [Fact]
    public void The_listed_items_of_the_notes_are_found_and_the_usual_labels_are_not_items()
    {
        AnalysisCompleteness.ListedItems(Request(Note)).Should().Equal("Treatment Method", "Waste Category", "Exception Type", "Reason", "Weight Unit", "CBWTF");
    }

    [Fact]
    public void Bullets_and_numbers_before_the_name_are_fine_and_a_note_with_fewer_than_three_items_lists_nothing()
    {
        AnalysisCompleteness.ListedItems(Request("1. Wards: the wards\n2) Teams: the teams\n- Rates: the hourly rate")).Should().Equal("Wards", "Teams", "Rates");
        AnalysisCompleteness.ListedItems(Request("Wards: the wards\nTeams: the teams")).Should().BeEmpty();
        AnalysisCompleteness.ListedItems(Request("Priority: High\nFrequency: Frequent\nTrigger: when a sheet comes\nNotes: none")).Should().BeEmpty("those are labels, not pages");
        AnalysisCompleteness.ListedItems("no entries here").Should().BeEmpty();
    }

    [Fact]
    public void A_list_squashed_into_one_step_is_reported_with_the_missing_items_named()
    {
        var problems = AnalysisCompleteness.Check(Request(Note), Squashed());

        problems.Should().ContainSingle();
        problems[0].Should().Contain("list of 6 items").And.Contain("Only 2 steps were written").And.Contain("Treatment Method").And.Contain("CBWTF")
            .And.Contain("EACH must be its own step").And.Contain("only fields of one screen");
    }

    [Fact]
    public void A_list_that_became_a_step_and_a_screen_each_is_not_reported_and_neither_is_a_note_without_a_list()
    {
        AnalysisCompleteness.Check(Request(Note), OneStepEach()).Should().BeEmpty();
        AnalysisCompleteness.Check(Request("When the case sheet comes the user opens the menu and picks the ward."), Squashed()).Should().BeEmpty();
    }

    [Fact]
    public void A_flow_with_as_many_steps_as_listed_items_is_left_alone_even_if_the_screens_have_other_names()
    {
        var f = OneStepEach();
        foreach (var s in f.Steps) s.Screen = "Page " + s.Id;
        f.Screens.Clear();
        AnalysisCompleteness.Check(Request(Note), f).Should().BeEmpty("there are enough steps: the AI named the pages its own way");
    }
}