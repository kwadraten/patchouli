using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Patchouli.Core.Csl;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Results;
using Patchouli.Core.Settings;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Import;

namespace Patchouli.Tests;

/// <summary>
/// Issue #11: the shared file import core — path validation, content sniffing,
/// behavior-aware file handling (in-place vs move-to-first-search-root), recovery,
/// dedup flow-through and the "ingested, never OCR-complete" status contract.
/// </summary>
public sealed class FileImportServiceTests : IDisposable
{
    private const string PdfBytes = "%PDF-1.7\n1 0 obj\n<<>>\nendobj\n";

    private const string CslBytes =
        "<style xmlns=\"http://purl.org/net/xbiblio/csl\"><info>" +
        "<title>T</title><id>http://example.org/styles/t</id></info></style>";

    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"patchouli-import-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        TestTempFileCleanup.DeleteDirectoryWithRetry(_root);
    }

    private string WriteFile(string relativePath, string content)
    {
        string full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, Encoding.ASCII);
        return full;
    }

    private Harness CreateHarness(
        FileHandlingBehavior mcpBehavior = FileHandlingBehavior.KeepInPlace,
        FileHandlingBehavior dragBehavior = FileHandlingBehavior.KeepInPlace,
        IReadOnlyList<FileSearchRoot>? roots = null,
        Func<string, PdfImportResult>? pdfImport = null,
        Func<string, Result<CslLocalInstallResult>>? cslInstall = null)
    {
        FakePdfWorkflow pdf = new(pdfImport ?? (_ => PdfImportResultFactory.Imported()));
        FakeCslInstall csl = new(cslInstall ?? (_ =>
            Result<CslLocalInstallResult>.Success(new CslLocalInstallResult(
                true, "t", "patchouli://csl-styles/t.csl", "T",
                [], false, false, true, null, null))));
        FakeBehaviorSettings behavior = new(
            new FileHandlingBehaviorSettings(mcpBehavior, dragBehavior));
        FakeFileResolution resolution = new(roots ?? []);
        FakeFingerprints fingerprints = new();
        FakeRecoveryStore recovery = new();
        FileImportService service = new(pdf, csl, behavior, resolution, fingerprints,
            recovery, new SystemClock());
        return new Harness(service, pdf, csl, behavior, resolution, recovery);
    }

    private sealed record Harness(
        FileImportService Service,
        FakePdfWorkflow Pdf,
        FakeCslInstall Csl,
        FakeBehaviorSettings Behavior,
        FakeFileResolution Resolution,
        FakeRecoveryStore Recovery);

    // ---- path validation ----

    [Fact]
    public async Task Import_rejects_url_before_any_file_access()
    {
        Harness harness = CreateHarness();

        Result<FileImportResult> result = await harness.Service.ImportAsync(
            new FileImportRequest("https://example.com/paper.pdf", null, FileImportTrigger.McpAdd));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(FileImportErrorCodes.UrlNotAllowed);
        harness.Pdf.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Import_rejects_missing_path()
    {
        Harness harness = CreateHarness();

        Result<FileImportResult> result = await harness.Service.ImportAsync(
            new FileImportRequest(Path.Combine(_root, "nope.pdf"), null, FileImportTrigger.McpAdd));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(FileImportErrorCodes.PathNotFound);
    }

    [Fact]
    public async Task Import_rejects_directory()
    {
        Harness harness = CreateHarness();
        string dir = Path.Combine(_root, "adir");
        Directory.CreateDirectory(dir);

        Result<FileImportResult> result = await harness.Service.ImportAsync(
            new FileImportRequest(dir, null, FileImportTrigger.McpAdd));

        // Directories are rejected before any ingestion. Directory.Exists is checked
        // before File.Exists so directories surface as NotRegularFile, not PathNotFound.
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(FileImportErrorCodes.NotRegularFile);
        harness.Pdf.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Import_checks_content_not_extension()
    {
        Harness harness = CreateHarness();
        // Named .pdf but actually plain text.
        string path = WriteFile("fake.pdf", "this is not a pdf at all");

        Result<FileImportResult> result = await harness.Service.ImportAsync(
            new FileImportRequest(path, null, FileImportTrigger.McpAdd));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(FileImportErrorCodes.UnsupportedFormat);
        harness.Pdf.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Import_sniffs_pdf_without_extension()
    {
        Harness harness = CreateHarness();
        string path = WriteFile("noext", PdfBytes);

        Result<FileImportResult> result = await harness.Service.ImportAsync(
            new FileImportRequest(path, null, FileImportTrigger.McpAdd));

        result.IsSuccess.Should().BeTrue();
        result.Value.Kind.Should().Be(LocalImportKind.Pdf);
        harness.Pdf.LastPath.Should().Be(Path.GetFullPath(path));
    }

    [Fact]
    public async Task Import_rejects_kind_mismatch_between_explicit_and_content()
    {
        Harness harness = CreateHarness();
        string path = WriteFile("style.csl", CslBytes);

        Result<FileImportResult> result = await harness.Service.ImportAsync(
            new FileImportRequest(path, "pdf", FileImportTrigger.McpAdd));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(FileImportErrorCodes.KindMismatch);
    }

    [Fact]
    public async Task Import_rejects_unknown_explicit_kind()
    {
        Harness harness = CreateHarness();
        string path = WriteFile("a.pdf", PdfBytes);

        Result<FileImportResult> result = await harness.Service.ImportAsync(
            new FileImportRequest(path, "docx", FileImportTrigger.McpAdd));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.InvalidArgument);
    }

    // ---- keep-in-place behavior ----

    [Fact]
    public async Task Import_keep_in_place_does_not_move_and_reports_ingested()
    {
        Harness harness = CreateHarness();
        string path = WriteFile("keep.pdf", PdfBytes);

        Result<FileImportResult> result = await harness.Service.ImportAsync(
            new FileImportRequest(path, null, FileImportTrigger.McpAdd));

        result.IsSuccess.Should().BeTrue();
        result.Value.Moved.Should().BeFalse();
        result.Value.Reused.Should().BeFalse();
        result.Value.ItemUri.Should().Be("patchouli://items/item-1.yml");
        result.Value.TextUri.Should().Be("patchouli://texts/doc-1/");
        // Importing a PDF never claims OCR or indexing is done.
        result.Value.BodyStatus.Should().Be("ingested");
        result.Value.ImportStatus.Should().Be("imported");
        File.Exists(path).Should().BeTrue();
        harness.Pdf.LastPath.Should().Be(Path.GetFullPath(path));
    }

    [Fact]
    public async Task Import_reports_duplicate_reuse_from_workflow()
    {
        Harness harness = CreateHarness(pdfImport: _ => PdfImportResultFactory.Duplicate());
        string path = WriteFile("dup.pdf", PdfBytes);

        Result<FileImportResult> result = await harness.Service.ImportAsync(
            new FileImportRequest(path, null, FileImportTrigger.McpAdd));

        result.IsSuccess.Should().BeTrue();
        result.Value.Reused.Should().BeTrue();
        result.Value.ImportStatus.Should().Be("already_imported");
    }

    [Fact]
    public async Task Import_surfaces_page_failures_as_issues_not_silent_success()
    {
        Harness harness = CreateHarness(pdfImport: _ => PdfImportResultFactory.WithPageFailure());
        string path = WriteFile("pages.pdf", PdfBytes);

        Result<FileImportResult> result = await harness.Service.ImportAsync(
            new FileImportRequest(path, null, FileImportTrigger.McpAdd));

        result.IsSuccess.Should().BeTrue();
        result.Value.Issues.Should().ContainSingle(issue => issue.Code == "page_failure");
        result.Value.ImportStatus.Should().Be("imported_with_page_failures");
    }

    [Fact]
    public async Task Import_scan_trigger_always_keeps_in_place()
    {
        // Even with move behavior configured, scan keeps the existing contract.
        Harness harness = CreateHarness(
            FileHandlingBehavior.MoveToFirstSearchRoot,
            FileHandlingBehavior.MoveToFirstSearchRoot);
        string path = WriteFile("scan.pdf", PdfBytes);

        Result<FileImportResult> result = await harness.Service.ImportAsync(
            new FileImportRequest(path, null, FileImportTrigger.Scan));

        result.IsSuccess.Should().BeTrue();
        result.Value.Moved.Should().BeFalse();
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task Import_csl_never_moves_even_with_move_behavior()
    {
        Harness harness = CreateHarness(
            FileHandlingBehavior.MoveToFirstSearchRoot,
            roots: [SearchRoot("first")]);
        string path = WriteFile("style.csl", CslBytes);

        Result<FileImportResult> result = await harness.Service.ImportAsync(
            new FileImportRequest(path, null, FileImportTrigger.McpAdd));

        result.IsSuccess.Should().BeTrue();
        result.Value.Kind.Should().Be(LocalImportKind.Csl);
        result.Value.Moved.Should().BeFalse();
        result.Value.StyleUri.Should().Be("patchouli://csl-styles/t.csl");
        result.Value.BodyStatus.Should().Be("body_readable");
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task Import_authorization_failure_happens_before_any_move()
    {
        Harness harness = CreateHarness(
            FileHandlingBehavior.MoveToFirstSearchRoot,
            roots: [SearchRoot("first")]);
        string path = WriteFile("auth.pdf", PdfBytes);

        Result<FileImportResult> result = await harness.Service.ImportAsync(
            new FileImportRequest(path, null, FileImportTrigger.McpAdd,
                _ => Task.FromResult(Result.Failure(AppErrorCodes.UnsupportedOperation, "denied"))));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.UnsupportedOperation);
        File.Exists(path).Should().BeTrue();
        harness.Pdf.CallCount.Should().Be(0);
    }

    // ---- move-to-first-search-root behavior ----

    private FileSearchRoot SearchRoot(string name)
    {
        string dir = Directory.CreateDirectory(Path.Combine(_root, "roots", name)).FullName;
        return new FileSearchRoot(FileSearchRootId.New(), LibraryId.New(), dir, true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Import_move_fails_explicitly_with_no_search_root()
    {
        Harness harness = CreateHarness(
            FileHandlingBehavior.MoveToFirstSearchRoot, roots: []);
        string path = WriteFile("mv.pdf", PdfBytes);

        Result<FileImportResult> result = await harness.Service.ImportAsync(
            new FileImportRequest(path, null, FileImportTrigger.McpAdd));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(FileImportErrorCodes.NoSearchRoot);
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task Import_move_fails_explicitly_when_first_root_unavailable()
    {
        // The "root" is a file, not a directory: not available, never silently
        // replaced by another root or by in-place registration.
        string fileAsRoot = WriteFile("notadir", "x");
        FileSearchRoot root = new(FileSearchRootId.New(), LibraryId.New(), fileAsRoot, true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Harness harness = CreateHarness(
            FileHandlingBehavior.MoveToFirstSearchRoot, roots: [root]);
        string path = WriteFile("mv2.pdf", PdfBytes);

        Result<FileImportResult> result = await harness.Service.ImportAsync(
            new FileImportRequest(path, null, FileImportTrigger.McpAdd));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(FileImportErrorCodes.SearchRootUnavailable);
        File.Exists(path).Should().BeTrue();
        harness.Pdf.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Import_move_uses_only_the_first_root_in_order()
    {
        FileSearchRoot first = SearchRoot("first");
        FileSearchRoot second = SearchRoot("second");
        Harness harness = CreateHarness(
            FileHandlingBehavior.MoveToFirstSearchRoot, roots: [first, second]);
        string path = WriteFile("order.pdf", PdfBytes);

        Result<FileImportResult> result = await harness.Service.ImportAsync(
            new FileImportRequest(path, null, FileImportTrigger.McpAdd));

        result.IsSuccess.Should().BeTrue();
        result.Value.Moved.Should().BeTrue();
        File.Exists(Path.Combine(first.RootPath, "order.pdf")).Should().BeTrue();
        File.Exists(Path.Combine(second.RootPath, "order.pdf")).Should().BeFalse();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task Import_move_skips_file_already_inside_target_root()
    {
        FileSearchRoot first = SearchRoot("first");
        Harness harness = CreateHarness(
            FileHandlingBehavior.MoveToFirstSearchRoot, roots: [first]);
        string path = Path.Combine(first.RootPath, "inside.pdf");
        File.WriteAllText(path, PdfBytes, Encoding.ASCII);

        Result<FileImportResult> result = await harness.Service.ImportAsync(
            new FileImportRequest(path, null, FileImportTrigger.McpAdd));

        result.IsSuccess.Should().BeTrue();
        result.Value.Moved.Should().BeFalse();
        harness.Pdf.LastPath.Should().Be(Path.GetFullPath(path));
    }

    [Fact]
    public async Task Import_move_reuses_identical_content_at_target_name()
    {
        FileSearchRoot first = SearchRoot("first");
        // Same bytes already occupy the target name: no physical move, dedup decides.
        File.WriteAllText(Path.Combine(first.RootPath, "same.pdf"), PdfBytes, Encoding.ASCII);
        Harness harness = CreateHarness(
            FileHandlingBehavior.MoveToFirstSearchRoot,
            roots: [first],
            pdfImport: _ => PdfImportResultFactory.Duplicate());
        string path = WriteFile("same.pdf", PdfBytes);

        Result<FileImportResult> result = await harness.Service.ImportAsync(
            new FileImportRequest(path, null, FileImportTrigger.McpAdd));

        result.IsSuccess.Should().BeTrue();
        result.Value.Moved.Should().BeFalse();
        result.Value.Reused.Should().BeTrue();
        // The source file is not deleted: the registered copy stays the usable original.
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task Import_move_same_name_different_content_gets_deterministic_suffix()
    {
        FileSearchRoot first = SearchRoot("first");
        File.WriteAllText(Path.Combine(first.RootPath, "clash.pdf"), PdfBytes + "different", Encoding.ASCII);
        Harness harness = CreateHarness(
            FileHandlingBehavior.MoveToFirstSearchRoot, roots: [first]);
        string path = WriteFile("clash.pdf", PdfBytes);

        Result<FileImportResult> result = await harness.Service.ImportAsync(
            new FileImportRequest(path, null, FileImportTrigger.McpAdd));

        result.IsSuccess.Should().BeTrue();
        result.Value.Moved.Should().BeTrue();
        File.Exists(Path.Combine(first.RootPath, "clash-2.pdf")).Should().BeTrue();
        // The pre-existing file is never overwritten.
        File.ReadAllText(Path.Combine(first.RootPath, "clash.pdf"), Encoding.ASCII)
            .Should().Be(PdfBytes + "different");
        harness.Pdf.LastPath.Should().Be(Path.Combine(first.RootPath, "clash-2.pdf"));
    }

    [Fact]
    public async Task Import_move_reports_moved_issue()
    {
        FileSearchRoot first = SearchRoot("first");
        Harness harness = CreateHarness(
            FileHandlingBehavior.MoveToFirstSearchRoot, roots: [first]);
        string path = WriteFile("noted.pdf", PdfBytes);

        Result<FileImportResult> result = await harness.Service.ImportAsync(
            new FileImportRequest(path, null, FileImportTrigger.McpAdd));

        result.IsSuccess.Should().BeTrue();
        result.Value.Issues.Should().ContainSingle(issue => issue.Code == "moved_to_search_root");
    }

    [Fact]
    public async Task Import_failure_after_move_restores_original_position()
    {
        FileSearchRoot first = SearchRoot("first");
        Harness harness = CreateHarness(
            FileHandlingBehavior.MoveToFirstSearchRoot,
            roots: [first],
            pdfImport: _ => PdfImportResultFactory.Failed("boom"));
        string path = WriteFile("restore.pdf", PdfBytes);

        Result<FileImportResult> result = await harness.Service.ImportAsync(
            new FileImportRequest(path, null, FileImportTrigger.McpAdd));

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Contain("restored");
        // The file is back where it started; nothing is lost.
        File.Exists(path).Should().BeTrue();
        File.Exists(Path.Combine(first.RootPath, "restore.pdf")).Should().BeFalse();
        harness.Recovery.Records.Should().ContainSingle(record =>
            record.Status == FileImportRecoveryStatuses.ImportFailedRestored);
    }

    [Fact]
    public async Task Import_cancellation_after_move_restores_original_position()
    {
        // Issue #11 review P1-4: when the import is cancelled after the file was
        // moved (here the PDF import observes the cancellation), the moved file
        // must be restored with a non-cancelled cleanup token and the cancellation
        // rethrown — never swallowed, never leaving the file stranded at the target.
        FileSearchRoot first = SearchRoot("first");
        using CancellationTokenSource cts = new();
        CancellationToken token = cts.Token;
        Harness harness = CreateHarness(
            FileHandlingBehavior.MoveToFirstSearchRoot,
            roots: [first],
            pdfImport: _ =>
            {
                // ImportAsync and the assertion finish before the source is disposed.
                // ReSharper disable once AccessToDisposedClosure
                cts.Cancel();
                throw new OperationCanceledException(token);
            });
        string path = WriteFile("cancel.pdf", PdfBytes);

        Func<Task> act = () => harness.Service.ImportAsync(
            new FileImportRequest(path, null, FileImportTrigger.McpAdd), token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        // The file is back where it started; nothing is lost.
        File.Exists(path).Should().BeTrue();
        File.Exists(Path.Combine(first.RootPath, "cancel.pdf")).Should().BeFalse();
        harness.Recovery.Records.Should().ContainSingle(record =>
            record.Status == FileImportRecoveryStatuses.ImportFailedRestored);
    }

    [Fact]
    public async Task Import_drag_drop_uses_its_own_independent_behavior()
    {
        // MCP add keeps in place while drag-drop moves: the two actions are independent.
        FileSearchRoot first = SearchRoot("first");
        Harness harness = CreateHarness(
            FileHandlingBehavior.KeepInPlace,
            FileHandlingBehavior.MoveToFirstSearchRoot,
            [first]);
        string mcpPath = WriteFile("mcp.pdf", PdfBytes);
        string dragPath = WriteFile("drag.pdf", PdfBytes);

        Result<FileImportResult> mcp = await harness.Service.ImportAsync(
            new FileImportRequest(mcpPath, null, FileImportTrigger.McpAdd));
        Result<FileImportResult> drag = await harness.Service.ImportAsync(
            new FileImportRequest(dragPath, null, FileImportTrigger.DragDrop));

        mcp.Value.Moved.Should().BeFalse();
        drag.Value.Moved.Should().BeTrue();
        File.Exists(mcpPath).Should().BeTrue();
        File.Exists(dragPath).Should().BeFalse();
    }

    [Fact]
    public async Task Import_error_messages_never_carry_host_paths()
    {
        Harness harness = CreateHarness();

        Result<FileImportResult> result = await harness.Service.ImportAsync(
            new FileImportRequest(Path.Combine(_root, "missing-deep", "x.pdf"), null,
                FileImportTrigger.McpAdd));

        result.IsFailure.Should().BeTrue();
        (result.ErrorMessage ?? "").Should().NotContain(_root);
    }

    // ---- fakes ----

    private static class PdfImportResultFactory
    {
        public static PdfImportResult Imported()
        {
            return new PdfImportResult(true, null, "imported",
                "item-1", "asset-1", "doc-1", 12);
        }

        public static PdfImportResult Duplicate()
        {
            return new PdfImportResult(true, null, "already_imported",
                "item-9", "asset-9", "doc-9", 12);
        }

        public static PdfImportResult WithPageFailure()
        {
            return new PdfImportResult(true, null,
                "imported_with_page_failures", "item-1", "asset-1", "doc-1", 12,
                1,
                [new PdfImportPageFailure(3, "page image unreadable")]);
        }

        public static PdfImportResult Failed(string message)
        {
            return new PdfImportResult(false, message, null,
                null, null, null);
        }
    }

    private sealed class FakePdfWorkflow(Func<string, PdfImportResult> handler) : IPdfImportWorkflow
    {
        public int CallCount { get; private set; }
        public string? LastPath { get; private set; }

        public Task<PdfImportResult> ImportPdfAsync(PdfImportRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastPath = request.PdfPath;
            return Task.FromResult(handler(request.PdfPath));
        }
    }

    private sealed class FakeCslInstall(Func<string, Result<CslLocalInstallResult>> handler)
        : ICslLocalFileInstallService
    {
        public Task<Result<CslLocalInstallResult>> InstallFromFileAsync(string path,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(handler(path));
        }
    }

    private sealed class FakeBehaviorSettings(FileHandlingBehaviorSettings settings)
        : IFileHandlingBehaviorSettingsService
    {
        public Task<Result<FileHandlingBehaviorSettings>> GetSettingsAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<FileHandlingBehaviorSettings>.Success(settings));
        }

        public Task<Result<FileHandlingBehaviorSettings>> SaveSettingsAsync(
            FileHandlingBehaviorSettings value, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<FileHandlingBehaviorSettings>.Success(value));
        }
    }

    private sealed class FakeFileResolution(IReadOnlyList<FileSearchRoot> roots) : IFileResolutionService
    {
        public Task<Result<IReadOnlyList<FileSearchRoot>>> ListSearchRootsAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<FileSearchRoot>>.Success(roots));
        }

        public Task<Result<FileSearchRoot>> AddSearchRootAsync(SelectedFileSearchRoot selectedRoot,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<FileSearchRoot>> BindSearchRootAsync(FileSearchRootId rootId,
            SelectedFileSearchRoot selectedRoot, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> DeleteSearchRootAsync(FileSearchRootId rootId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> SetSearchRootAvailabilityAsync(FileSearchRootId rootId, bool isAvailable,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> SetSearchRootOrderAsync(IReadOnlyList<FileSearchRootId> orderedRootIds,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<IReadOnlyList<KnownFileLocation>>> ListKnownLocationsAsync(
            FileAssetId fileAssetId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<FileResolutionResult>> ResolveFileAsync(FileAssetId fileAssetId,
            ResolveFilePurpose purpose, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<FileAsset>> ConfirmMovedCandidateAsync(FileAssetId fileAssetId,
            string selectedPath, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<FileAsset>> RebindSourceAsync(FileAssetId fileAssetId, string selectedPath,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<FileAsset>> ConfirmChangedFileAsync(FileAssetId fileAssetId,
            string selectedPath, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> ReuseRevisionForNewFingerprintAsync(FileAssetId fileAssetId,
            string selectedPath, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> KeepOldEvidenceAsync(FileAssetId fileAssetId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> MarkFileMissingAsync(FileAssetId fileAssetId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeFingerprints : IFileFingerprintService
    {
        public Task<Result<FileFingerprint>> GetFileMetadataAsync(string path,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<string>> ComputeQuickHashAsync(string path,
            CancellationToken cancellationToken = default)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                return Task.FromResult(Result<string>.Success(hash));
            }
            catch (IOException exception)
            {
                return Task.FromResult(Result<string>.Failure(
                    FileImportErrorCodes.UnreadableFile, exception.Message));
            }
        }
    }

    private sealed class FakeRecoveryStore : IFileImportRecoveryStore
    {
        public readonly List<FileImportRecoveryRecord> Records = [];

        public Task<Result> RecordAsync(FileImportRecoveryRecord record,
            CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<IReadOnlyList<FileImportRecoveryRecord>>> ListRecordsAsync(int limit = 50,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<FileImportRecoveryRecord>>.Success(Records));
        }
    }
}
