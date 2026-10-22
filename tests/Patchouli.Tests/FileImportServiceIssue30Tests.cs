using System.Security.Cryptography;
using System.Text;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Csl;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Settings;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Import;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

/// <summary>
/// Issue #30, plan test items 7 (service level) and 9: an explicit import holds
/// the shared path claims — source and move target — for its whole lifetime, so
/// no other importer can enter either path mid-import; and the recovery after a
/// post-move failure or cancellation respects the commit boundary: uncommitted
/// imports restore the file, but once the database references the moved
/// location the file stays where the database expects it.
/// </summary>
public sealed class FileImportServiceIssue30Tests : IDisposable
{
    private const string PdfBytes = "%PDF-1.7\n1 0 obj\n<<>>\nendobj\n";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"issue30-import-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        TestTempFileCleanup.DeleteDirectoryWithRetry(_root);
    }

    [Fact]
    public async Task Import_holds_claims_on_source_and_target_for_the_whole_import()
    {
        FileSearchRoot first = SearchRoot("first");
        FileOperationCoordinator coordinator = new();
        TaskCompletionSource importEntered = NewSignal();
        TaskCompletionSource releaseImport = NewSignal();
        Harness harness = CreateHarness(
            coordinator,
            committed: null,
            pdfHandler: async _ =>
            {
                importEntered.TrySetResult();
                await releaseImport.Task;
                return PdfImportResultFactory.Imported();
            },
            roots: [first]);
        string source = WriteFile("claimed.pdf", PdfBytes);
        string target = Path.Combine(first.RootPath, "claimed.pdf");

        Task<Result<FileImportResult>> importing = harness.Service.ImportAsync(
            new FileImportRequest(source, null, FileImportTrigger.McpAdd));
        await importEntered.Task.WaitAsync(Timeout);

        coordinator.IsClaimed(source).Should().BeTrue("the add owns its source path");
        coordinator.IsClaimed(target).Should().BeTrue(
            "the move target was added to the claim before the file was touched");
        coordinator.TryClaim([target]).Should().BeNull(
            "the watcher cannot take the target while the add is in flight");
        coordinator.TryClaim([source]).Should().BeNull();

        releaseImport.TrySetResult();
        Result<FileImportResult> result = await importing.WaitAsync(Timeout);

        result.IsSuccess.Should().BeTrue();
        harness.Pdf.CallCount.Should().Be(1, "exactly one importer entered the path");
        harness.Pdf.LastPath.Should().Be(target);
        coordinator.IsClaimed(source).Should().BeFalse("claims are released when the import finishes");
        coordinator.IsClaimed(target).Should().BeFalse();
    }

    [Fact]
    public async Task Cancellation_after_move_restores_the_file_and_releases_the_claims()
    {
        FileSearchRoot first = SearchRoot("first");
        FileOperationCoordinator coordinator = new();
        using CancellationTokenSource cancelling = new();
        CancellationToken token = cancelling.Token;
        Harness harness = CreateHarness(
            coordinator,
            committed: new FakeCommittedLookup(),
            pdfHandler: _ =>
            {
                // ReSharper disable once AccessToDisposedClosure -- asserted before disposal.
                cancelling.Cancel();
                throw new OperationCanceledException(token);
            },
            roots: [first]);
        string source = WriteFile("cancel-uncommitted.pdf", PdfBytes);
        string target = Path.Combine(first.RootPath, "cancel-uncommitted.pdf");

        Func<Task> act = () => harness.Service.ImportAsync(
            new FileImportRequest(source, null, FileImportTrigger.McpAdd), token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(source).Should().BeTrue("an uncommitted import restores the file");
        File.Exists(target).Should().BeFalse();
        harness.Recovery.Records.Should().ContainSingle(record =>
            record.Status == FileImportRecoveryStatuses.ImportFailedRestored);
        coordinator.IsClaimed(source).Should().BeFalse();
        coordinator.IsClaimed(target).Should().BeFalse();
    }

    [Fact]
    public async Task Cancellation_after_commit_keeps_the_file_where_the_database_points()
    {
        FileSearchRoot first = SearchRoot("first");
        FileOperationCoordinator coordinator = new();
        FakeCommittedLookup lookup = new();
        using CancellationTokenSource cancelling = new();
        CancellationToken token = cancelling.Token;
        Harness harness = CreateHarness(
            coordinator,
            committed: lookup,
            pdfHandler: _ =>
            {
                // The commit landed; only afterwards was cancellation observed.
                lookup.Committed = true;
                // ReSharper disable once AccessToDisposedClosure -- asserted before disposal.
                cancelling.Cancel();
                throw new OperationCanceledException(token);
            },
            roots: [first]);
        string source = WriteFile("cancel-committed.pdf", PdfBytes);
        string target = Path.Combine(first.RootPath, "cancel-committed.pdf");

        Func<Task> act = () => harness.Service.ImportAsync(
            new FileImportRequest(source, null, FileImportTrigger.McpAdd), token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(target).Should().BeTrue(
            "the database already references the moved location, so the file must stay there");
        File.Exists(source).Should().BeFalse();
        harness.Recovery.Records.Should().ContainSingle(record =>
            record.Status == FileImportRecoveryStatuses.ImportFailedKeptAtMovedLocation);
        coordinator.IsClaimed(source).Should().BeFalse();
        coordinator.IsClaimed(target).Should().BeFalse();
    }

    [Fact]
    public async Task Failure_after_commit_keeps_the_file_and_reports_the_kept_location()
    {
        FileSearchRoot first = SearchRoot("first");
        FileOperationCoordinator coordinator = new();
        FakeCommittedLookup lookup = new();
        Harness harness = CreateHarness(
            coordinator,
            committed: lookup,
            pdfHandler: _ =>
            {
                lookup.Committed = true;
                return Task.FromResult(PdfImportResultFactory.Failed("boom"));
            },
            roots: [first]);
        string source = WriteFile("fail-committed.pdf", PdfBytes);
        string target = Path.Combine(first.RootPath, "fail-committed.pdf");

        Result<FileImportResult> result = await harness.Service.ImportAsync(
            new FileImportRequest(source, null, FileImportTrigger.McpAdd));

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Contain("kept");
        File.Exists(target).Should().BeTrue();
        File.Exists(source).Should().BeFalse();
        harness.Recovery.Records.Should().ContainSingle(record =>
            record.Status == FileImportRecoveryStatuses.ImportFailedKeptAtMovedLocation);
    }

    [Fact]
    public async Task Failure_before_commit_restores_the_file_even_with_a_lookup_wired()
    {
        FileSearchRoot first = SearchRoot("first");
        FileOperationCoordinator coordinator = new();
        Harness harness = CreateHarness(
            coordinator,
            committed: new FakeCommittedLookup(),
            pdfHandler: _ => Task.FromResult(PdfImportResultFactory.Failed("boom")),
            roots: [first]);
        string source = WriteFile("fail-uncommitted.pdf", PdfBytes);
        string target = Path.Combine(first.RootPath, "fail-uncommitted.pdf");

        Result<FileImportResult> result = await harness.Service.ImportAsync(
            new FileImportRequest(source, null, FileImportTrigger.McpAdd));

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Contain("restored");
        File.Exists(source).Should().BeTrue();
        File.Exists(target).Should().BeFalse();
        harness.Recovery.Records.Should().ContainSingle(record =>
            record.Status == FileImportRecoveryStatuses.ImportFailedRestored);
    }

    [Fact]
    public async Task Failure_recovery_completes_even_when_the_caller_cancels_during_the_committed_probe()
    {
        FileSearchRoot first = SearchRoot("first");
        FileOperationCoordinator coordinator = new();
        using CancellationTokenSource cancelling = new();
        CancellationToken token = cancelling.Token;
        Harness harness = CreateHarness(
            coordinator,
            committed: new CancellingCommittedLookup(cancelling),
            pdfHandler: _ => Task.FromResult(PdfImportResultFactory.Failed("boom")),
            roots: [first]);
        string source = WriteFile("fail-cancel-during-recovery.pdf", PdfBytes);
        string target = Path.Combine(first.RootPath, "fail-cancel-during-recovery.pdf");

        // The probe cancels the caller's token mid-recovery (and would throw if the
        // token it received were the caller's). Recovery runs on a non-cancellable
        // token, so the file is still restored and the recovery is still recorded
        // instead of the file being stranded at the target with no record.
        Result<FileImportResult> result = await harness.Service.ImportAsync(
            new FileImportRequest(source, null, FileImportTrigger.McpAdd), token);

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Contain("restored");
        File.Exists(source).Should().BeTrue("recovery completed despite the cancellation");
        File.Exists(target).Should().BeFalse();
        harness.Recovery.Records.Should().ContainSingle(record =>
            record.Status == FileImportRecoveryStatuses.ImportFailedRestored);
    }

    [Fact]
    public async Task Committed_location_lookup_matches_paths_with_platform_case_semantics()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        MigrationRunner runner = new(database.ConnectionFactory, TestPaths.MigrationsDirectory);
        await runner.RunAsync();
        FixedClock clock = new(new DateTimeOffset(2026, 6, 19, 1, 0, 0, TimeSpan.Zero));
        LibraryIdentityService library = new(database.ConnectionFactory, clock);
        LibraryMetadata metadata = (await library.CreateLibraryAsync("case-semantics")).Value;

        string committedPath = Path.Combine(_root, "Committed.PDF");
        string assetId = Guid.NewGuid().ToString();
        string timestamp = clock.UtcNow.ToString("O");
        await using (SqliteConnection connection = database.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into file_assets (
                    file_asset_id, library_id, original_path, file_name, size_bytes, status,
                    created_at, updated_at
                )
                values (@FileAssetId, @LibraryId, @OriginalPath, @FileName, 100, 'available',
                    @CreatedAt, @CreatedAt);
                insert into known_file_locations (location_id, file_asset_id, path, last_seen_at, status)
                values (@LocationId, @FileAssetId, @OriginalPath, @CreatedAt, 'available');
                """,
                new
                {
                    FileAssetId = assetId,
                    LibraryId = metadata.LibraryId.Value.ToString(),
                    OriginalPath = committedPath,
                    FileName = Path.GetFileName(committedPath),
                    LocationId = Guid.NewGuid().ToString(),
                    CreatedAt = timestamp
                });
        }

        CommittedFileLocationLookup lookup = new(database.ConnectionFactory);

        (await lookup.HasCommittedLocationAsync(committedPath)).Should().BeTrue(
            "the exact committed path is found");
        (await lookup.HasCommittedLocationAsync(Path.Combine(_root, "committed.pdf"))).Should()
            .Be(OperatingSystem.IsWindows(),
                "path case is significant exactly where the platform says it is: " +
                "on Linux committed.PDF and committed.pdf are different files and the " +
                "lookup must agree with the claim coordinator's Ordinal semantics");
    }

    // ---- harness & fakes ----

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private string WriteFile(string relativePath, string content)
    {
        string full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, Encoding.ASCII);
        return full;
    }

    private FileSearchRoot SearchRoot(string name)
    {
        string dir = Directory.CreateDirectory(Path.Combine(_root, "roots", name)).FullName;
        return new FileSearchRoot(FileSearchRootId.New(), LibraryId.New(), dir, true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    private Harness CreateHarness(
        FileOperationCoordinator coordinator,
        ICommittedFileLocationLookup? committed,
        Func<string, Task<PdfImportResult>> pdfHandler,
        IReadOnlyList<FileSearchRoot> roots)
    {
        FakePdfWorkflow pdf = new(pdfHandler);
        FakeCslInstall csl = new();
        FakeBehaviorSettings behavior = new(new FileHandlingBehaviorSettings(
            FileHandlingBehavior.MoveToFirstSearchRoot, FileHandlingBehavior.MoveToFirstSearchRoot));
        FakeFileResolution resolution = new(roots);
        FakeFingerprints fingerprints = new();
        FakeRecoveryStore recovery = new();
        FileImportService service = new(pdf, csl, behavior, resolution, fingerprints,
            recovery, new SystemClock(), coordinator, committed);
        return new Harness(service, pdf, recovery);
    }

    private sealed record Harness(
        FileImportService Service,
        FakePdfWorkflow Pdf,
        FakeRecoveryStore Recovery);

    private static class PdfImportResultFactory
    {
        public static PdfImportResult Imported()
        {
            return new PdfImportResult(true, null, "imported",
                "item-1", "asset-1", "doc-1", 12);
        }

        public static PdfImportResult Failed(string message)
        {
            return new PdfImportResult(false, message, null,
                null, null, null);
        }
    }

    private sealed class FakePdfWorkflow(Func<string, Task<PdfImportResult>> handler) : IPdfImportWorkflow
    {
        public int CallCount { get; private set; }
        public string? LastPath { get; private set; }

        public Task<PdfImportResult> ImportPdfAsync(PdfImportRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastPath = request.PdfPath;
            return handler(request.PdfPath);
        }
    }

    private sealed class FakeCommittedLookup : ICommittedFileLocationLookup
    {
        public bool Committed { get; set; }

        public Task<bool> HasCommittedLocationAsync(string path,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Committed);
        }
    }

    private sealed class CancellingCommittedLookup(CancellationTokenSource cancelling)
        : ICommittedFileLocationLookup
    {
        public Task<bool> HasCommittedLocationAsync(string path,
            CancellationToken cancellationToken = default)
        {
            // The caller's request is cancelled while recovery is probing. If the token
            // handed to the probe were the caller's, this throw would abort recovery
            // before the restore and its durable record.
            cancelling.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }
    }

    private sealed class FakeCslInstall : ICslLocalFileInstallService
    {
        public Task<Result<CslLocalInstallResult>> InstallFromFileAsync(string path,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
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

        public Task<Result<FileAsset>> RebindSourceAsync(FileAssetId fileAssetId,
            string selectedPath, CancellationToken cancellationToken = default)
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
