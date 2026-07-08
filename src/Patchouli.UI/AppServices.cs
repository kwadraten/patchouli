using Patchouli.Core.Bibliography;
using Patchouli.Core.Credentials;
using Patchouli.Core.Csl;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Mcp;
using Patchouli.Core.Operations;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Evidence;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Credentials;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.Coordinates;
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
using Patchouli.Infrastructure.Snapshots;
using Patchouli.Infrastructure.Workflows;
using Patchouli.Mcp;
using Patchouli.Ocr;
using Patchouli.Ocr.MinerU;
using Patchouli.Search;

namespace Patchouli.UI;

public sealed class AppServices
{
    private IOcrQueueScheduler? _ocrQueue;
    private HttpClient? _cslCatalogHttpClient;
    private AppServices(string runtimeDatabasePath, PatchouliAppSettings settings)
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
        Files = new FileAssetService(ConnectionFactory, Library, Clock);
        Documents = new DocumentInstanceService(ConnectionFactory, Clock);
        FileResolution = new FileResolutionService(ConnectionFactory, Library, Clock);
        Pages = new PageService(ConnectionFactory, Clock);
        Layout = new LayoutTreeService(ConnectionFactory, Clock);
        OcrPresets = new OcrPresetService(ConnectionFactory, Library, Clock);
        ModelPathValidator = new OcrModelPathValidator();
        var adapterRegistry = new OcrAdapterRegistry();
        adapterRegistry.RegisterAdapter(new MockOcrAdapter());
        adapterRegistry.RegisterAdapter(new LocalPlaceholderOcrAdapter(ModelPathValidator));
        adapterRegistry.RegisterAdapter(new MinerUOcrAdapter());

