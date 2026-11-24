using FlowCapture.Domain;
using FluentAssertions;
using Xunit;

namespace FlowCapture.UnitTests;

public class FlowStatusMachineTests
{
    [Theory]
    [InlineData(FlowStatus.Draft, FlowStatus.Analysed)]
    [InlineData(FlowStatus.Analysed, FlowStatus.Analysed)]
    [InlineData(FlowStatus.UnderReview, FlowStatus.UnderReview)]
    [InlineData(FlowStatus.Confirmed, FlowStatus.UnderReview)]
    [InlineData(FlowStatus.DocumentsGenerated, FlowStatus.UnderReview)]
    public void A_new_analysis_moves_confirmed_flows_back_to_review(FlowStatus from, FlowStatus expected) =>
        FlowStatusMachine.OnAnalysisSucceeded(from).Should().Be(expected);

    [Fact]
    public void Editing_content_before_any_analysis_is_rejected() =>
        FluentActions.Invoking(() => FlowStatusMachine.OnContentChanged(FlowStatus.Draft)).Should().Throw<InvalidTransitionException>();

    [Theory]
    [InlineData(FlowStatus.Confirmed)]
    [InlineData(FlowStatus.DocumentsGenerated)]
    public void Editing_confirmed_content_reopens_the_review(FlowStatus from) =>
        FlowStatusMachine.OnContentChanged(from).Should().Be(FlowStatus.UnderReview);

    [Fact]
    public void Only_a_flow_under_review_can_be_confirmed()
    {
        FlowStatusMachine.Confirm(FlowStatus.UnderReview).Should().Be(FlowStatus.Confirmed);
        foreach (var s in new[] { FlowStatus.Draft, FlowStatus.Analysed, FlowStatus.Confirmed, FlowStatus.DocumentsGenerated })
            FluentActions.Invoking(() => FlowStatusMachine.Confirm(s)).Should().Throw<InvalidTransitionException>();
    }

    [Fact]
    public void Confirming_an_analysed_flow_explains_that_review_comes_first() =>
        FluentActions.Invoking(() => FlowStatusMachine.Confirm(FlowStatus.Analysed)).Should().Throw<InvalidTransitionException>().WithMessage("*review*");

    [Theory]
    [InlineData(FlowStatus.Analysed, FlowStatus.UnderReview)]
    [InlineData(FlowStatus.UnderReview, FlowStatus.UnderReview)]
    public void Submit_for_review_allowed_from_analysed_and_under_review(FlowStatus from, FlowStatus expected) =>
        FlowStatusMachine.SubmitForReview(from).Should().Be(expected);

    [Theory]
    [InlineData(FlowStatus.Draft)]
    [InlineData(FlowStatus.Confirmed)]
    public void Submit_for_review_rejected_otherwise(FlowStatus from) =>
        FluentActions.Invoking(() => FlowStatusMachine.SubmitForReview(from)).Should().Throw<InvalidTransitionException>();

    [Fact]
    public void Documents_need_a_confirmed_flow()
    {
        FlowStatusMachine.OnDocumentsGenerated(FlowStatus.Confirmed).Should().Be(FlowStatus.DocumentsGenerated);
        FlowStatusMachine.OnDocumentsGenerated(FlowStatus.DocumentsGenerated).Should().Be(FlowStatus.DocumentsGenerated);
        FluentActions.Invoking(() => FlowStatusMachine.OnDocumentsGenerated(FlowStatus.UnderReview)).Should().Throw<InvalidTransitionException>();
        FlowStatusMachine.CanGenerateDocuments(FlowStatus.Analysed).Should().BeFalse();
        FlowStatusMachine.CanGenerateDocuments(FlowStatus.Confirmed).Should().BeTrue();
    }

    [Fact]
    public void Reopen_only_applies_to_confirmed_flows()
    {
        FlowStatusMachine.Reopen(FlowStatus.Confirmed).Should().Be(FlowStatus.UnderReview);
        FlowStatusMachine.Reopen(FlowStatus.DocumentsGenerated).Should().Be(FlowStatus.UnderReview);
        FluentActions.Invoking(() => FlowStatusMachine.Reopen(FlowStatus.UnderReview)).Should().Throw<InvalidTransitionException>();
    }
}
