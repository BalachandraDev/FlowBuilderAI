using System.Text.Json.Nodes;
using FlowCapture.Application.Common;
using FlowCapture.Application.Flows;
using FlowCapture.Application.Services;
using FlowCapture.Infrastructure.Persistence;
using FlowCapture.Shared.Content;
using FluentAssertions;
using Xunit;

namespace FlowCapture.UnitTests;

public class FlowLockEnforcerTests
{
    private static JsonObject Prev() => TestData.Example();

    [Fact]
    public void A_locked_item_the_model_changed_is_restored()
    {
        var prev = Prev();
        prev["steps"]![1]!["action"] = "User edit: pre-select the ward of the logged in user.";
        var result = TestData.Example();   // model output still has the original wording

        var warnings = FlowLockEnforcer.Enforce(prev, result, ["S2"]);

        result["steps"]![1]!["action"]!.GetValue<string>().Should().StartWith("User edit");
        warnings.Should().ContainSingle().Which.Should().Contain("S2");
    }

    [Fact]
    public void A_locked_item_the_model_deleted_is_put_back_at_its_old_position()
    {
        var prev = Prev();
        var result = TestData.Example();
        ((JsonArray)result["steps"]!).RemoveAt(1);

        FlowLockEnforcer.Enforce(prev, result, ["S2"]);

        result["steps"]!.AsArray().Select(s => s!["id"]!.GetValue<string>()).Should().Equal("S1", "S2", "S3", "S4", "S5");
    }

    [Fact]
    public void A_locked_top_level_field_is_restored()
    {
        var prev = Prev();
        prev["purpose"] = "Purpose written by the lead.";
        var result = TestData.Example();

        FlowLockEnforcer.Enforce(prev, result, ["purpose"]);

        result["purpose"]!.GetValue<string>().Should().Be("Purpose written by the lead.");
    }

    [Fact]
    public void Requirements_are_matched_by_req_id()
    {
        var prev = Prev();
        prev["requirements"]![0]!["priority"] = "Low";
        var result = TestData.Example();

        FlowLockEnforcer.Enforce(prev, result, ["REQ001"]);

        result["requirements"]![0]!["priority"]!.GetValue<string>().Should().Be("Low");
    }

    [Fact]
    public void Unlocked_changes_by_the_model_are_kept_and_unknown_locks_are_ignored()
    {
        var prev = Prev();
        var result = TestData.Example();
        result["steps"]![2]!["action"] = "Model improved wording.";

        var warnings = FlowLockEnforcer.Enforce(prev, result, ["S2", "NOPE9"]);

        result["steps"]![2]!["action"]!.GetValue<string>().Should().Be("Model improved wording.");
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void Items_without_sources_are_detected_as_user_created()
    {
        var node = TestData.Example();
        node["businessRules"]![2]!["sourceEntryIds"] = new JsonArray();
        FlowLockEnforcer.ItemsWithoutSources(node).Should().BeEquivalentTo(["BR3"]);
    }
}

public class MermaidTests
{
    [Fact]
    public void The_builder_draws_lanes_decisions_and_the_end_node()
    {
        var c = FlowJson.Deserialize(TestData.Example().ToJsonString());
        var mermaid = MermaidBuilder.Build(c);

        mermaid.Should().StartWith("flowchart TD");
        mermaid.Should().Contain("subgraph L1[\"MRD user\"]").And.Contain("subgraph L2[\"System\"]");
        mermaid.Should().Contain("S1[\"1. Opens the IP Case Sheet Receive in Department menu.\"]");
        mermaid.Should().Contain("D1{\"Is the user a super user?\"}");
        mermaid.Should().Contain("D1 -->|Yes| S2").And.Contain("D1 -->|No| S3");
        mermaid.Should().Contain("S5 --> END1").And.Contain("END1([\"End\"])");
        MermaidSyntaxChecker.IsAcceptable(mermaid, c).Should().BeTrue();
    }

    [Fact]
    public void The_example_diagram_from_the_prompt_pack_passes_the_structure_check()
    {
        var c = FlowJson.Deserialize(TestData.Example().ToJsonString());
        MermaidSyntaxChecker.IsAcceptable(c.Mermaid, c).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("graph LR\nA-->B")]
    [InlineData("flowchart TD\n  S1[\"x\"]")]                       // other nodes missing
    [InlineData("flowchart TD\n  subgraph L1[\"a\"]\n S1[\"x\"]")]  // subgraph never closed
    public void A_broken_model_diagram_is_rejected(string bad)
    {
        var c = FlowJson.Deserialize(TestData.Example().ToJsonString());
        MermaidSyntaxChecker.IsAcceptable(bad, c).Should().BeFalse();
    }

    [Fact]
    public void Labels_are_made_safe_for_mermaid()
    {
        MermaidBuilder.Label("Open \"menu\" [A|B] {x}\nthen `go`").Should().Be("Open 'menu' A B x then 'go'");
        MermaidBuilder.Label(new string('x', 200)).Length.Should().BeLessThanOrEqualTo(80);
        MermaidBuilder.Label("   ").Should().Be("-");
    }

