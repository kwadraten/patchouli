using Dapper;
using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Import;
using Patchouli.Core.Operations;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Operations;
using Patchouli.Infrastructure.Workflows;
using Patchouli.Ocr;
using Patchouli.UI;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

public sealed class FirstRunViewModelTests
{
    [Fact]
    public async Task OpenDatabaseCommand_moves_from_database_to_library_step()
    {
        var openedPath = "";
        var viewModel = new FirstRunViewModel(path =>
        {
            openedPath = path;
            return Task.FromResult<(FirstRunWorkflow Workflow, PdfDiscoveryService Discovery)>((null!, new PdfDiscoveryService()));
        })
        {
            DatabasePath = @"C:\temp\runtime.sqlite"
        };

        await viewModel.OpenDatabaseCommand.ExecuteAsync();

        openedPath.Should().Be(@"C:\temp\runtime.sqlite");
        viewModel.CurrentStep.Should().Be("library");
        viewModel.ShowLibraryStep.Should().BeTrue();
        viewModel.HasError.Should().BeFalse();
    }

    [Fact]
    public async Task CreateLibrary_without_open_database_stays_recoverable()
    {
        var viewModel = new FirstRunViewModel(_ =>
            Task.FromResult<(FirstRunWorkflow Workflow, PdfDiscoveryService Discovery)>((null!, new PdfDiscoveryService())));

        await viewModel.CreateLibraryCommand.ExecuteAsync();

        viewModel.CurrentStep.Should().Be("database");
        viewModel.HasError.Should().BeTrue();
        viewModel.LastError.Should().Contain("数据库");
    }

