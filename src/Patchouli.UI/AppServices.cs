using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Core.Cli;
using Patchouli.Core.Credentials;
using Patchouli.Core.Csl;
using Patchouli.Core.Conflicts;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Mcp;
using Patchouli.Core.Operations;
using Patchouli.Core.Results;
using Patchouli.Core.Settings;
using Patchouli.Core.Time;
using Patchouli.Evidence;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Bibliography.Biblatex;
using Patchouli.Infrastructure.Bibliography.MetadataLookup;
using Patchouli.Infrastructure.Cli;
using Patchouli.Infrastructure.Credentials;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.Coordinates;
using Patchouli.Infrastructure.Conflicts;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Evidence;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Ocr.MinerU;
using Patchouli.Infrastructure.Operations;
using Patchouli.Infrastructure.Rendering;
using Patchouli.Infrastructure.Search;
using Patchouli.Infrastructure.Settings;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.Infrastructure.Workflows;
using Patchouli.Mcp;
using Patchouli.Ocr;
using Patchouli.Ocr.MinerU;
using Patchouli.Search;
using Patchouli.UI.Diagnostics;

namespace Patchouli.UI;

public sealed class AppServices
{
    private readonly OcrRunEngine _ocrEngine;
    private HttpClient? _cslCatalogHttpClient;
    private HttpClient? _metadataLookupHttpClient;

    private IReadOnlyList<Patchouli.Core.Bibliography.MetadataLookup.MetadataSourcePreference>
        _metadataLookupPreferences = [];

