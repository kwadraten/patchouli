using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Conflicts;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Operations;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Operations;

namespace Patchouli.Tests;

public sealed class FileResolutionServiceTests
{
    [Fact]
    public async Task RegisterFile_existing_file_adds_known_location()
    {
        await using FileResolutionTestContext context = await FileResolutionTestContext.CreateAsync();
        string file = await context.Temp.WriteFileAsync("source.pdf", "content");

        Result<FileAsset> asset = await context.FileAssetService.RegisterFileAsync(file);
        Result<IReadOnlyList<KnownFileLocation>> locations =
            await context.FileResolutionService.ListKnownLocationsAsync(asset.Value.FileAssetId);

        locations.Value.Should().ContainSingle(location =>
            location.Path == Path.GetFullPath(file) &&
            location.Status == FileAssetStatus.Available);
    }

    [Fact]
    public async Task ResolveFile_original_path_available_returns_exact()
    {
        await using FileResolutionTestContext context = await FileResolutionTestContext.CreateAsync();
        string file = await context.Temp.WriteFileAsync("available.pdf", "same content");
        Result<FileAsset> asset = await context.FileAssetService.RegisterFileAsync(file);

        Result<FileResolutionResult> result =
            await context.FileResolutionService.ResolveFileAsync(asset.Value.FileAssetId,
                ResolveFilePurpose.OpenOriginal);

        result.Value.Status.Should().Be(FileAssetStatus.Available);
        result.Value.ResolvedPath.Should().Be(Path.GetFullPath(file));
        result.Value.Confidence.Should().Be(FileResolutionConfidence.Exact);
        result.Value.RequiredAction.Should().Be(FileResolutionRequiredAction.None);
    }

    [Fact]
    public async Task ResolveFile_materializes_the_original_path_before_fingerprinting()
    {
        TrackingRootAccess rootAccess = new();
        await using FileResolutionTestContext context = await FileResolutionTestContext.CreateAsync(rootAccess);
        string file = await context.Temp.WriteFileAsync("icloud.pdf", "same content");
        Result<FileAsset> asset = await context.FileAssetService.RegisterFileAsync(file);

        Result<FileResolutionResult> result = await context.FileResolutionService.ResolveFileAsync(
            asset.Value.FileAssetId, ResolveFilePurpose.RenderPage);

        result.Value.Status.Should().Be(FileAssetStatus.Available);
        rootAccess.MaterializedPaths.Should().Contain(Path.GetFullPath(file));
    }

    [Fact]
    public async Task ResolveFile_original_path_changed_returns_changed_and_requires_confirmation()
    {
        await using FileResolutionTestContext context = await FileResolutionTestContext.CreateAsync();
        string file = await context.Temp.WriteFileAsync("changed.pdf", "first content");
        Result<FileAsset> asset = await context.FileAssetService.RegisterFileAsync(file);
        await File.WriteAllTextAsync(file, "changed content");

        Result<FileResolutionResult> result =
            await context.FileResolutionService.ResolveFileAsync(asset.Value.FileAssetId,
                ResolveFilePurpose.OpenOriginal);

        result.Value.Status.Should().Be(FileAssetStatus.Changed);
        result.Value.ResolvedPath.Should().BeNull();
        result.Value.RequiredAction.Should().Be(FileResolutionRequiredAction.ConfirmChangedFile);
        result.Value.Conflicts.Should().ContainSingle(conflict =>
            conflict.ConflictCode == ConflictCode.SourceFileChangedOrBBoxBasisStale);
    }

