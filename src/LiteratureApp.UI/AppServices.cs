using LiteratureApp.Core.Bibliography;
using LiteratureApp.Core.Credentials;
using LiteratureApp.Core.Documents;
using LiteratureApp.Core.Files;
using LiteratureApp.Core.Import;
using LiteratureApp.Core.Layout;
using LiteratureApp.Core.Library;
using LiteratureApp.Core.Results;
using LiteratureApp.Core.Time;
using LiteratureApp.Evidence;
using LiteratureApp.Infrastructure.Bibliography;
using LiteratureApp.Infrastructure.Credentials;
using LiteratureApp.Infrastructure.Coordinates;
using LiteratureApp.Infrastructure.Database;
using LiteratureApp.Infrastructure.Documents;
using LiteratureApp.Infrastructure.Evidence;
using LiteratureApp.Infrastructure.Files;
using LiteratureApp.Infrastructure.Layout;
using LiteratureApp.Infrastructure.LibraryIdentity;
using LiteratureApp.Infrastructure.Mcp;
using LiteratureApp.Infrastructure.Migrations;
using LiteratureApp.Infrastructure.Ocr;
using LiteratureApp.Infrastructure.Ocr.MinerU;
using LiteratureApp.Infrastructure.Rendering;
using LiteratureApp.Infrastructure.Search;
using LiteratureApp.Infrastructure.Snapshots;
using LiteratureApp.Infrastructure.Workflows;
using LiteratureApp.Mcp;
using LiteratureApp.Ocr;
using LiteratureApp.Ocr.MinerU;
using LiteratureApp.Search;

namespace LiteratureApp.UI;

public sealed class AppServices
{
    private IOcrQueueScheduler? _ocrQueue;
    private AppServices(string runtimeDatabasePath)
    {
        RuntimeDatabasePath = runtimeDatabasePath;
        ConnectionFactory = new SqliteConnectionFactory(runtimeDatabasePath);
        Clock = new SystemClock();
        MigrationRunner = new MigrationRunner(ConnectionFactory, Path.Combine(AppContext.BaseDirectory, "migrations"));
        Library = new LibraryIdentityService(ConnectionFactory, Clock);
        Items = new ItemService(ConnectionFactory, Library, Clock);
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
        adapterRegistry.RegisterAdapter(new TesseractCliAdapter(new SystemProcessRunner()));
        OcrAdapters = adapterRegistry;
        PageRenders = new PageRenderService(ConnectionFactory, Library, FileResolution, new ExternalProcessPdfPageRenderer(new SystemProcessRunner()), Clock, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteratureApp", "cache", "page-renders"));
        PageCoordinates = new PageCoordinateService(ConnectionFactory);
        var searchUnitBuilder = new SearchUnitBuilder(ConnectionFactory, Clock);
        SearchUnits = searchUnitBuilder;
        SearchIndex = new SearchIndexRebuilder(ConnectionFactory, Clock);
        var searchProfiles = new SearchProfileService(ConnectionFactory, Library, Clock);
        SearchProfiles = searchProfiles;
        QueryRewriter = searchProfiles;
        Search = new SqliteSearchService(ConnectionFactory, searchProfiles);
        Evidence = new EvidenceReferenceService(ConnectionFactory, Clock, PageCoordinates);
        Ocr = new OcrRunCoordinator(ConnectionFactory, Clock, new MockOcrEngine(), searchUnitBuilder, adapterRegistry, PageRenders, PageCoordinates);
        Mcp = new McpReadApi(ConnectionFactory, Search, Evidence, PageCoordinates);
        SnapshotPublisher = new SnapshotPublisher(Clock);
        SnapshotImporter = new SnapshotImporter();
        BranchInspection = new SnapshotBranchInspectionService(SnapshotImporter, ConnectionFactory, Library);
        Credentials = new CredentialStore(ConnectionFactory, Library, Clock);
        PdfMetadata = new PdfMetadataReader();
        PdfDiscovery = new PdfDiscoveryService();
        MinerUImporter = new MinerUResultImporter(ConnectionFactory, Clock);
        PdfImport = new PdfImportWorkflow(Files, Items, Documents, Pages, PdfMetadata, Clock);
        McpVerification = new McpVerificationService(ConnectionFactory, Mcp);
        FirstRunWorkflow = new FirstRunWorkflow(Library, PdfDiscovery, PdfImport, MinerUImporter, SearchUnits, SearchIndex, McpVerification);
    }
    public string RuntimeDatabasePath { get; }
    public SqliteConnectionFactory ConnectionFactory { get; }
    public IClock Clock { get; }
    public MigrationRunner MigrationRunner { get; }
    public ILibraryIdentityService Library { get; }
    public IItemService Items { get; }
    public IFileAssetService Files { get; }
    public IDocumentInstanceService Documents { get; }
    public IFileResolutionService FileResolution { get; }
    public IPageService Pages { get; }
    public ILayoutTreeService Layout { get; }
    public IOcrPresetService OcrPresets { get; }
    public IOcrModelPathValidator ModelPathValidator { get; }
    public IOcrAdapterRegistry OcrAdapters { get; }
    public IPageRenderService PageRenders { get; }
    public IPageCoordinateService PageCoordinates { get; }
    public IOcrRunCoordinator Ocr { get; }
    public ISearchUnitBuilder SearchUnits { get; }
    public ISearchIndexRebuilder SearchIndex { get; }
    public ISearchService Search { get; }
    public ISearchProfileService SearchProfiles { get; }
    public IQueryRewriter QueryRewriter { get; }
    public IEvidenceReferenceService Evidence { get; }
    public IMcpReadApi Mcp { get; }
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
    public static async Task<AppServices> CreateAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var logger = new SimpleFileLogger(AppRuntimeOptions.FromEnvironment().LogDirectory);
        try { await logger.LogAsync("startup", $"Opening runtime database {path}"); } catch { }
        var services = new AppServices(path);
        await services.MigrationRunner.RunAsync();
        try { await logger.LogAsync("migration", "Pending migrations completed."); } catch { }
        return services;
    }
}
