using FluentAssertions;
using Patchouli.Core.Import;
using Patchouli.Infrastructure.Workflows;
using Patchouli.Ocr;

namespace Patchouli.Tests;

/// <summary>
/// Issue #31, plan test item 5: page-count failures surface as a typed
/// <see cref="PdfiumDocumentException"/> carrying the native error code and the
/// document path — never as a swallowed <c>null</c> or a generic message — while
/// cancellation still propagates as <see cref="OperationCanceledException"/> and
/// only a genuinely missing file reports <c>null</c>.
/// </summary>
public sealed class PdfiumPageCountIssue31Tests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"issue31-pagecount-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Corrupt_pdf_throws_typed_exception_with_format_error_code_and_path()
    {
        string corrupt = Path.Combine(_root, "corrupt.pdf");
        await File.WriteAllTextAsync(corrupt, "this is not a pdf at all, just text");
        PdfMetadataReader reader = new();

        Func<Task> act = () => reader.GetPageCountAsync(corrupt);

        PdfiumDocumentException exception =
            (await act.Should().ThrowAsync<PdfiumDocumentException>()).Which;
        exception.Code.Should().Be(PdfiumErrorCodes.Format,
            "PDFium error 3 is FPDF_ERR_FORMAT — a format problem, not a lock/busy signal");
        exception.Path.Should().Be(corrupt);
    }

    [Fact]
    public async Task Engine_page_count_failure_is_the_same_typed_exception()
    {
        string corrupt = Path.Combine(_root, "corrupt-engine.pdf");
        await File.WriteAllTextAsync(corrupt, "definitely not a pdf");
        PdfiumDocumentEngine engine = new();

        Func<Task> act = () => engine.GetPageCountAsync(corrupt);

        PdfiumDocumentException exception =
            (await act.Should().ThrowAsync<PdfiumDocumentException>()).Which;
        exception.Code.Should().Be(PdfiumErrorCodes.Format);
        exception.Path.Should().Be(corrupt);
    }

    [Fact]
    public async Task Valid_pdf_reports_its_real_page_count()
    {
        string valid = TestFixtures.CopyRealThreePagePdfTo(_root, "valid.pdf");
        PdfMetadataReader reader = new();

        int? count = await reader.GetPageCountAsync(valid).WaitAsync(Timeout);

        count.Should().Be(3);
    }

    [Fact]
    public async Task Missing_file_reports_null_instead_of_throwing()
    {
        PdfMetadataReader reader = new();

        int? count = await reader.GetPageCountAsync(Path.Combine(_root, "missing.pdf"))
            .WaitAsync(Timeout);

        count.Should().BeNull("a missing file is the one case with nothing to read");
    }

    [Fact]
    public async Task Engine_missing_file_throws_file_not_found()
    {
        // Pins the exact exception the reader's vanish-race translation relies on: the
        // native layer reports a missing file as FileNotFoundException (unlike its
        // typed PdfiumDocumentException for real PDFium failures).
        PdfiumDocumentEngine engine = new();

        Func<Task> act = () => engine.GetPageCountAsync(Path.Combine(_root, "missing-engine.pdf"));

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Missing_file_reports_no_page_infos_instead_of_throwing()
    {
        PdfMetadataReader reader = new();

        IReadOnlyList<PdfPageInfoResult>? infos = await reader
            .GetPageInfosAsync(Path.Combine(_root, "missing-infos.pdf"))
            .WaitAsync(Timeout);

        infos.Should().BeNull("a missing file has neither a page count nor page infos");
    }

    [Fact]
    public async Task Cancelled_page_count_still_propagates_operation_canceled()
    {
        string valid = TestFixtures.CopyRealThreePagePdfTo(_root, "cancel.pdf");
        PdfMetadataReader reader = new();
        using CancellationTokenSource cancelling = new();
        cancelling.Cancel();

        Func<Task> act = () => reader.GetPageCountAsync(valid, cancelling.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "cancellation is not a PDFium failure and must not be converted into one");
    }
}