    [Fact]
    public async Task ResolveFile_missing_original_but_found_in_search_root_returns_moved_candidate()
    {
        await using FileResolutionTestContext context = await FileResolutionTestContext.CreateAsync();
        string original = await context.Temp.WriteFileAsync("moved.pdf", "move me");
        Result<FileAsset> asset = await context.FileAssetService.RegisterFileAsync(original);
        File.Delete(original);
        string root = context.Temp.CreateDirectory("root");
        string moved = Path.Combine(root, "moved.pdf");
        await File.WriteAllTextAsync(moved, "move me");
        await context.FileResolutionService.AddSearchRootAsync(SelectedRoot(root));

        Result<FileResolutionResult> result =
            await context.FileResolutionService.ResolveFileAsync(asset.Value.FileAssetId,
                ResolveFilePurpose.OpenOriginal);

        result.Value.Status.Should().Be(FileAssetStatus.MovedCandidate);
        result.Value.ResolvedPath.Should().Be(Path.GetFullPath(moved));
        result.Value.RequiredAction.Should().Be(FileResolutionRequiredAction.None);
        result.Value.Candidates.Should().ContainSingle();
    }

    [Fact]
    public async Task ResolveFile_multiple_matching_candidates_returns_conflict()
    {
        await using FileResolutionTestContext context = await FileResolutionTestContext.CreateAsync();
        string original = await context.Temp.WriteFileAsync("duplicate.pdf", "duplicate");
        Result<FileAsset> asset = await context.FileAssetService.RegisterFileAsync(original);
        File.Delete(original);
        string root = context.Temp.CreateDirectory("roots");
        Directory.CreateDirectory(Path.Combine(root, "a"));
        Directory.CreateDirectory(Path.Combine(root, "b"));
        await File.WriteAllTextAsync(Path.Combine(root, "a", "duplicate.pdf"), "duplicate");
        await File.WriteAllTextAsync(Path.Combine(root, "b", "duplicate.pdf"), "duplicate");
        await context.FileResolutionService.AddSearchRootAsync(SelectedRoot(root));

        Result<FileResolutionResult> result =
            await context.FileResolutionService.ResolveFileAsync(asset.Value.FileAssetId,
                ResolveFilePurpose.OpenOriginal);

        result.Value.Status.Should().Be(FileAssetStatus.Conflict);
        result.Value.ResolvedPath.Should().BeNull();
        result.Value.RequiredAction.Should().Be(FileResolutionRequiredAction.ChooseCandidate);
        result.Value.Candidates.Should().HaveCount(2);
        result.Value.Conflicts.Should().ContainSingle(conflict =>
            conflict.ConflictCode == ConflictCode.FileRelocationMultipleCandidates);
    }

    [Fact]
    public async Task ResolveFile_no_candidates_returns_missing()
    {
        await using FileResolutionTestContext context = await FileResolutionTestContext.CreateAsync();
        string original = await context.Temp.WriteFileAsync("missing.pdf", "gone");
        Result<FileAsset> asset = await context.FileAssetService.RegisterFileAsync(original);
        File.Delete(original);

        Result<FileResolutionResult> result =
            await context.FileResolutionService.ResolveFileAsync(asset.Value.FileAssetId,
                ResolveFilePurpose.OpenOriginal);

        result.Value.Status.Should().Be(FileAssetStatus.Missing);
        result.Value.RequiredAction.Should().Be(FileResolutionRequiredAction.LocateManually);
    }

    [Fact]
    public async Task ResolveFile_all_roots_offline_returns_offline_root()
    {
        await using FileResolutionTestContext context = await FileResolutionTestContext.CreateAsync();
        string original = await context.Temp.WriteFileAsync("offline.pdf", "offline");
        Result<FileAsset> asset = await context.FileAssetService.RegisterFileAsync(original);
        File.Delete(original);
        Result<FileSearchRoot> root =
            await context.FileResolutionService.AddSearchRootAsync(
                SelectedRoot(context.Temp.CreateDirectory("offline-root")));
        await context.FileResolutionService.SetSearchRootAvailabilityAsync(root.Value.RootId, false);

        Result<FileResolutionResult> result =
            await context.FileResolutionService.ResolveFileAsync(asset.Value.FileAssetId,
                ResolveFilePurpose.OpenOriginal);

        result.Value.Status.Should().Be(FileAssetStatus.OfflineRoot);
        result.Value.RequiredAction.Should().Be(FileResolutionRequiredAction.ReconnectOfflineRoot);
    }