    private AppServices(string runtimeDatabasePath, PatchouliAppSettings settings, string settingsPath)
    {
        RuntimeDatabasePath = runtimeDatabasePath;
        Settings = settings;
        ConnectionFactory = new SqliteConnectionFactory(runtimeDatabasePath);
        Clock = new SystemClock();
        BlockingOperations = new BlockingOperationService(ConnectionFactory, Clock);
        MigrationRunner = new MigrationRunner(ConnectionFactory, Path.Combine(AppContext.BaseDirectory, "migrations"));
        Library = new LibraryIdentityService(ConnectionFactory, Clock);
        LibraryPreferences = new LibraryPreferencesService(ConnectionFactory, Library, Clock);
        LibraryItems = new LibraryItemQueryService(ConnectionFactory);
        Items = new ItemService(ConnectionFactory, Library, Clock);
        _cslCatalogHttpClient = new HttpClient();
        CslCatalog = new CslStyleCatalog(ConnectionFactory, _cslCatalogHttpClient);
        CslStore = new CslStyleStore(ConnectionFactory, Clock, blockingOperations: BlockingOperations);
        CslItemMapper = new CslItemMapper();
        CslRenderer = new CslRenderer(Items, CslStore, CslItemMapper);
        ItemTypeProfiles = new CslItemTypeProfileService();
        ItemTypeInference = new ItemTypeInferenceService(ConnectionFactory, Clock, ItemTypeProfiles, Items);
        _metadataLookupPreferences = ToMetadataLookupPreferences(settings.MetadataLookup);
        _metadataLookupHttpClient = new HttpClient();
        MetadataSources = MetadataSourceRegistry.CreateDefault(_metadataLookupHttpClient);
        MetadataLookup =
            new MetadataLookupService(Items, MetadataSources, ItemTypeInference, () => _metadataLookupPreferences);
        LibrarySettings = new LibrarySettingStore(ConnectionFactory);
        LibrarySettingRecords = new LibrarySettingRecordService(LibrarySettings, Clock);
        LibrarySettingCoordinator = new LibrarySettingCoordinator(LibrarySettings, LibrarySettingRecords);
        Files = new FileAssetService(ConnectionFactory, Library, Clock);
        Documents = new DocumentInstanceService(ConnectionFactory, Clock);
        INativeFileAccessAdapter? nativeAdapter = OperatingSystem.IsMacOS()
            ? new MacOSNativeFileAccessAdapter()
            : null;
        FileSearchRootAccess = new FileSearchRootAccess(nativeAdapter,
            settings.FileScanning.ExclusionPatterns);
        AppSettingsDeviceRootBindingStore rootBindings = new(settingsPath);
        FileResolution = new FileResolutionService(ConnectionFactory, Library, Clock,
            blockingOperations: BlockingOperations, rootAccess: FileSearchRootAccess, rootBindings: rootBindings);
        BiblatexHelper = new BiblatexHelperClient();
        BiblatexImport = new BiblatexImportService(BiblatexHelper, Items, Files, Documents);
        ConflictActions = new ConflictActionExecutorRegistry(
        [
            new FileConflictActionExecutor(FileResolution, ConflictCode.FileRelocationMultipleCandidates),
            new FileConflictActionExecutor(FileResolution, ConflictCode.SourceFileChangedOrBBoxBasisStale),
            new BiblatexConflictActionExecutor(ConflictCode.BiblatexItemFieldConflict),
            new BiblatexConflictActionExecutor(ConflictCode.BiblatexBatchLinkCandidates)
        ]);
        Pages = new PageService(ConnectionFactory, Clock);
        Markdown = new MarkdigMarkdownEngine();
        DocumentTrees = new DocumentTreeService(ConnectionFactory, Clock, Markdown);
        DocumentTreeEditor = (IDocumentTreeEditor)DocumentTrees;
        DocumentMarkdown = new DocumentMarkdownCompiler(DocumentTrees, Markdown);
        OcrPresets = new OcrPresetService(ConnectionFactory, Library, Clock);
        ModelPathValidator = new OcrModelPathValidator();
        OcrAdapterRegistry adapterRegistry = new();
        if (settings.Runtime.UseMockOcrOnly)
        {
            adapterRegistry.RegisterAdapter(new MockOcrAdapter());
            adapterRegistry.RegisterAdapter(new LocalPlaceholderOcrAdapter(ModelPathValidator));
        }

        adapterRegistry.RegisterAdapter(new MinerUOcrAdapter());
        adapterRegistry.RegisterAdapter(new MultimodalLlmOcrAdapter());

        OcrAdapters = adapterRegistry;
        PdfiumPdfPageRenderer pdfRenderer = new();
        PdfPreviewRenderer = pdfRenderer;
        PageRenders = new PageRenderService(ConnectionFactory, Library, FileResolution, pdfRenderer, Clock,
            Path.Combine(new PlatformAppPaths().Resolve().CacheDirectory, "page-renders"), FileSearchRootAccess);
        PageCoordinates = new PageCoordinateService(ConnectionFactory);
        SearchUnitBuilder searchUnitBuilder = new(ConnectionFactory, Clock, Markdown);
        SearchUnits = searchUnitBuilder;
        SearchIndex = new SearchIndexRebuilder(ConnectionFactory, Clock);
        OcrDocumentTreeImporter ocrTreeImporter = new(DocumentTrees);
        SearchProfileService searchProfiles = new(ConnectionFactory, Library, Clock);
        SearchProfiles = searchProfiles;
        QueryRewriter = searchProfiles;
        Search = new SqliteSearchService(ConnectionFactory, searchProfiles);
        Evidence = new EvidenceReferenceService(ConnectionFactory, Clock, PageCoordinates);
        MinerUImporter = new MinerUResultImporter(ConnectionFactory, Clock, ocrTreeImporter);
        IOcrEngine pageOcrEngine = settings.Runtime.UseMockOcrOnly ? new MockOcrEngine() : new UnavailableOcrEngine();
        Credentials = new CredentialStore(settingsPath);
        _ocrEngine = new OcrRunEngine(ConnectionFactory, Clock, Credentials.GetActiveSecretForProviderAsync,
            pageOcrEngine, searchUnitBuilder, ocrTreeImporter,
            adapterRegistry, PageRenders, PageCoordinates, MinerUImporter,
            configuration => (MinerUClientFactoryOverride ?? CreateMinerUClient)(configuration),
            fileResolution: FileResolution, fileMaterialization: FileSearchRootAccess);
        OcrQueueTaskExecutor ocrQueueExecutor = new(_ocrEngine, SearchUnits, SearchIndex);
        OcrQueueScheduler ocrQueueScheduler = new(
            async cancellationToken =>
            {
                Result<LibraryMetadata> library = await Library.GetCurrentLibraryAsync(cancellationToken);
                return library.IsFailure
                    ? Result<LibraryId>.Failure(library.ErrorCode!, library.ErrorMessage!)
                    : Result<LibraryId>.Success(library.Value.LibraryId);
            },
            Clock,
            ocrQueueExecutor,
            loopErrorLogger: exception =>
                UnexpectedExceptions.Sink.Report(exception, "ocr-scheduler", "scheduler-loop"));
        Ocr = new QueuedOcrRunCoordinator(ocrQueueScheduler, _ocrEngine);
        LogicalPageOcr = new LogicalPageOcrService(Ocr, DocumentTrees);
        McpSettings = new McpServerSettingsService(settingsPath, Clock, BlockingOperations);
        Mcp = new McpReadApi(
            ConnectionFactory, Search, Evidence, PageCoordinates, CslStore, CslRenderer, Markdown, DocumentMarkdown);
        McpWrites = new McpWriteApi(Items, BiblatexHelper, CslStore);
        CliPath = new CliPathService();
        SnapshotPublisher = new SnapshotPublisher(Clock);
        SnapshotImporter = new SnapshotImporter(BlockingOperations);
        BranchInspection = new SnapshotBranchInspectionService(SnapshotImporter, ConnectionFactory, Library);
        SnapshotSync = new SnapshotSyncCoordinator(
            SnapshotPublisher,
            SnapshotImporter,
            BranchInspection,
            new SnapshotSyncSettingsStore(runtimeDatabasePath, settingsPath, settings.Runtime.DefaultStagingRoot),
            Clock);
        PdfMetadata = new PdfMetadataReader();
        PdfDiscovery = new PdfDiscoveryService(FileSearchRootAccess);
        PdfImport = new PdfImportWorkflow(Files, Items, Documents, Pages, PdfMetadata, Clock, ItemTypeInference);
        McpVerification = new McpVerificationService(ConnectionFactory, Mcp);
        FirstRunWorkflow = new FirstRunWorkflow(Library, PdfDiscovery, PdfImport, BlockingOperations);
    }