    [Fact]
    public async Task OpenDatabaseCommand_import_mode_checks_library_metadata_and_shows_missing_setup_steps()
    {
        await using var database = TemporarySqliteDatabase.Create();
        var clock = new FixedClock(DateTimeOffset.Parse("2026-06-20T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        await new LibraryIdentityService(database.ConnectionFactory, clock).CreateLibraryAsync("Existing Library");
        var openedPath = "";
        var viewModel = new FirstRunViewModel(path =>
        {
            openedPath = path;
            return Task.FromResult<(FirstRunWorkflow Workflow, PdfDiscoveryService Discovery)>((null!, new PdfDiscoveryService()));
        })
        {
            DatabasePath = database.Path,
            IsImportMode = true
        };

        await viewModel.OpenDatabaseCommand.ExecuteAsync();

        openedPath.Should().Be(database.Path);
        viewModel.HasError.Should().BeFalse();
        viewModel.CurrentStep.Should().Be(FirstRunStep.Scan);
        viewModel.ProgressText.Should().Contain("跳过资料库身份步骤");
        viewModel.ProgressText.Should().Contain("缺少 file_search_roots");
        viewModel.ProgressText.Should().Contain("缺少 ocr_presets");
    }

    [Fact]
    public async Task OpenDatabaseCommand_import_mode_skips_existing_roots_and_presets_with_status_feedback()
    {
        await using var database = TemporarySqliteDatabase.Create();
        var clock = new FixedClock(DateTimeOffset.Parse("2026-06-20T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        var library = new LibraryIdentityService(database.ConnectionFactory, clock);
        var created = await library.CreateLibraryAsync("Configured Library");
        await using (var connection = database.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "insert into file_search_roots (root_id, library_id, root_path, is_available, created_at, updated_at) values (@Id, @LibraryId, @Path, 1, @Now, @Now);",
                new { Id = Guid.NewGuid().ToString("D"), LibraryId = created.Value.LibraryId.ToString(), Path = Path.GetTempPath(), Now = clock.UtcNow.ToString("O") });
        }
        await new OcrPresetService(database.ConnectionFactory, library, clock).CreatePresetAsync("MinerU", null, OcrEngineIds.MinerU, OcrModelIds.MinerUDefault, null, "{}", true);
        var viewModel = new FirstRunViewModel(path =>
            Task.FromResult<(FirstRunWorkflow Workflow, PdfDiscoveryService Discovery)>((null!, new PdfDiscoveryService())))
        {
            DatabasePath = database.Path,
            IsImportMode = true
        };

        await viewModel.OpenDatabaseCommand.ExecuteAsync();

        viewModel.HasError.Should().BeFalse();
        viewModel.CurrentStep.Should().Be(FirstRunStep.Complete);
        viewModel.IsComplete.Should().BeTrue();
        viewModel.ProgressText.Should().Contain("跳过文件搜索根配置步骤");
        viewModel.ProgressText.Should().Contain("跳过 OCR Preset 配置步骤");
    }

    [Fact]
    public async Task ScanDirectoryCommand_imports_all_pages_from_real_pdf_fixture()
    {
        await using var context = await ScanImportContext.CreateAsync();
        TestFixtures.CopyRealThreePagePdfTo(context.ScanRoot, "full-document.pdf");
        var viewModel = new FirstRunViewModel(context.Workflow, new PdfDiscoveryService())
        {
            ScanRoot = context.ScanRoot
        };

        await viewModel.ScanCommand.ExecuteAsync();

        viewModel.HasError.Should().BeFalse();
        viewModel.ImportedPdfCount.Should().Be(1);
        await using var connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        var pageCount = await connection.ExecuteScalarAsync<int>("select count(1) from pages;");
        pageCount.Should().Be(3);
    }

    [Fact]
    public async Task ScanDirectoryCommand_records_completed_initial_root_scan()
    {
        await using var context = await ScanImportContext.CreateAsync();
        TestFixtures.CopyRealThreePagePdfTo(context.ScanRoot, "full-document.pdf");
        var viewModel = new FirstRunViewModel(context.Workflow, new PdfDiscoveryService())
        {
            ScanRoot = context.ScanRoot
        };

        await viewModel.ScanCommand.ExecuteAsync();

        var operations = await context.BlockingOperations.ListAsync(
            status: BlockingOperationStatus.Completed,
            operationType: BlockingOperationTypes.InitialRootScan,
            scopeType: BlockingOperationScopeTypes.FileSearchRoot,
            scopeId: Path.GetFullPath(context.ScanRoot));

        operations.IsSuccess.Should().BeTrue();
        operations.Value.Should().ContainSingle();
        operations.Value.Single().ProgressLabel.Should().Contain("candidate");
    }

    [Fact]
    public async Task ScanDirectoryCommand_records_failed_initial_root_scan_when_no_pdfs_are_found()
    {
        await using var context = await ScanImportContext.CreateAsync();
        var viewModel = new FirstRunViewModel(context.Workflow, new PdfDiscoveryService())
        {
            ScanRoot = context.ScanRoot
        };

        await viewModel.ScanCommand.ExecuteAsync();

        var operations = await context.BlockingOperations.ListAsync(
            status: BlockingOperationStatus.Failed,
            operationType: BlockingOperationTypes.InitialRootScan,
            scopeType: BlockingOperationScopeTypes.FileSearchRoot,
            scopeId: Path.GetFullPath(context.ScanRoot));

        viewModel.CurrentStep.Should().Be(FirstRunStep.Scan);
        viewModel.HasError.Should().BeTrue();
        operations.IsSuccess.Should().BeTrue();
        operations.Value.Should().ContainSingle();
        operations.Value.Single().FailureCode.Should().Be(AppErrorCodes.NotFound);
    }

    [Fact]
    public async Task FinishSetupCommand_requires_token()
    {
        var viewModel = new FirstRunViewModel(_ =>
            Task.FromResult<(FirstRunWorkflow Workflow, PdfDiscoveryService Discovery)>((null!, new PdfDiscoveryService())));
        SetState(viewModel, new FirstRunWorkflowState(
            FirstRunStep.MinerUConfig,
            "Configure MinerU OCR.",
            "input.pdf",
            null,
            null,
            null,
            Guid.NewGuid().ToString(),
            null,
            false));

        await viewModel.FinishSetupCommand.ExecuteAsync();

        viewModel.CurrentStep.Should().Be(FirstRunStep.MinerUConfig);
        viewModel.HasError.Should().BeTrue();
        viewModel.LastError.Should().Contain("MinerU API token");
    }

    [Fact]
    public async Task FinishSetupCommand_completes_after_token()
    {
        var viewModel = new FirstRunViewModel(_ =>
            Task.FromResult<(FirstRunWorkflow Workflow, PdfDiscoveryService Discovery)>((null!, new PdfDiscoveryService())))
        {
            MinerUToken = "token"
        };
        SetState(viewModel, new FirstRunWorkflowState(
            FirstRunStep.MinerUConfig,
            "Configure MinerU OCR.",
            "input.pdf",
            null,
            null,
            null,
            Guid.NewGuid().ToString(),
            null,
            false));

        await viewModel.FinishSetupCommand.ExecuteAsync();

        viewModel.CurrentStep.Should().Be(FirstRunStep.Complete);
        viewModel.IsComplete.Should().BeTrue();
    }

    private static void SetState(FirstRunViewModel viewModel, FirstRunWorkflowState state)
    {
        var field = typeof(FirstRunViewModel).GetField("_state", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        field!.SetValue(viewModel, state);
    }

    private sealed class ScanImportContext : IAsyncDisposable
    {
        private ScanImportContext(
            TemporarySqliteDatabase database,
            string scanRoot,
            FirstRunWorkflow workflow,
            IBlockingOperationService blockingOperations)
        {
            Database = database;
            ScanRoot = scanRoot;
            Workflow = workflow;
            BlockingOperations = blockingOperations;
        }

        public TemporarySqliteDatabase Database { get; }
        public string ScanRoot { get; }
        public FirstRunWorkflow Workflow { get; }
        public IBlockingOperationService BlockingOperations { get; }

        public static async Task<ScanImportContext> CreateAsync()
        {
            var database = TemporarySqliteDatabase.Create();
            var clock = new FixedClock(DateTimeOffset.Parse("2026-06-20T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            var library = new LibraryIdentityService(database.ConnectionFactory, clock);
            await library.CreateLibraryAsync("Scan Import");
            var pdfImport = new PdfImportWorkflow(
                new FileAssetService(database.ConnectionFactory, library, clock),
                new ItemService(database.ConnectionFactory, library, clock),
                new DocumentInstanceService(database.ConnectionFactory, clock),
                new PageService(database.ConnectionFactory, clock),
                new PdfMetadataReader(),
                clock);
            var blockingOperations = new BlockingOperationService(database.ConnectionFactory, clock);
            var workflow = new FirstRunWorkflow(library, new PdfDiscoveryService(), pdfImport, blockingOperations);
            var scanRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"patchouli-scan-{Guid.NewGuid():N}")).FullName;
            return new ScanImportContext(database, scanRoot, workflow, blockingOperations);
        }

        public async ValueTask DisposeAsync()
        {
            if (Directory.Exists(ScanRoot))
                Directory.Delete(ScanRoot, recursive: true);
            await Database.DisposeAsync();
        }
    }
}
