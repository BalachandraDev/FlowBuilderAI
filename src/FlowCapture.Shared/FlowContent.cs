using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowCapture.Shared.Content;

/// <summary>JSON options used for every flow.v1 document (camelCase, compact).</summary>
public static class FlowJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        // Optional fields (relatedGapId, options) must be left out, not written as null, or the schema rejects the document.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    public static string Serialize(FlowContent content) => JsonSerializer.Serialize(content, Options);
    public static FlowContent Deserialize(string json) =>
        JsonSerializer.Deserialize<FlowContent>(json, Options) ?? throw new JsonException("Empty flow content.");
}

/// <summary>C# mirror of prompts/schemas/flow.v1.json. Property names map to camelCase JSON.</summary>
public sealed class FlowContent
{
    public string SchemaVersion { get; set; } = "1.0";
    public string FlowTitle { get; set; } = "";
    public string Department { get; set; } = "";
    public string ModuleCode { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Scope { get; set; } = "";
    public List<ActorItem> Actors { get; set; } = [];
    public string Trigger { get; set; } = "";
    public List<TextItem> PreConditions { get; set; } = [];
    public List<TextItem> PostConditions { get; set; } = [];
    public List<StepItem> Steps { get; set; } = [];
    public List<DecisionItem> Decisions { get; set; } = [];
    public List<AlternateFlowItem> AlternateFlows { get; set; } = [];
    public List<ExceptionFlowItem> ExceptionFlows { get; set; } = [];
    public List<BusinessRuleItem> BusinessRules { get; set; } = [];
    public List<ValidationItem> Validations { get; set; } = [];
    public List<DataEntityItem> DataEntities { get; set; } = [];
    public List<ScreenItem> Screens { get; set; } = [];
    public List<ReportItem> ReportsAndPrints { get; set; } = [];
    public List<IntegrationItem> Integrations { get; set; } = [];
    public List<RequirementItem> Requirements { get; set; } = [];
    public List<AcceptanceCriterionItem> AcceptanceCriteria { get; set; } = [];
    public List<UserRoleItem> UserRoles { get; set; } = [];
    public List<NonFunctionalItem> NonFunctionalNotes { get; set; } = [];
    public UseCaseItem UseCase { get; set; } = new();
    public List<GapItem> Gaps { get; set; } = [];
    public List<QuestionItem> ClarificationQuestions { get; set; } = [];
    public ConfidenceItem Confidence { get; set; } = new();
    public List<ChangeLogItem> ChangeLog { get; set; } = [];
    public string Mermaid { get; set; } = "";
}

public sealed class ActorItem
{
    public string Id { get; set; } = "";
    public string Role { get; set; } = "";
    public string Department { get; set; } = "";
    public List<string> SourceEntryIds { get; set; } = [];
}

public sealed class TextItem
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public List<string> SourceEntryIds { get; set; } = [];
}

public sealed class StepItem
{
    public string Id { get; set; } = "";
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string Screen { get; set; } = "";
    public List<string> Inputs { get; set; } = [];
    public List<string> Outputs { get; set; } = [];
    public List<string> Next { get; set; } = [];
    public List<string> SourceEntryIds { get; set; } = [];
}

public sealed class DecisionPath
{
    public string Summary { get; set; } = "";
    public string Next { get; set; } = "";
}

public sealed class DecisionItem
{
    public string Id { get; set; } = "";
    public string Condition { get; set; } = "";
    public DecisionPath YesPath { get; set; } = new();
    public DecisionPath NoPath { get; set; } = new();
    public List<string> SourceEntryIds { get; set; } = [];
}

public sealed class AlternateFlowItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Condition { get; set; } = "";
    public string BranchFrom { get; set; } = "";
    public List<string> Steps { get; set; } = [];
    public string RejoinAt { get; set; } = "";
    public List<string> SourceEntryIds { get; set; } = [];
}

public sealed class ExceptionFlowItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Condition { get; set; } = "";
    public List<string> Handling { get; set; } = [];
    public string Outcome { get; set; } = "";
    public List<string> SourceEntryIds { get; set; } = [];
}

public sealed class BusinessRuleItem
{
    public string Id { get; set; } = "";
    public string Rule { get; set; } = "";
    public List<string> AppliesTo { get; set; } = [];
    public List<string> SourceEntryIds { get; set; } = [];
}

public sealed class ValidationItem
{
    public string Id { get; set; } = "";
    public string Field { get; set; } = "";
    public string Rule { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public List<string> SourceEntryIds { get; set; } = [];
}

public sealed class DataFieldItem
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Mandatory { get; set; } = "";
    public string Source { get; set; } = "";
}

public sealed class DataEntityItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<DataFieldItem> Fields { get; set; } = [];
    public string SuggestedChange { get; set; } = "";
    public List<string> SourceEntryIds { get; set; } = [];
}

public sealed class ScreenFieldItem
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Mandatory { get; set; } = "";
    public string DefaultValue { get; set; } = "";
    public string Source { get; set; } = "";
}

public sealed class ScreenItem
{
    public string Id { get; set; } = "";
    public string MenuName { get; set; } = "";
    public List<ScreenFieldItem> Fields { get; set; } = [];
    public List<string> SourceEntryIds { get; set; } = [];
}

public sealed class ReportItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> SourceEntryIds { get; set; } = [];
}

public sealed class IntegrationItem
{
    public string Id { get; set; } = "";
    public string System { get; set; } = "";
    public string Direction { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> SourceEntryIds { get; set; } = [];
}

public sealed class RequirementItem
{
    public string ReqId { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public string Priority { get; set; } = "";
    public List<string> SourceEntryIds { get; set; } = [];
}

public sealed class AcceptanceCriterionItem
{
    public string Id { get; set; } = "";
    public string ReqId { get; set; } = "";
    public string Given { get; set; } = "";
    public string When { get; set; } = "";
    public string Then { get; set; } = "";
    public List<string> SourceEntryIds { get; set; } = [];
}

public sealed class UserRoleItem
{
    public string Id { get; set; } = "";
    public string UserRole { get; set; } = "";
    public string Frequency { get; set; } = "";
    public string Module { get; set; } = "";
    public string MenuName { get; set; } = "";
    public List<string> SourceEntryIds { get; set; } = [];
}

public sealed class NonFunctionalItem
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Note { get; set; } = "";
    public List<string> SourceEntryIds { get; set; } = [];
}

public sealed class UseCaseItem
{
    public List<string> PrimaryActors { get; set; } = [];
    public string Priority { get; set; } = "";
    public string MainScenario { get; set; } = "";
}

public sealed class GapItem
{
    public string Id { get; set; } = "";
    public string Section { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class QuestionItem
{
    public string Id { get; set; } = "";
    public string Section { get; set; } = "";
    public string Question { get; set; } = "";
    public string? RelatedGapId { get; set; }
    public List<string>? Options { get; set; }
}

public sealed class ConfidenceItem
{
    public double PurposeAndScope { get; set; }
    public double Actors { get; set; }
    public double Steps { get; set; }
    public double Decisions { get; set; }
    public double AlternateAndExceptionFlows { get; set; }
    public double BusinessRulesAndValidations { get; set; }
    public double DataAndScreens { get; set; }
    public double ReportsAndIntegrations { get; set; }
    public double Requirements { get; set; }
}

public sealed class ChangeLogItem
{
    public string ItemId { get; set; } = "";
    public string Change { get; set; } = "";
    public string Summary { get; set; } = "";
}