    public string RuntimeDatabasePath { get; }
    public PatchouliAppSettings Settings { get; }
    public SqliteConnectionFactory ConnectionFactory { get; }
    public IClock Clock { get; }
    public IBlockingOperationService BlockingOperations { get; }
    public MigrationRunner MigrationRunner { get; }
    public ILibraryIdentityService Library { get; }
    public ILibraryPreferencesService LibraryPreferences { get; }
    public ILibraryItemQueryService LibraryItems { get; }
    public IItemService Items { get; }
    public ICslStyleCatalog CslCatalog { get; }
    public ICslStyleStore CslStore { get; }
    public ICslItemMapper CslItemMapper { get; }
    public ICslRenderer CslRenderer { get; }
    public ICslItemTypeProfileService ItemTypeProfiles { get; }
    public IItemTypeInferenceService ItemTypeInference { get; }
    public IMetadataSourceRegistry MetadataSources { get; }
    public IMetadataLookupService MetadataLookup { get; }
    public ILibrarySettingStore LibrarySettings { get; }
    public LibrarySettingRecordService LibrarySettingRecords { get; }
    public LibrarySettingCoordinator LibrarySettingCoordinator { get; }
    public IFileAssetService Files { get; }
    public IDocumentInstanceService Documents { get; }
    public IFileResolutionService FileResolution { get; }
    public IBiblatexHelperClient BiblatexHelper { get; }
    public IBiblatexImportService BiblatexImport { get; }
    public ConflictActionExecutorRegistry ConflictActions { get; }
    public FileSearchRootAccess FileSearchRootAccess { get; }
    public IPageService Pages { get; }
    public IMarkdownEngine Markdown { get; }
    public IDocumentTreeService DocumentTrees { get; }
    public IDocumentTreeEditor DocumentTreeEditor { get; }
    public IDocumentMarkdownCompiler DocumentMarkdown { get; }
    public IOcrPresetService OcrPresets { get; }
    public IOcrModelPathValidator ModelPathValidator { get; }
    public IOcrAdapterRegistry OcrAdapters { get; }
    public IPageRenderService PageRenders { get; }
    public IPdfPagePixelBufferRenderer PdfPreviewRenderer { get; }
    public IPageCoordinateService PageCoordinates { get; }
    public IOcrRunCoordinator Ocr { get; }
    public Func<MinerUConfiguration, IMinerUClient>? MinerUClientFactoryOverride { get; set; }
    public ILogicalPageOcrService LogicalPageOcr { get; }
    public ISearchUnitBuilder SearchUnits { get; }
    public ISearchIndexRebuilder SearchIndex { get; }
    public ISearchService Search { get; }
    public ISearchProfileService SearchProfiles { get; }
    public IQueryRewriter QueryRewriter { get; }
    public IEvidenceReferenceService Evidence { get; }
    public IMcpReadApi Mcp { get; }
    public IMcpWriteApi McpWrites { get; }
    public IMcpServerSettingsService McpSettings { get; }
    public ICliPathService CliPath { get; }
    public ISnapshotPublisher SnapshotPublisher { get; }
    public ISnapshotImporter SnapshotImporter { get; }
    public ISnapshotBranchInspectionService BranchInspection { get; }
    public ISnapshotSyncCoordinator SnapshotSync { get; }
    public ICredentialStore Credentials { get; }
    public IPdfMetadataReader PdfMetadata { get; }
    public PdfDiscoveryService PdfDiscovery { get; }
    public IMinerUResultImporter MinerUImporter { get; }
    public PdfImportWorkflow PdfImport { get; }
    public McpVerificationService McpVerification { get; }
    public FirstRunWorkflow FirstRunWorkflow { get; }

