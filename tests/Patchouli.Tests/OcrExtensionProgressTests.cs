using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class OcrExtensionProgressTests
{
    [Fact]
    public void Page_local_counts_are_aggregated_across_noncontiguous_pages_and_native_results()
    {
        OcrJobPage first = Page(3, 2);
        OcrJobPage second = Page(9);
        RecordingProgress reports = new();
        using OcrExtensionProgress sink = new([first, second], 3, reports);

        sink.Report(new ExtensionProgress("page-ocr", 1, 2, ItemId: "3"));
        reports.Values[^1].Fraction.Should().Be(0.5, "one native page and half an image page are processed");
        reports.Values[^1].Detail.Should().Be("pages:1/3;page:4;inputs:1/2");
        sink.Report(new ExtensionProgress("page-ocr", 2, 2, Status: ExtensionProgressStatus.Completed, ItemId: "3"));
        sink.Report(new ExtensionProgress("page-ocr", 0, 1, Status: ExtensionProgressStatus.Started, ItemId: "9"));
        reports.Values[^1].Fraction.Should().BeApproximately(2.0 / 3, 0.00001);
        reports.Values[^1].Detail.Should().Be("pages:2/3;page:10;inputs:0/1");
        sink.Report(new ExtensionProgress("page-ocr", 1, 1, Status: ExtensionProgressStatus.Failed, ItemId: "9"));
        reports.Values[^1].Fraction.Should().Be(1);
        reports.Values[^1].Detail.Should().StartWith("pages:3/3");
        reports.Values.Should().OnlyContain(report => report.Stage == OcrTaskStage.Recognizing);
    }

    [Fact]
    public void Region_progress_is_page_scoped_monotonic_and_never_forwards_raw_messages()
    {
        OcrJobPage page = Page(7, 2);
        RecordingProgress reports = new();
        using OcrExtensionProgress sink = new([page], 1, reports);
        sink.Report(new ExtensionProgress("direct-ocr", 1, 1,
            ExecutionId: page.Inputs[0].Id.Value, ItemId: "7"));
        sink.Report(new ExtensionProgress("page-ocr", 1, 2, ItemId: "7"));
        sink.Report(new ExtensionProgress("region-ocr", 2, 4, "secret /local/path",
            ExecutionId: page.Inputs[1].Id.Value, ItemId: "7"));
        reports.Values[^1].Fraction.Should().BeApproximately(0.8, 0.00001);
        reports.Values[^1].Detail.Should().Be("pages:0/1;page:8;inputs:1/2;phase:region-ocr;units:2/4");

        sink.Report(new ExtensionProgress("region-ocr", 1, 4,
            ExecutionId: page.Inputs[1].Id.Value, ItemId: "7"));
        reports.Values[^1].Fraction.Should()
            .BeApproximately(0.8, 0.00001, "out-of-order callbacks cannot move the bar backwards");
        reports.Values.Should().OnlyContain(report => report.Detail != null && !report.Detail.Contains("secret"));
    }

    [Fact]
    public void Concurrent_input_progress_does_not_assume_earlier_inputs_have_completed()
    {
        OcrJobPage page = Page(0, 2);
        RecordingProgress reports = new();
        using OcrExtensionProgress sink = new([page], 1, reports);
        sink.Report(new ExtensionProgress("direct-ocr", 1, 1,
            ExecutionId: page.Inputs[1].Id.Value, ItemId: "0"));
        reports.Values[^1].Fraction.Should().Be(0.5);
        sink.Report(new ExtensionProgress("direct-ocr", 0, 1,
            ExecutionId: page.Inputs[0].Id.Value, ItemId: "0"));
        reports.Values[^1].Fraction.Should().Be(0.5);
    }

    [Fact]
    public void Foreign_duplicate_terminal_and_post_invocation_callbacks_are_ignored()
    {
        OcrJobPage page = Page(2);
        RecordingProgress reports = new();
        using OcrExtensionProgress sink = new([page], 1, reports);
        sink.Report(new ExtensionProgress("page-ocr", 1, 1, ItemId: "99"));
        sink.Report(new ExtensionProgress("region-ocr", 1, 2, ExecutionId: "foreign-input", ItemId: "2"));
        sink.Report(new ExtensionProgress("page-ocr", 1, 1, ItemId: "not-a-page"));
        reports.Values.Should().BeEmpty();
        ExtensionProgress finished = new("page-ocr", 1, 1, Status: ExtensionProgressStatus.Completed, ItemId: "2");
        sink.Report(finished);
        sink.Report(finished);
        reports.Values.Should().ContainSingle();
        sink.Dispose();
        sink.Report(new ExtensionProgress("page-ocr", 0, 1, ItemId: "2"));
        reports.Values.Should().ContainSingle();
    }

    private static OcrJobPage Page(int index, int inputs = 1)
    {
        return new OcrJobPage(new ExtensionPageIdentity(DocumentInstanceId.New(), PageId.New(),
                DocumentTreeRevisionId.New(), index),
            Enumerable.Range(0, inputs).Select(input => new OcrJobInput(new OcrInputId($"input-{index}-{input}"),
                new NormalizedBBox(0, 0, 1, 1), "image/png")).ToArray());
    }

    private sealed class RecordingProgress : IProgress<OcrTaskStageProgress>
    {
        public List<OcrTaskStageProgress> Values { get; } = [];

        public void Report(OcrTaskStageProgress value)
        {
            Values.Add(value);
        }
    }
}