    [Fact]
    public void A_flow_without_steps_still_produces_a_valid_diagram()
    {
        MermaidBuilder.Build(new FlowContent()).Should().StartWith("flowchart TD");
    }
}

public class FlowDifferTests
{
    [Fact]
    public void Added_removed_and_modified_items_and_fields_are_reported()
    {
        var a = TestData.Example();
        var b = TestData.Example();
        b["purpose"] = "New purpose.";
        b["steps"]![0]!["action"] = "Changed action.";
        ((JsonArray)b["businessRules"]!).RemoveAt(2);
        ((JsonArray)b["gaps"]!).Add(JsonNode.Parse("""{"id":"G4","section":"general","description":"New gap."}"""));

        var diff = FlowDiffer.Diff(a, b);

        diff.Should().Contain(d => d.Section == "purpose" && d.Change == "Modified");
        diff.Should().Contain(d => d.Section == "steps" && d.ItemId == "S1" && d.Change == "Modified" && d.Before != null && d.After != null);
        diff.Should().Contain(d => d.Section == "businessRules" && d.ItemId == "BR3" && d.Change == "Removed" && d.After == null);
        diff.Should().Contain(d => d.Section == "gaps" && d.ItemId == "G4" && d.Change == "Added" && d.Before == null);
        diff.Should().HaveCount(4);
    }

    [Fact]
    public void Identical_documents_have_no_differences() =>
        FlowDiffer.Diff(TestData.Example(), TestData.Example()).Should().BeEmpty();
}

public class SchemaToolsTests
{
    [Fact]
    public void Inlining_removes_every_reference_and_definition()
    {
        var inlined = SchemaTools.InlineRefs(TestData.FlowSchema());
        var text = inlined.ToJsonString();
        text.Should().NotContain("$ref").And.NotContain("$defs").And.NotContain("$schema");
        inlined["properties"]!["steps"]!["items"]!["properties"]!["next"].Should().NotBeNull();
        inlined["properties"]!["flowTitle"]!["minLength"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public void The_project_schema_can_also_be_inlined() =>
        SchemaTools.InlineRefs(TestData.ProjectSchema()).ToJsonString().Should().NotContain("$ref");

    [Fact]
    public void Category_patching_replaces_the_enum_and_keeps_to_be_confirmed()
    {
        var patched = SchemaTools.PatchCategories(TestData.FlowSchema(), ["AA", "BB"]);
        var values = patched["$defs"]!["requirement"]!["properties"]!["category"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>());
        values.Should().Equal("AA", "BB", "To be confirmed");
        // the original is not modified
        TestData.FlowSchema()["$defs"]!["requirement"]!["properties"]!["category"]!["enum"]!.AsArray().Should().HaveCount(4);
    }
}

public class PhiMaskerTests
{
    [Theory]
    [InlineData("call him on 9876543210 tomorrow", "[PHONE]")]
    [InlineData("number is +91 98765 43210.", "[PHONE]")]
    [InlineData("mail rajan@example.com please", "[EMAIL]")]
    [InlineData("his UHID: AB123456 is wrong", "[UHID]")]
    [InlineData("MRN 77889900 not found", "[UHID]")]
    [InlineData("patient with IP No 24/001234 came", "[IP_NUMBER]")]
    [InlineData("aadhaar 1234 5678 9012 given", "[ID_NUMBER]")]
    [InlineData("Mr Ramesh Kumar visited", "[NAME]")]
    [InlineData("the patient name is Lakshmi Devi in the register", "[PATIENT_NAME]")]
    public void Identifiers_are_replaced(string input, string placeholder)
    {
        var result = PhiMaskerCore.Mask(input);
        result.Text.Should().Contain(placeholder);
        result.Count.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("In MRD the user opens the IP Case Sheet Receive menu and selects the ward.")]
    [InlineData("The IP number field is mandatory on the Patient Details screen.")]
    [InlineData("Show the UHID field next to the Case Sheet Receive button.")]
    [InlineData("REQ001 is a change request for the Department Wise Report.")]
    [InlineData("Dr Suresh approves it, then Billing prints the Final Bill Summary.")]
    public void Workflow_text_is_left_alone(string input)
    {
        var result = PhiMaskerCore.Mask(input);
        result.Text.Should().Be(input);
        result.Count.Should().Be(0);
    }

    [Fact]
    public void Names_from_the_dictionary_are_masked_case_insensitively()
    {
        var r = PhiMaskerCore.Mask("Then ramesh and Lakshmi check it.", ["Ramesh", "lakshmi"]);
        r.Text.Should().Be("Then [NAME] and [NAME] check it.");
        r.Count.Should().Be(2);
    }

    [Fact]
    public void The_original_numbers_do_not_survive()
    {
        var r = PhiMaskerCore.Mask("UHID: AB123456, phone 9876543210, email a@b.com");
        r.Text.Should().NotContain("AB123456").And.NotContain("9876543210").And.NotContain("a@b.com");
    }
}

public class PromptFileTests
{
    [Theory]
    [InlineData("common-rules.v1.md", "common-rules", 1)]
    [InlineData("part-analysis.system.v1.md", "part-analysis.system", 1)]
    [InlineData("whole-analysis.system.v2.md", "whole-analysis.system", 2)]
    [InlineData("retry.validation-feedback.v1.md", "retry.validation-feedback", 1)]
    [InlineData("schemas/flow.v1.json", "schema.flow", 1)]
    [InlineData("schemas/project-analysis.v1.json", "schema.project-analysis", 1)]
    [InlineData("examples/mrd-003.expected.json", "example.mrd-003.expected", 1)]
    [InlineData("examples/mrd-003.user-message.txt", "example.mrd-003.user-message", 1)]
    public void Files_map_to_template_keys_and_versions(string path, string key, int version) =>
        DbSeeder.Classify(path).Should().Be((key, version));

    [Theory]
    [InlineData("README.md")]
    [InlineData("notes.txt")]
    [InlineData("schemas/readme.md")]
    public void Other_files_are_not_templates(string path) => DbSeeder.Classify(path).Should().BeNull();
}