    [Fact]
    public async Task ConfirmMovedCandidate_updates_status_and_known_location()
    {
        await using FileResolutionTestContext context = await FileResolutionTestContext.CreateAsync();
        string original = await context.Temp.WriteFileAsync("confirm.pdf", "confirm");
        Result<FileAsset> asset = await context.FileAssetService.RegisterFileAsync(original);
        File.Delete(original);
        string moved = await context.Temp.WriteFileAsync(Path.Combine("new", "confirm.pdf"), "confirm");

        Result<FileAsset> confirmed =
            await context.FileResolutionService.ConfirmMovedCandidateAsync(asset.Value.FileAssetId, moved);
        Result<IReadOnlyList<KnownFileLocation>> locations =
            await context.FileResolutionService.ListKnownLocationsAsync(asset.Value.FileAssetId);

        confirmed.Value.Status.Should().Be(FileAssetStatus.Available);
        confirmed.Value.OriginalPath.Should().Be(Path.GetFullPath(moved));
        locations.Value.Should().Contain(location => location.Path == Path.GetFullPath(moved));
    }

    [Fact]
    public async Task MarkFileMissing_does_not_delete_item_or_document_instance()
    {
        await using FileResolutionTestContext context = await FileResolutionTestContext.CreateAsync();
        Result<ItemMetadata> item = await context.ItemService.CreateItemAsync("book", "Book");
        string file = await context.Temp.WriteFileAsync("source.pdf", "source");
        Result<FileAsset> asset = await context.FileAssetService.RegisterFileAsync(file);
        Result<DocumentInstance> instance = await context.DocumentInstanceService.AttachDocumentInstanceAsync(
            item.Value.ItemId,
            asset.Value.FileAssetId,
            DocumentInstanceType.PrimaryScan);

        Result marked = await context.FileResolutionService.MarkFileMissingAsync(asset.Value.FileAssetId);
        Result<ItemMetadata> itemAfter = await context.ItemService.GetItemAsync(item.Value.ItemId);
        Result<DocumentInstance> instanceAfter =
            await context.DocumentInstanceService.GetDocumentInstanceAsync(instance.Value.DocumentInstanceId);

        marked.IsSuccess.Should().BeTrue();
        itemAfter.IsSuccess.Should().BeTrue();
        instanceAfter.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveFile_does_not_store_file_content_in_database()
    {
        await using FileResolutionTestContext context = await FileResolutionTestContext.CreateAsync();
        string content = $"secret-file-content-{Guid.NewGuid():N}";
        string file = await context.Temp.WriteFileAsync("secret.pdf", content);
        Result<FileAsset> asset = await context.FileAssetService.RegisterFileAsync(file);

        await context.FileResolutionService.ResolveFileAsync(asset.Value.FileAssetId, ResolveFilePurpose.VerifyHash);

        await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        (string Name, string Type)[] columns = (await connection.QueryAsync<(string Name, string Type)>(
            """
            select name as Name, type as Type from pragma_table_info('file_assets')
            union all
            select name as Name, type as Type from pragma_table_info('known_file_locations');
            """)).ToArray();

        columns.Select(c => c.Name).Should()
            .NotContain(name => name.Contains("content", StringComparison.OrdinalIgnoreCase));
        columns.Select(c => c.Type).Should()
            .NotContain(type => type.Contains("blob", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AddSearchRoot_rejects_blank_path()
    {
        await using FileResolutionTestContext context = await FileResolutionTestContext.CreateAsync();

        Result<FileSearchRoot> result = await context.FileResolutionService.AddSearchRootAsync(SelectedRoot(" "));

        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task AddSearchRoot_rejects_duplicate_root_for_same_library()
    {
        await using FileResolutionTestContext context = await FileResolutionTestContext.CreateAsync();
        string root = context.Temp.CreateDirectory("same-root");

        Result<FileSearchRoot> first = await context.FileResolutionService.AddSearchRootAsync(SelectedRoot(root));
        Result<FileSearchRoot> second = await context.FileResolutionService.AddSearchRootAsync(SelectedRoot(root));

        first.IsSuccess.Should().BeTrue();
        second.ErrorCode.Should().Be(AppErrorCodes.InvalidState);
    }

    [Fact]
    public async Task DeleteSearchRoot_removes_registered_root()
    {
        await using FileResolutionTestContext context = await FileResolutionTestContext.CreateAsync();
        string root = context.Temp.CreateDirectory("delete-root");
        Result<FileSearchRoot> added = await context.FileResolutionService.AddSearchRootAsync(SelectedRoot(root));

        Result deleted = await context.FileResolutionService.DeleteSearchRootAsync(added.Value.RootId);
        Result<IReadOnlyList<FileSearchRoot>> roots = await context.FileResolutionService.ListSearchRootsAsync();

        deleted.IsSuccess.Should().BeTrue();
        roots.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task AddSearchRoot_records_completed_scan_operation_for_available_root()
    {
        await using FileResolutionTestContext context = await FileResolutionTestContext.CreateAsync();
        string root = context.Temp.CreateDirectory("scan-root");
        await context.Temp.WriteFileAsync(Path.Combine("scan-root", "one.pdf"), "one");
        await context.Temp.WriteFileAsync(Path.Combine("scan-root", "nested", "two.pdf"), "two");

        Result<FileSearchRoot> added = await context.FileResolutionService.AddSearchRootAsync(SelectedRoot(root));
        Result<IReadOnlyList<BlockingOperation>> operations = await context.BlockingOperations.ListAsync(
            BlockingOperationStatus.Completed,
            BlockingOperationTypes.FileSearchRootScan,
            BlockingOperationScopeTypes.FileSearchRoot,
            Path.GetFullPath(root));

        added.IsSuccess.Should().BeTrue();
        added.Value.IsAvailable.Should().BeTrue();
        operations.IsSuccess.Should().BeTrue();
        operations.Value.Should().ContainSingle();
        operations.Value.Single().ProgressLabel.Should().Contain("Scanned 2 file");
    }

    [Fact]
    public async Task AddSearchRoot_records_failed_scan_operation_for_unavailable_root()
    {
        await using FileResolutionTestContext context = await FileResolutionTestContext.CreateAsync();
        string root = Path.Combine(context.Temp.Path, "missing-root");

        Result<FileSearchRoot> added = await context.FileResolutionService.AddSearchRootAsync(SelectedRoot(root));
        Result<IReadOnlyList<BlockingOperation>> operations = await context.BlockingOperations.ListAsync(
            BlockingOperationStatus.Failed,
            BlockingOperationTypes.FileSearchRootScan,
            BlockingOperationScopeTypes.FileSearchRoot,
            Path.GetFullPath(root));

        added.IsSuccess.Should().BeTrue();
        added.Value.IsAvailable.Should().BeFalse();
        operations.IsSuccess.Should().BeTrue();
        operations.Value.Should().ContainSingle();
        operations.Value.Single().FailureCode.Should().Be(AppErrorCodes.NotFound);
    }

    [Fact]
    public async Task ListKnownLocations_returns_registered_locations()
    {
        await using FileResolutionTestContext context = await FileResolutionTestContext.CreateAsync();
        string file = await context.Temp.WriteFileAsync("known.pdf", "known");
        Result<FileAsset> asset = await context.FileAssetService.RegisterFileAsync(file);

        Result<IReadOnlyList<KnownFileLocation>> locations =
            await context.FileResolutionService.ListKnownLocationsAsync(asset.Value.FileAssetId);

        locations.Value.Should().ContainSingle(location => location.Path == Path.GetFullPath(file));
    }

    [Fact]
    public async Task ResolveFile_ignores_unavailable_search_roots()
    {
        await using FileResolutionTestContext context = await FileResolutionTestContext.CreateAsync();
        string original = await context.Temp.WriteFileAsync("ignored.pdf", "ignored");
        Result<FileAsset> asset = await context.FileAssetService.RegisterFileAsync(original);
        File.Delete(original);
        string offlineRoot = context.Temp.CreateDirectory("offline-with-match");
        await File.WriteAllTextAsync(Path.Combine(offlineRoot, "ignored.pdf"), "ignored");
        string onlineRoot = context.Temp.CreateDirectory("online-empty");
        Result<FileSearchRoot> offline =
            await context.FileResolutionService.AddSearchRootAsync(SelectedRoot(offlineRoot));
        await context.FileResolutionService.SetSearchRootAvailabilityAsync(offline.Value.RootId, false);
        await context.FileResolutionService.AddSearchRootAsync(SelectedRoot(onlineRoot));

        Result<FileResolutionResult> result =
            await context.FileResolutionService.ResolveFileAsync(asset.Value.FileAssetId,
                ResolveFilePurpose.OpenOriginal);

        result.Value.Status.Should().Be(FileAssetStatus.Missing);
        result.Value.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task Changed_file_does_not_auto_update_original_path()
    {
        await using FileResolutionTestContext context = await FileResolutionTestContext.CreateAsync();
        string file = await context.Temp.WriteFileAsync("changed-path.pdf", "before");
        Result<FileAsset> asset = await context.FileAssetService.RegisterFileAsync(file);
        await File.WriteAllTextAsync(file, "after");

        Result<FileResolutionResult> resolution =
            await context.FileResolutionService.ResolveFileAsync(asset.Value.FileAssetId,
                ResolveFilePurpose.OpenOriginal);
        Result<FileAsset> current = await context.FileAssetService.GetFileAssetAsync(asset.Value.FileAssetId);

        resolution.Value.Status.Should().Be(FileAssetStatus.Changed);
        current.Value.OriginalPath.Should().Be(Path.GetFullPath(file));
    }

    [Fact]
    public async Task Reusing_a_revision_rolls_back_the_file_change_when_the_new_source_lacks_a_full_fingerprint()
    {
        await using FileResolutionTestContext context = await FileResolutionTestContext.CreateAsync();
        string original = await context.Temp.WriteFileAsync("rollback.pdf", "original");
        Result<FileAsset> asset = await context.FileAssetService.RegisterFileAsync(original);
        string replacement = Path.Combine(context.Temp.Path, "replacement.pdf");
        FileResolutionService service = new(
            context.Database.ConnectionFactory,
            new LibraryIdentityService(context.Database.ConnectionFactory,
                new FixedClock(new DateTimeOffset(2026, 6, 19, 3, 0, 0, TimeSpan.Zero))),
            new FixedClock(new DateTimeOffset(2026, 6, 19, 3, 0, 0, TimeSpan.Zero)),
            new MissingFullFingerprintService(new FileFingerprint(
                replacement,
                "replacement.pdf",
                11,
                DateTimeOffset.Parse("2026-06-19T03:00:00Z"),
                "quick",
                null)),
            context.BlockingOperations);

        Result reused = await service.ReuseRevisionForNewFingerprintAsync(asset.Value.FileAssetId, replacement);
        Result<FileAsset> current = await context.FileAssetService.GetFileAssetAsync(asset.Value.FileAssetId);

        reused.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        current.Value.OriginalPath.Should().Be(Path.GetFullPath(original));
        current.Value.FullBlake3.Should().Be(asset.Value.FullBlake3);
    }

    private static SelectedFileSearchRoot SelectedRoot(string path)
    {
        return new SelectedFileSearchRoot(
            path,
            "test",
            FileSearchRootAuthorizationKinds.None,
            null,
            null,
            DateTimeOffset.UtcNow);
    }

    private sealed class FileResolutionTestContext : IAsyncDisposable
    {
        private FileResolutionTestContext(
            TemporarySqliteDatabase database,
            TemporaryDirectory temp,
            ItemService itemService,
            FileAssetService fileAssetService,
            DocumentInstanceService documentInstanceService,
            FileResolutionService fileResolutionService,
            IBlockingOperationService blockingOperations)
        {
            Database = database;
            Temp = temp;
            ItemService = itemService;
            FileAssetService = fileAssetService;
            DocumentInstanceService = documentInstanceService;
            FileResolutionService = fileResolutionService;
            BlockingOperations = blockingOperations;
        }

        public TemporarySqliteDatabase Database { get; }
        public TemporaryDirectory Temp { get; }
        public ItemService ItemService { get; }
        public FileAssetService FileAssetService { get; }
        public DocumentInstanceService DocumentInstanceService { get; }
        public FileResolutionService FileResolutionService { get; }
        public IBlockingOperationService BlockingOperations { get; }

        public static async Task<FileResolutionTestContext> CreateAsync(IFileSearchRootAccess? rootAccess = null)
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            TemporaryDirectory temp = TemporaryDirectory.Create();
            FixedClock clock = new(new DateTimeOffset(2026, 6, 19, 3, 0, 0, TimeSpan.Zero));
            MigrationRunner runner = new(database.ConnectionFactory, TestPaths.MigrationsDirectory);
            await runner.RunAsync();

            LibraryIdentityService libraryService = new(database.ConnectionFactory, clock);
            await libraryService.CreateLibraryAsync("File resolution library");
            FileFingerprintService fingerprintService = new();
            BlockingOperationService blockingOperations = new(database.ConnectionFactory, clock);
            ItemService itemService = new(database.ConnectionFactory, libraryService, clock);
            FileAssetService fileAssetService =
                new(database.ConnectionFactory, libraryService, clock, fingerprintService);
            DocumentInstanceService documentInstanceService = new(database.ConnectionFactory, clock);
            FileResolutionService fileResolutionService = new(
                database.ConnectionFactory,
                libraryService,
                clock,
                fingerprintService,
                blockingOperations,
                rootAccess);

            return new FileResolutionTestContext(
                database,
                temp,
                itemService,
                fileAssetService,
                documentInstanceService,
                fileResolutionService,
                blockingOperations);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            Temp.Dispose();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            string path =
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"patchouli-resolution-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public string CreateDirectory(string relativePath)
        {
            string path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public async Task<string> WriteFileAsync(string relativePath, string content)
        {
            string path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }

    private sealed class MissingFullFingerprintService(FileFingerprint fingerprint) : IFileFingerprintService
    {
        public Task<Result<FileFingerprint>> GetFileMetadataAsync(string path,
            CancellationToken cancellationToken = default)
        {
            _ = path;
            _ = cancellationToken;
            return Task.FromResult(Result<FileFingerprint>.Success(fingerprint));
        }

        public Task<Result<string>> ComputeQuickHashAsync(string path, CancellationToken cancellationToken = default)
        {
            _ = path;
            _ = cancellationToken;
            return Task.FromResult(Result<string>.Success(fingerprint.QuickHash));
        }
    }

    private sealed class TrackingRootAccess : IFileSearchRootAccess
    {
        private readonly FileSearchRootAccess _inner = new();

        public List<string> MaterializedPaths { get; } = [];

        public Task<Result> EnsureAvailableAsync(string path, CancellationToken cancellationToken = default)
        {
            MaterializedPaths.Add(path);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<SelectedFileSearchRoot>> SelectRootAsync(CancellationToken cancellationToken = default)
        {
            return _inner.SelectRootAsync(cancellationToken);
        }

        public Task<Result<ResolvedFileSearchRoot>> ReopenAsync(FileSearchRoot root,
            CancellationToken cancellationToken = default)
        {
            return _inner.ReopenAsync(root, cancellationToken);
        }

        public Task<Result<ResolvedFileSearchRoot>> ResolveSelectedAsync(SelectedFileSearchRoot root,
            CancellationToken cancellationToken = default)
        {
            return _inner.ResolveSelectedAsync(root, cancellationToken);
        }

        public Task<FileSearchRootScanResult> ScanPdfAsync(ResolvedFileSearchRoot root,
            CancellationToken cancellationToken = default)
        {
            return _inner.ScanPdfAsync(root, cancellationToken);
        }

        public Task<FileSearchRootTraversalResult> TraverseAsync(ResolvedFileSearchRoot root,
            CancellationToken cancellationToken = default)
        {
            return _inner.TraverseAsync(root, cancellationToken);
        }
    }
}
