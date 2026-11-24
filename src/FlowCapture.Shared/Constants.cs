namespace FlowCapture.Shared;

/// <summary>Global (account level) role names.</summary>
public static class AppRoles
{
    public const string Executive = "ImplementationExecutive";
    public const string Lead = "ImplementationLead";
    public const string Reviewer = "Reviewer";
    public const string Admin = "Admin";

    public static readonly string[] All = [Executive, Lead, Reviewer, Admin];
}

public static class Tbc
{
    /// <summary>The only accepted spelling for "unknown" in structured flow content.</summary>
    public const string ToBeConfirmed = "To be confirmed";
    public const string NotApplicable = "Not applicable";
}

public static class ApiHeaders
{
    public const string CorrelationId = "X-Correlation-Id";
    public const string IdempotencyKey = "Idempotency-Key";
}

public static class PromptKeys
{
    public const string PartAnalysis = "part-analysis.system";
    public const string Reanalysis = "reanalysis.system";
    public const string WholeAnalysis = "whole-analysis.system";
    public const string RetryFeedback = "retry.validation-feedback";
    public const string FlowSchema = "schema.flow";
    public const string ProjectSchema = "schema.project-analysis";
}
