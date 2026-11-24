using System.Text.Json.Nodes;
using FlowCapture.Application.Common;
using FlowCapture.Application.Flows;
using FlowCapture.Shared.Content;
using FluentAssertions;
using Xunit;

namespace FlowCapture.UnitTests;

public class FlowContentValidatorTests
{
    private static readonly IReadOnlySet<string> Refs = new HashSet<string> { "E1", "E2" };

    private static FlowContentValidator Validator() =>
        new(SchemaTools.PatchCategories(TestData.FlowSchema(), ["CR", "NR", "ER"]));

    private static FlowValidationContext Ai(IReadOnlySet<string>? exempt = null) =>
        new("MRD", "MRD", Refs, true, exempt ?? new HashSet<string>());

    private static FlowValidationResult Run(Action<JsonObject>? mutate = null, FlowValidationContext? ctx = null)
    {
        var node = TestData.Example();
        mutate?.Invoke(node);
        return Validator().Validate(node, ctx ?? Ai());
    }

    [Fact]
    public void The_MRD_003_example_is_valid()
    {
        var result = Run();
        result.Errors.Should().BeEmpty();
        result.IsValid.Should().BeTrue();
        result.Content!.Requirements.Should().ContainSingle().Which.ReqId.Should().Be("REQ001");
    }

    [Fact]
    public void Content_survives_a_round_trip_through_the_C_sharp_model_unchanged()
    {
        // Guards against the Shared model drifting away from flow.v1.json.
        var original = TestData.Example();
        var model = FlowJson.Deserialize(original.ToJsonString());
        var again = JsonNode.Parse(FlowJson.Serialize(model))!;
        JsonNode.DeepEquals(original, again).Should().BeTrue();
    }

    [Fact]
    public void A_step_that_points_at_a_missing_id_is_reported()
    {
        var r = Run(n => n["steps"]![0]!["next"] = new JsonArray("S99"));
        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.Contains("S99") && e.Contains("does not exist"));
    }

    [Fact]
    public void A_step_nobody_can_reach_is_reported()
    {
        var r = Run(n => ((JsonArray)n["steps"]!).Add(JsonNode.Parse("""
            {"id":"S6","actor":"MRD user","action":"Orphan step.","screen":"To be confirmed","inputs":[],"outputs":[],"next":["END"],"sourceEntryIds":["E1"]}
            """)));
        r.Errors.Should().Contain(e => e.Contains("S6") && e.Contains("reached"));
    }

    [Fact]
    public void Duplicate_ids_are_reported()
    {
        var r = Run(n => ((JsonArray)n["businessRules"]!).Add(n["businessRules"]![0]!.DeepClone()));
        r.Errors.Should().Contain(e => e.Contains("BR1") && e.Contains("more than once"));
    }

    [Fact]
    public void An_unknown_source_reference_is_reported()
    {
        var r = Run(n => n["steps"]![0]!["sourceEntryIds"] = new JsonArray("E7"));
        r.Errors.Should().Contain(e => e.Contains("E7"));
    }

    [Fact]
    public void An_item_the_AI_created_without_any_source_is_rejected()
    {
        var r = Run(n => n["businessRules"]![0]!["sourceEntryIds"] = new JsonArray());
        r.Errors.Should().Contain(e => e.Contains("BR1") && e.Contains("at least one source"));
    }

    [Fact]
    public void An_exempt_item_may_have_no_source()
    {
        var r = Run(n => n["businessRules"]![0]!["sourceEntryIds"] = new JsonArray(), Ai(new HashSet<string> { "BR1" }));
        r.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Manual_edits_do_not_need_sources()
    {
        var node = TestData.Example();
        node["businessRules"]![0]!["sourceEntryIds"] = new JsonArray();
        node["steps"]![0]!["sourceEntryIds"] = new JsonArray("E99");
        Validator().Validate(node, FlowValidationContext.Manual("MRD", "MRD")).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_text_is_rejected(string blank)
    {
        var r = Run(n => n["purpose"] = blank);
        r.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("TBC")]
    [InlineData("N/A")]
    [InlineData("Nill")]
    [InlineData("to be confirmed")]
    [InlineData("TO BE CONFIRMED")]
    public void Placeholder_spellings_other_than_the_exact_one_are_rejected(string value)
    {
        var r = Run(n => n["trigger"] = value);
        r.Errors.Should().Contain(e => e.Contains("trigger"));
    }

    [Fact]
    public void The_exact_to_be_confirmed_text_is_accepted()
    {
        Run(n => n["trigger"] = "To be confirmed").IsValid.Should().BeTrue();
    }

    [Fact]
    public void The_department_must_match_the_flow()
    {
        var r = Run(n => n["department"] = "Billing");
        r.Errors.Should().Contain(e => e.Contains("department must be 'MRD'"));
    }

    [Fact]
    public void A_category_outside_the_configured_list_is_rejected()
    {
        var r = Run(n => n["requirements"]![0]!["category"] = "XX");
        r.IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_newly_configured_category_is_accepted()
    {
        var node = TestData.Example();
        node["requirements"]![0]!["category"] = "BUG";
        var validator = new FlowContentValidator(SchemaTools.PatchCategories(TestData.FlowSchema(), ["CR", "NR", "ER", "BUG"]));
        validator.Validate(node, Ai()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void An_acceptance_criterion_for_an_unknown_requirement_is_reported()
    {
        var r = Run(n => n["acceptanceCriteria"]![0]!["reqId"] = "REQ777");
        r.Errors.Should().Contain(e => e.Contains("REQ777"));
    }

    [Fact]
    public void A_question_linked_to_a_missing_gap_is_reported()
    {
        var r = Run(n => n["clarificationQuestions"]![0]!["relatedGapId"] = "G9");
        r.Errors.Should().Contain(e => e.Contains("G9"));
    }

    [Fact]
    public void A_flow_without_steps_is_rejected()
    {
        var r = Run(n =>
        {
            n["steps"] = new JsonArray();
            n["decisions"] = new JsonArray();
            n["alternateFlows"] = new JsonArray();
            n["businessRules"] = new JsonArray();
        });
        r.Errors.Should().Contain(e => e.Contains("at least one step"));
    }

    [Fact]
    public void Extra_unknown_properties_are_rejected_by_the_schema()
    {
        var r = Run(n => n["invented"] = "x");
        r.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Missing_required_sections_are_rejected_by_the_schema()
    {
        var r = Run(n => n.Remove("gaps"));
        r.IsValid.Should().BeFalse();
        r.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void A_decision_path_to_an_unknown_node_is_reported()
    {
        var r = Run(n => n["decisions"]![0]!["noPath"]!["next"] = "S42");
        r.Errors.Should().Contain(e => e.Contains("noPath") && e.Contains("S42"));
    }
}