        OcrAdapters = adapterRegistry;
        var pdfRenderer = new MuPdfNetPdfPageRenderer();
        PdfPreviewRenderer = pdfRenderer;
        PageRenders = new PageRenderService(ConnectionFactory, Library, FileResolution, pdfRenderer, Clock, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Patchouli", "cache", "page-renders"));
        PageCoordinates = new PageCoordinateService(ConnectionFactory);
        var searchUnitBuilder = new SearchUnitBuilder(ConnectionFactory, Clock);
        SearchUnits = searchUnitBuilder;
        SearchIndex = new SearchIndexRebuilder(ConnectionFactory, Clock);
        var searchProfiles = new SearchProfileService(ConnectionFactory, Library, Clock);
        SearchProfiles = searchProfiles;
        QueryRewriter = searchProfiles;
        Search = new SqliteSearchService(ConnectionFactory, searchProfiles);
        Evidence = new EvidenceReferenceService(ConnectionFactory, Clock, PageCoordinates);
        MinerUImporter = new MinerUResultImporter(ConnectionFactory, Clock);
        Ocr = new OcrRunCoordinator(ConnectionFactory, Clock, new MockOcrEngine(), searchUnitBuilder, adapterRegistry, PageRenders, PageCoordinates, MinerUImporter);
        McpSettings = new McpServerSettingsService(ConnectionFactory, Clock, BlockingOperations);
        Mcp = new McpReadApi(ConnectionFactory, Search, Evidence, PageCoordinates, CslStore, CslRenderer);
        SnapshotPublisher = new SnapshotPublisher(Clock);
        SnapshotImporter = new SnapshotImporter(BlockingOperations);
        BranchInspection = new SnapshotBranchInspectionService(SnapshotImporter, ConnectionFactory, Library);
        Credentials = new CredentialStore(ConnectionFactory, Library, Clock);
        PdfMetadata = new PdfMetadataReader();
        PdfDiscovery = new PdfDiscoveryService();
        PdfImport = new PdfImportWorkflow(Files, Items, Documents, Pages, PdfMetadata, Clock, ItemTypeInference);
        McpVerification = new McpVerificationService(ConnectionFactory, Mcp);
        FirstRunWorkflow = new FirstRunWorkflow(Library, PdfDiscovery, PdfImport);
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
    public IFileAssetService Files { get; }
    public IDocumentInstanceService Documents { get; }
    public IFileResolutionService FileResolution { get; }
    public IPageService Pages { get; }
    public ILayoutTreeService Layout { get; }
    public IOcrPresetService OcrPresets { get; }
    public IOcrModelPathValidator ModelPathValidator { get; }
    public IOcrAdapterRegistry OcrAdapters { get; }
    public IPageRenderService PageRenders { get; }
    public IPdfPageMemoryRenderer PdfPreviewRenderer { get; }
    public IPageCoordinateService PageCoordinates { get; }
    public IOcrRunCoordinator Ocr { get; }
    public ISearchUnitBuilder SearchUnits { get; }
    public ISearchIndexRebuilder SearchIndex { get; }
    public ISearchService Search { get; }
    public ISearchProfileService SearchProfiles { get; }
    public IQueryRewriter QueryRewriter { get; }
    public IEvidenceReferenceService Evidence { get; }
    public IMcpReadApi Mcp { get; }
    public IMcpServerSettingsService McpSettings { get; }
    public ISnapshotPublisher SnapshotPublisher { get; }
    public ISnapshotImporter SnapshotImporter { get; }
    public ISnapshotBranchInspectionService BranchInspection { get; }
    public ICredentialStore Credentials { get; }
    public IPdfMetadataReader PdfMetadata { get; }
    public PdfDiscoveryService PdfDiscovery { get; }
    public IMinerUResultImporter MinerUImporter { get; }
    public PdfImportWorkflow PdfImport { get; }
    public McpVerificationService McpVerification { get; }
    public FirstRunWorkflow FirstRunWorkflow { get; }
    public IOcrRunCoordinator CreateOcrRunCoordinator(Func<MinerUConfiguration, IMinerUClient> minerUClientFactory) =>
        new OcrRunCoordinator(
            ConnectionFactory,
            Clock,
            new MockOcrEngine(),
            new SearchUnitBuilder(ConnectionFactory, Clock),
            OcrAdapters,
            PageRenders,
            PageCoordinates,
            MinerUImporter,
            minerUClientFactory);
    public async Task<Result<IOcrQueueScheduler>> GetOcrQueueAsync(CancellationToken cancellationToken = default)
    {
        if (_ocrQueue is not null)
        {
            return Result<IOcrQueueScheduler>.Success(_ocrQueue);
        }

        var library = await Library.GetCurrentLibraryAsync(cancellationToken);
        if (library.IsFailure)
        {
            return Result<IOcrQueueScheduler>.Failure(library.ErrorCode!, library.ErrorMessage!);
        }

        var executor = new OcrQueueTaskExecutor(Ocr);
        _ocrQueue = new OcrQueueScheduler(library.Value.LibraryId, Clock, executor);
        return Result<IOcrQueueScheduler>.Success(_ocrQueue);
    }
    public async Task<Result<IOcrQueueRowService>> GetOcrQueueRowsAsync(CancellationToken cancellationToken = default)
    {
        var queue = await GetOcrQueueAsync(cancellationToken);
        return queue.IsFailure
            ? Result<IOcrQueueRowService>.Failure(queue.ErrorCode!, queue.ErrorMessage!)
            : Result<IOcrQueueRowService>.Success(new OcrQueueRowService(queue.Value, ConnectionFactory));
    }
    public static async Task<AppServices> CreateAsync(string path, PatchouliAppSettings? settings = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        settings ??= PatchouliAppSettings.Load();
        var logger = new SimpleFileLogger(settings.Runtime.LogDirectory);
        try { await logger.LogAsync("startup", $"Opening runtime database {path}"); } catch { }
        var services = new AppServices(path, settings);
        await services.MigrationRunner.RunAsync();
        try { await logger.LogAsync("migration", "Pending migrations completed."); } catch { }
        return services;
    }
}