    public void UpdateMetadataLookupPreferences(MetadataLookupAppSettings settings)
    {
        _metadataLookupPreferences = ToMetadataLookupPreferences(settings);
    }

    public async Task<Result<MetadataLookupAppSettings?>> GetSyncedMetadataLookupAsync(
        CancellationToken cancellationToken = default)
    {
        Result<MetadataLookupAppSettings?> record =
            await LibrarySettingCoordinator.ReadAsync<MetadataLookupAppSettings>(
                LibrarySettingKeys.MetadataLookup,
                true,
                cancellationToken);
        if (record.IsFailure)
        {
            return Result<MetadataLookupAppSettings?>.Failure(record.ErrorCode!, record.ErrorMessage!);
        }

        if (record.Value is null)
        {
            return Result<MetadataLookupAppSettings?>.Success(null);
        }

        return Result<MetadataLookupAppSettings?>.Success(
            MetadataLookupAppSettings.MergeWithDefaults(record.Value.Sources));
    }

    public async Task<Result> SaveSyncedMetadataLookupAsync(MetadataLookupAppSettings settings, string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "A device identity is required for synced settings.");
        }

        MetadataLookupAppSettings normalized = MetadataLookupAppSettings.MergeWithDefaults(settings.Sources);
        SettingsSaveResult saved = await LibrarySettingCoordinator.SaveEnabledAsync(
            LibrarySettingKeys.MetadataLookup,
            normalized,
            deviceId,
            _ => Task.FromResult(SettingsSaveResult.Success),
            UpdateMetadataLookupPreferences,
            cancellationToken);

        return saved.IsSuccess
            ? Result.Success()
            : Result.Failure(saved.ErrorCode ?? AppErrorCodes.DatabaseError, saved.ErrorMessage ??
                                                                             "Unable to save the synchronized setting.");
    }

    private async Task ApplySyncedMetadataLookupAsync(PatchouliAppSettings settings)
    {
        Result<LibraryMetadata> library = await Library.GetCurrentLibraryAsync();
        if (library.IsFailure ||
            !settings.Sync.IsSettingEnabled(LibrarySettingKeys.MetadataLookup, library.Value.LibraryId))
        {
            return;
        }

        Result<MetadataLookupAppSettings?> synced =
            await LibrarySettingCoordinator.ReadAsync<MetadataLookupAppSettings>(
                LibrarySettingKeys.MetadataLookup,
                true);
        if (synced.IsSuccess && synced.Value is not null)
        {
            UpdateMetadataLookupPreferences(MetadataLookupAppSettings.MergeWithDefaults(synced.Value.Sources));
        }
    }

    public void UpdateFileScanExclusions(FileScanningAppSettings settings)
    {
        FileSearchRootAccess.UpdateExclusionPatterns(settings.ExclusionPatterns);
    }

    private static IReadOnlyList<Patchouli.Core.Bibliography.MetadataLookup.MetadataSourcePreference>
        ToMetadataLookupPreferences(MetadataLookupAppSettings settings)
    {
        return settings.Sources.Select((source, index) =>
            new Patchouli.Core.Bibliography.MetadataLookup.MetadataSourcePreference(source.SourceId, source.Enabled,
                index)).ToArray();
    }

    public Task<Result<IOcrQueueScheduler>> GetOcrQueueAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<IOcrQueueScheduler>.Success(((QueuedOcrRunCoordinator)Ocr).Queue));
    }

    public async Task<Result<IOcrQueueRowService>> GetOcrQueueRowsAsync(CancellationToken cancellationToken = default)
    {
        Result<IOcrQueueScheduler> queue = await GetOcrQueueAsync(cancellationToken);
        return queue.IsFailure
            ? Result<IOcrQueueRowService>.Failure(queue.ErrorCode!, queue.ErrorMessage!)
            : Result<IOcrQueueRowService>.Success(new OcrQueueRowService(queue.Value, ConnectionFactory));
    }

    private IMinerUClient CreateMinerUClient(MinerUConfiguration configuration)
    {
        return new MinerUClient(new MinerUOptions
        {
            Token = configuration.Token,
            BaseUrl = configuration.BaseUrl ?? Settings.MinerU.BaseUrl,
            ModelVersion = configuration.ModelVersion ?? Settings.MinerU.ModelVersion,
            IsOcr = configuration.IsOcr,
            EnableTable = configuration.EnableTable,
            EnableFormula = configuration.EnableFormula,
            PollingTimeoutSeconds = configuration.PollingTimeoutSeconds
        });
    }

    public static async Task<AppServices> CreateAsync(string path, PatchouliAppSettings? settings = null,
        string? settingsPath = null)
    {
        settingsPath ??= PatchouliAppSettings.ResolvePath();
        settings ??= PatchouliAppSettings.Load(settingsPath);
        AppPathGuard.ValidateDatabasePath(path, settings.Runtime.DefaultSyncRoot);
        AppPathGuard.ValidateMutablePath(settings.Runtime.LogDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        SimpleFileLogger logger = new(settings.Runtime.LogDirectory);
        try
        {
            await logger.LogAsync("startup", $"Opening runtime database {path}");
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, "operation-log", "startup");
        }

        AppServices services = new(path, settings, settingsPath);
        await services.MigrationRunner.RunAsync();
        if (services.FileResolution is FileResolutionService fileResolution)
        {
            Result adopted = await fileResolution.AdoptLegacyDeviceRootBindingsAsync();
            if (adopted.IsFailure)
            {
                try
                {
                    await logger.LogAsync("migration",
                        adopted.ErrorMessage ?? "Legacy root binding migration failed.");
                }
                catch (Exception exception)
                {
                    UnexpectedExceptions.Sink.Report(exception, "operation-log", "legacy-root-binding-migration");
                }
            }
        }

        Result ocrReconcile = await services._ocrEngine.ReconcileInterruptedRunsAsync();
        if (ocrReconcile.IsFailure)
        {
            try
            {
                await logger.LogAsync("ocr-reconcile",
                    ocrReconcile.ErrorMessage ?? "OCR startup reconciliation failed.");
            }
            catch (Exception exception)
            {
                UnexpectedExceptions.Sink.Report(exception, "operation-log", "ocr-reconcile");
            }
        }

        await ((QueuedOcrRunCoordinator)services.Ocr).Queue.StartAsync();

        await services.ApplySyncedMetadataLookupAsync(settings);
        try
        {
            await logger.LogAsync("migration", "Pending migrations completed.");
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, "operation-log", "migration");
        }

        return services;
    }
}
