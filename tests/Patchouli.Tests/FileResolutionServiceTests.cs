using Dapper;
using FluentAssertions;
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
        await using var context = await FileResolutionTestContext.CreateAsync();
        var file = await context.Temp.WriteFileAsync("source.pdf", "content");

        var asset = await context.FileAssetService.RegisterFileAsync(file);
        var locations = await context.FileResolutionService.ListKnownLocationsAsync(asset.Value.FileAssetId);

        locations.Value.Should().ContainSingle(location =>
            location.Path == Path.GetFullPath(file) &&
            location.Status == FileAssetStatus.Available);
    }

    [Fact]
    public async Task ResolveFile_original_path_available_returns_exact()
    {
        await using var context = await FileResolutionTestContext.CreateAsync();
        var file = await context.Temp.WriteFileAsync("available.pdf", "same content");
        var asset = await context.FileAssetService.RegisterFileAsync(file);

        var result = await context.FileResolutionService.ResolveFileAsync(asset.Value.FileAssetId, ResolveFilePurpose.OpenOriginal);

        result.Value.Status.Should().Be(FileAssetStatus.Available);
        result.Value.ResolvedPath.Should().Be(Path.GetFullPath(file));
        result.Value.Confidence.Should().Be(FileResolutionConfidence.Exact);
        result.Value.RequiredAction.Should().Be(FileResolutionRequiredAction.None);
    }

    [Fact]
    public async Task ResolveFile_original_path_changed_returns_changed_and_requires_confirmation()
    {
        await using var context = await FileResolutionTestContext.CreateAsync();
        var file = await context.Temp.WriteFileAsync("changed.pdf", "first content");
        var asset = await context.FileAssetService.RegisterFileAsync(file);
        await File.WriteAllTextAsync(file, "changed content");

        var result = await context.FileResolutionService.ResolveFileAsync(asset.Value.FileAssetId, ResolveFilePurpose.OpenOriginal);

        result.Value.Status.Should().Be(FileAssetStatus.Changed);
        result.Value.ResolvedPath.Should().BeNull();
        result.Value.RequiredAction.Should().Be(FileResolutionRequiredAction.ConfirmChangedFile);
        result.Value.Conflicts.Should().ContainSingle(conflict => conflict.ConflictCode == ConflictCode.SourceFileChangedOrBBoxBasisStale);
    }

    [Fact]
    public async Task ResolveFile_missing_original_but_found_in_search_root_returns_moved_candidate()
    {
        await using var context = await FileResolutionTestContext.CreateAsync();
        var original = await context.Temp.WriteFileAsync("moved.pdf", "move me");
        var asset = await context.FileAssetService.RegisterFileAsync(original);
        File.Delete(original);
        var root = context.Temp.CreateDirectory("root");
        var moved = Path.Combine(root, "moved.pdf");
        await File.WriteAllTextAsync(moved, "move me");
        await context.FileResolutionService.AddSearchRootAsync(root);

        var result = await context.FileResolutionService.ResolveFileAsync(asset.Value.FileAssetId, ResolveFilePurpose.OpenOriginal);

        result.Value.Status.Should().Be(FileAssetStatus.MovedCandidate);
        result.Value.ResolvedPath.Should().Be(Path.GetFullPath(moved));
        result.Value.RequiredAction.Should().Be(FileResolutionRequiredAction.None);
        result.Value.Candidates.Should().ContainSingle();
    }

    [Fact]
    public async Task ResolveFile_multiple_matching_candidates_returns_conflict()
    {
        await using var context = await FileResolutionTestContext.CreateAsync();
        var original = await context.Temp.WriteFileAsync("duplicate.pdf", "duplicate");
        var asset = await context.FileAssetService.RegisterFileAsync(original);
        File.Delete(original);
        var root = context.Temp.CreateDirectory("roots");
        Directory.CreateDirectory(Path.Combine(root, "a"));
        Directory.CreateDirectory(Path.Combine(root, "b"));
        await File.WriteAllTextAsync(Path.Combine(root, "a", "duplicate.pdf"), "duplicate");
        await File.WriteAllTextAsync(Path.Combine(root, "b", "duplicate.pdf"), "duplicate");
        await context.FileResolutionService.AddSearchRootAsync(root);

        var result = await context.FileResolutionService.ResolveFileAsync(asset.Value.FileAssetId, ResolveFilePurpose.OpenOriginal);

        result.Value.Status.Should().Be(FileAssetStatus.Conflict);
        result.Value.ResolvedPath.Should().BeNull();
        result.Value.RequiredAction.Should().Be(FileResolutionRequiredAction.ChooseCandidate);
        result.Value.Candidates.Should().HaveCount(2);
        result.Value.Conflicts.Should().ContainSingle(conflict => conflict.ConflictCode == ConflictCode.FileRelocationMultipleCandidates);
    }

    [Fact]
    public async Task ResolveFile_no_candidates_returns_missing()
    {
        await using var context = await FileResolutionTestContext.CreateAsync();
        var original = await context.Temp.WriteFileAsync("missing.pdf", "gone");
        var asset = await context.FileAssetService.RegisterFileAsync(original);
        File.Delete(original);

        var result = await context.FileResolutionService.ResolveFileAsync(asset.Value.FileAssetId, ResolveFilePurpose.OpenOriginal);

        result.Value.Status.Should().Be(FileAssetStatus.Missing);
        result.Value.RequiredAction.Should().Be(FileResolutionRequiredAction.LocateManually);
    }

    [Fact]
    public async Task ResolveFile_all_roots_offline_returns_offline_root()
    {
        await using var context = await FileResolutionTestContext.CreateAsync();
        var original = await context.Temp.WriteFileAsync("offline.pdf", "offline");
        var asset = await context.FileAssetService.RegisterFileAsync(original);
        File.Delete(original);
        var root = await context.FileResolutionService.AddSearchRootAsync(context.Temp.CreateDirectory("offline-root"));
        await context.FileResolutionService.SetSearchRootAvailabilityAsync(root.Value.RootId, isAvailable: false);

        var result = await context.FileResolutionService.ResolveFileAsync(asset.Value.FileAssetId, ResolveFilePurpose.OpenOriginal);

        result.Value.Status.Should().Be(FileAssetStatus.OfflineRoot);
        result.Value.RequiredAction.Should().Be(FileResolutionRequiredAction.ReconnectOfflineRoot);
    }

    [Fact]
    public async Task ConfirmMovedCandidate_updates_status_and_known_location()
    {
        await using var context = await FileResolutionTestContext.CreateAsync();
        var original = await context.Temp.WriteFileAsync("confirm.pdf", "confirm");
        var asset = await context.FileAssetService.RegisterFileAsync(original);
        File.Delete(original);
        var moved = await context.Temp.WriteFileAsync(Path.Combine("new", "confirm.pdf"), "confirm");

        var confirmed = await context.FileResolutionService.ConfirmMovedCandidateAsync(asset.Value.FileAssetId, moved);
        var locations = await context.FileResolutionService.ListKnownLocationsAsync(asset.Value.FileAssetId);

        confirmed.Value.Status.Should().Be(FileAssetStatus.Available);
        confirmed.Value.OriginalPath.Should().Be(Path.GetFullPath(moved));
        locations.Value.Should().Contain(location => location.Path == Path.GetFullPath(moved));
    }

    [Fact]
    public async Task MarkFileMissing_does_not_delete_item_or_document_instance()
    {
        await using var context = await FileResolutionTestContext.CreateAsync();
        var item = await context.ItemService.CreateItemAsync("book", "Book");
        var file = await context.Temp.WriteFileAsync("source.pdf", "source");
        var asset = await context.FileAssetService.RegisterFileAsync(file);
        var instance = await context.DocumentInstanceService.AttachDocumentInstanceAsync(
            item.Value.ItemId,
            asset.Value.FileAssetId,
            DocumentInstanceType.PrimaryScan);

        var marked = await context.FileResolutionService.MarkFileMissingAsync(asset.Value.FileAssetId);
        var itemAfter = await context.ItemService.GetItemAsync(item.Value.ItemId);
        var instanceAfter = await context.DocumentInstanceService.GetDocumentInstanceAsync(instance.Value.DocumentInstanceId);

        marked.IsSuccess.Should().BeTrue();
        itemAfter.IsSuccess.Should().BeTrue();
        instanceAfter.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveFile_does_not_store_file_content_in_database()
    {
        await using var context = await FileResolutionTestContext.CreateAsync();
        var content = $"secret-file-content-{Guid.NewGuid():N}";
        var file = await context.Temp.WriteFileAsync("secret.pdf", content);
        var asset = await context.FileAssetService.RegisterFileAsync(file);

        await context.FileResolutionService.ResolveFileAsync(asset.Value.FileAssetId, ResolveFilePurpose.VerifyHash);

        await using var connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        var columns = (await connection.QueryAsync<(string Name, string Type)>(
            """
            select name as Name, type as Type from pragma_table_info('file_assets')
            union all
            select name as Name, type as Type from pragma_table_info('known_file_locations');
            """)).ToArray();

        columns.Select(c => c.Name).Should().NotContain(name => name.Contains("content", StringComparison.OrdinalIgnoreCase));
        columns.Select(c => c.Type).Should().NotContain(type => type.Contains("blob", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AddSearchRoot_rejects_blank_path()
    {
        await using var context = await FileResolutionTestContext.CreateAsync();

        var result = await context.FileResolutionService.AddSearchRootAsync(" ");

        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task AddSearchRoot_rejects_duplicate_root_for_same_library()
    {
        await using var context = await FileResolutionTestContext.CreateAsync();
        var root = context.Temp.CreateDirectory("same-root");

        var first = await context.FileResolutionService.AddSearchRootAsync(root);
        var second = await context.FileResolutionService.AddSearchRootAsync(root);

        first.IsSuccess.Should().BeTrue();
        second.ErrorCode.Should().Be(AppErrorCodes.InvalidState);
    }

    [Fact]
    public async Task DeleteSearchRoot_removes_registered_root()
    {
        await using var context = await FileResolutionTestContext.CreateAsync();
        var root = context.Temp.CreateDirectory("delete-root");
        var added = await context.FileResolutionService.AddSearchRootAsync(root);

        var deleted = await context.FileResolutionService.DeleteSearchRootAsync(added.Value.RootId);
        var roots = await context.FileResolutionService.ListSearchRootsAsync();

        deleted.IsSuccess.Should().BeTrue();
        roots.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task AddSearchRoot_records_completed_scan_operation_for_available_root()
    {
        await using var context = await FileResolutionTestContext.CreateAsync();
        var root = context.Temp.CreateDirectory("scan-root");
        await context.Temp.WriteFileAsync(Path.Combine("scan-root", "one.pdf"), "one");
        await context.Temp.WriteFileAsync(Path.Combine("scan-root", "nested", "two.pdf"), "two");

        var added = await context.FileResolutionService.AddSearchRootAsync(root);
        var operations = await context.BlockingOperations.ListAsync(
            status: BlockingOperationStatus.Completed,
            operationType: BlockingOperationTypes.FileSearchRootScan,
            scopeType: BlockingOperationScopeTypes.FileSearchRoot,
            scopeId: Path.GetFullPath(root));

        added.IsSuccess.Should().BeTrue();
        added.Value.IsAvailable.Should().BeTrue();
        operations.IsSuccess.Should().BeTrue();
        operations.Value.Should().ContainSingle();
        operations.Value.Single().ProgressLabel.Should().Contain("Scanned 2 file");
    }

    [Fact]
    public async Task AddSearchRoot_records_failed_scan_operation_for_unavailable_root()
    {
        await using var context = await FileResolutionTestContext.CreateAsync();
        var root = Path.Combine(context.Temp.Path, "missing-root");

        var added = await context.FileResolutionService.AddSearchRootAsync(root);
        var operations = await context.BlockingOperations.ListAsync(
            status: BlockingOperationStatus.Failed,
            operationType: BlockingOperationTypes.FileSearchRootScan,
            scopeType: BlockingOperationScopeTypes.FileSearchRoot,
            scopeId: Path.GetFullPath(root));

        added.IsSuccess.Should().BeTrue();
        added.Value.IsAvailable.Should().BeFalse();
        operations.IsSuccess.Should().BeTrue();
        operations.Value.Should().ContainSingle();
        operations.Value.Single().FailureCode.Should().Be(AppErrorCodes.NotFound);
    }

    [Fact]
    public async Task ListKnownLocations_returns_registered_locations()
    {
        await using var context = await FileResolutionTestContext.CreateAsync();
        var file = await context.Temp.WriteFileAsync("known.pdf", "known");
        var asset = await context.FileAssetService.RegisterFileAsync(file);

        var locations = await context.FileResolutionService.ListKnownLocationsAsync(asset.Value.FileAssetId);

        locations.Value.Should().ContainSingle(location => location.Path == Path.GetFullPath(file));
    }

    [Fact]
    public async Task ResolveFile_ignores_unavailable_search_roots()
    {
        await using var context = await FileResolutionTestContext.CreateAsync();
        var original = await context.Temp.WriteFileAsync("ignored.pdf", "ignored");
        var asset = await context.FileAssetService.RegisterFileAsync(original);
        File.Delete(original);
        var offlineRoot = context.Temp.CreateDirectory("offline-with-match");
        await File.WriteAllTextAsync(Path.Combine(offlineRoot, "ignored.pdf"), "ignored");
        var onlineRoot = context.Temp.CreateDirectory("online-empty");
        var offline = await context.FileResolutionService.AddSearchRootAsync(offlineRoot);
        await context.FileResolutionService.SetSearchRootAvailabilityAsync(offline.Value.RootId, isAvailable: false);
        await context.FileResolutionService.AddSearchRootAsync(onlineRoot);

        var result = await context.FileResolutionService.ResolveFileAsync(asset.Value.FileAssetId, ResolveFilePurpose.OpenOriginal);

        result.Value.Status.Should().Be(FileAssetStatus.Missing);
        result.Value.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task Changed_file_does_not_auto_update_original_path()
    {
        await using var context = await FileResolutionTestContext.CreateAsync();
        var file = await context.Temp.WriteFileAsync("changed-path.pdf", "before");
        var asset = await context.FileAssetService.RegisterFileAsync(file);
        await File.WriteAllTextAsync(file, "after");

        var resolution = await context.FileResolutionService.ResolveFileAsync(asset.Value.FileAssetId, ResolveFilePurpose.OpenOriginal);
        var current = await context.FileAssetService.GetFileAssetAsync(asset.Value.FileAssetId);

        resolution.Value.Status.Should().Be(FileAssetStatus.Changed);
        current.Value.OriginalPath.Should().Be(Path.GetFullPath(file));
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

        public static async Task<FileResolutionTestContext> CreateAsync()
        {
            var database = TemporarySqliteDatabase.Create();
            var temp = TemporaryDirectory.Create();
            var clock = new FixedClock(new DateTimeOffset(2026, 6, 19, 3, 0, 0, TimeSpan.Zero));
            var runner = new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory);
            await runner.RunAsync();

            var libraryService = new LibraryIdentityService(database.ConnectionFactory, clock);
            await libraryService.CreateLibraryAsync("File resolution library");
            var fingerprintService = new FileFingerprintService();
            var blockingOperations = new BlockingOperationService(database.ConnectionFactory, clock);
            var itemService = new ItemService(database.ConnectionFactory, libraryService, clock);
            var fileAssetService = new FileAssetService(database.ConnectionFactory, libraryService, clock, fingerprintService);
            var documentInstanceService = new DocumentInstanceService(database.ConnectionFactory, clock);
            var fileResolutionService = new FileResolutionService(
                database.ConnectionFactory,
                libraryService,
                clock,
                fingerprintService,
                blockingOperations);

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
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"patchouli-resolution-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public string CreateDirectory(string relativePath)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public async Task<string> WriteFileAsync(string relativePath, string content)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
