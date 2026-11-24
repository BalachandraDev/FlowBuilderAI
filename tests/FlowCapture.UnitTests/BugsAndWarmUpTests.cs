using FlowCapture.Application.Documents;
using FlowCapture.Domain;
using FlowCapture.Infrastructure.Files;
using FluentAssertions;
using Xunit;

namespace FlowCapture.UnitTests;

public class BugsAndWarmUpTests
{
    [Theory]
    [InlineData("NR-003", "NR-003 (New Requirement)")]
    [InlineData("CR-012", "CR-012 (Change Request)")]
    [InlineData("BUG-004", "BUG-004 (Bug)")]
    [InlineData("bug-004", "bug-004 (Bug)")]
    [InlineData("IMPL-1", "IMPL-1")]
    public void The_document_says_what_kind_of_request_it_is(string code, string expected) =>
        GuideHtmlBuilder.RequestText(code).Should().Be(expected);

    [Fact]
    public void A_bug_is_a_third_kind_of_request_and_is_stored_as_the_text_BUG()
    {
        Enum.GetNames<RequestKind>().Should().Equal("NR", "CR", "BUG");
        RequestKind.BUG.ToString().Length.Should().BeLessThanOrEqualTo(4, "the Kind column holds four letters");
        Enum.TryParse<RequestKind>("bug", true, out var kind).Should().BeTrue();
        kind.Should().Be(RequestKind.BUG);
    }

    [Fact]
    public void The_warm_up_samples_are_real_files_the_readers_can_read_so_the_warm_up_does_its_job()
    {
        var (excel, pdf) = WarmUpHostedService.WarmFileReaders();
        excel.Should().Contain("Sheet: Warm").And.Contain("Ward | 12");
        pdf.Should().Contain("Warm up text");
    }
}
