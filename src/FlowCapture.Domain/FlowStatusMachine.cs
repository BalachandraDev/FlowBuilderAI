namespace FlowCapture.Domain;

/// <summary>
/// Flow lifecycle: Draft -> Analysed -> UnderReview -> Confirmed -> DocumentsGenerated.
/// Any content change after confirmation moves the flow back to UnderReview.
/// Role checks (Lead only for confirm) are enforced by the application layer; this class only knows states.
/// </summary>
public static class FlowStatusMachine
{
    /// <summary>A new analysis result was saved.</summary>
    public static FlowStatus OnAnalysisSucceeded(FlowStatus current) => current switch
    {
        FlowStatus.Draft => FlowStatus.Analysed,
        FlowStatus.Analysed => FlowStatus.Analysed,
        FlowStatus.UnderReview => FlowStatus.UnderReview,
        FlowStatus.Confirmed or FlowStatus.DocumentsGenerated => FlowStatus.UnderReview,
        _ => throw new InvalidTransitionException($"Unknown status {current}."),
    };

    /// <summary>The content was edited manually or rolled back to an older version.</summary>
    public static FlowStatus OnContentChanged(FlowStatus current)
    {
        if (current == FlowStatus.Draft)
            throw new InvalidTransitionException("The flow has no analysed content yet. Run an analysis first.");
        return OnAnalysisSucceeded(current);
    }

    public static FlowStatus SubmitForReview(FlowStatus current) => current switch
    {
        FlowStatus.Analysed or FlowStatus.UnderReview => FlowStatus.UnderReview,
        _ => throw new InvalidTransitionException($"A flow in status {current} cannot be submitted for review."),
    };

    public static FlowStatus Confirm(FlowStatus current) => current switch
    {
        FlowStatus.UnderReview => FlowStatus.Confirmed,
        FlowStatus.Analysed => throw new InvalidTransitionException("Submit the flow for review before confirming it."),
        _ => throw new InvalidTransitionException($"A flow in status {current} cannot be confirmed."),
    };

    public static FlowStatus Reopen(FlowStatus current) => current switch
    {
        FlowStatus.Confirmed or FlowStatus.DocumentsGenerated => FlowStatus.UnderReview,
        _ => throw new InvalidTransitionException($"A flow in status {current} is not confirmed."),
    };

    public static FlowStatus OnDocumentsGenerated(FlowStatus current) => current switch
    {
        FlowStatus.Confirmed or FlowStatus.DocumentsGenerated => FlowStatus.DocumentsGenerated,
        _ => throw new InvalidTransitionException("Documents can only be generated for a confirmed flow."),
    };

    public static bool CanGenerateDocuments(FlowStatus current) =>
        current is FlowStatus.Confirmed or FlowStatus.DocumentsGenerated;

    public static bool IsAtLeastAnalysed(FlowStatus current) => current != FlowStatus.Draft;
}
