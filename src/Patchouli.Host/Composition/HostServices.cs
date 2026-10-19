using Patchouli.Core.Bibliography;
using Patchouli.Core.Agents;
using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Core.Cli;
using Patchouli.Core.Credentials;
using Patchouli.Core.Diagnostics;
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
using Patchouli.Core.Pagination;
using Patchouli.Core.Results;
using Patchouli.Core.Settings;
using Patchouli.Core.Time;
using Patchouli.Core.Translation;
using Patchouli.Core.Update;
using Patchouli.Core.View;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Agents;
using Patchouli.Infrastructure.Bibliography.Biblatex;
using Patchouli.Infrastructure.Bibliography.MetadataLookup;
using Patchouli.Infrastructure.Cli;
using Patchouli.Infrastructure.Credentials;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.Coordinates;
using Patchouli.Infrastructure.Conflicts;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Documents.Translations;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.Import;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Ocr.MinerU;
using Patchouli.Infrastructure.Ocr.NdlKoten;
using Patchouli.Infrastructure.Ocr.NdlLite;
using Patchouli.Infrastructure.Ocr.RapidOcr;
using Patchouli.Infrastructure.Operations;
using Patchouli.Infrastructure.Rendering;
using Patchouli.Infrastructure.Search;
using Patchouli.Infrastructure.Settings;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.Infrastructure.Update;
using Patchouli.Infrastructure.Workflows;
using Patchouli.Mcp;
using Patchouli.Ocr;
using Patchouli.Ocr.MinerU;
using Patchouli.Core.Search;
using Patchouli.Host.Caching;
using Patchouli.Host.Agents;
using Patchouli.Host.Lifecycle;
using Patchouli.Host.Mcp;
using Patchouli.Host.Translation;
using Patchouli.Host.Settings;
using Patchouli.UI;
using Patchouli.Llm;
using System.Collections.Generic;

namespace Patchouli.Host.Composition;

public sealed class HostServices
{
    private static readonly Action<Exception, string, string?> FallbackUnexpectedExceptionReporter =
        static (exception, boundary, operation) =>
            System.Diagnostics.Trace.WriteLine(
                $"Patchouli unexpected error at {boundary}/{operation}: {exception}");

    private readonly OcrRunEngine _ocrEngine;
    private readonly OnnxDocumentLayoutDetector _documentLayoutDetector = new();
    private readonly Action<Exception, string, string?> _reportUnexpectedException;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _shutdownGate = new();
    private readonly object _settingsCommitGate = new();

    private readonly Dictionary<string, long> _appliedSettingsCommitSequences =
        new(StringComparer.OrdinalIgnoreCase);

    private long _metadataLookupApplyGeneration;
    private Task? _shutdownTask;
    private LlmAppSettings _llmSettings;
    private AgentIntegrationAppSettings _agentSettings;
    private HttpClient? _cslCatalogHttpClient;
    private HttpClient? _metadataLookupHttpClient;
    private HttpClient? _ndlKotenModelHttpClient;
    private HttpClient? _ndlLiteModelHttpClient;
    private HttpClient? _rapidOcrModelHttpClient;
    private readonly HttpClient _layoutModelHttpClient = new() { Timeout = TimeSpan.FromMinutes(10) };
    // Issue #15: dedicated clients for update checks (short timeout) and installer
    // downloads (long timeout for ~100MB installers).
    private readonly HttpClient _updateApiHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient _updateDownloadHttpClient = new() { Timeout = TimeSpan.FromMinutes(15) };
    private IAppSettingsStore? _observedSettingsStore;

    private IReadOnlyList<Core.Bibliography.MetadataLookup.MetadataSourcePreference>
        _metadataLookupPreferences = [];

    private HostServices(string runtimeDatabasePath, PatchouliAppSettings settings, string settingsPath,
        IAppLogger logger, Action<Exception, string, string?>? reportUnexpectedException = null,
        IHostActivityTracker? activityTracker = null)
    {
        _reportUnexpectedException = reportUnexpectedException ?? FallbackUnexpectedExceptionReporter;
        ActivityTracker = activityTracker;
        RuntimeDatabasePath = runtimeDatabasePath;
        StartupSettings = settings;
        _llmSettings = settings.Llm;
        _agentSettings = settings.Agents;
        AgentPlatformEnvironment = new PlatformAgentEnvironment();
        AgentProcessRunner = new SystemProcessRunner();
        AgentInstallOffers = new AgentRegistryInstallOfferProvider(AgentRegistryLocations.Resolve(),
            AgentPlatformEnvironment);
        AgentDetection = new AgentDetectionService(AgentProcessRunner, AgentPlatformEnvironment, AgentInstallOffers);
        AgentWorkspace = AgentWorkspaceLocator.FromRuntimeDatabase(runtimeDatabasePath);
        AgentLauncher = new AgentLaunchService(AgentDetection, AgentWorkspace);
        AgentMcpConfig = new AgentMcpConfigService();
        AppStorageLocations appPaths = new PlatformAppPaths().Resolve();
        OcrStorage = OcrStorageLocations.FromResolved(appPaths);
        LayoutModelDownload = new LayoutModelDownloadService(_layoutModelHttpClient, OcrStorage.GetLayoutModelPath);
        ConnectionFactory = new SqliteConnectionFactory(runtimeDatabasePath);
        Clock = new SystemClock();
        BlockingOperations = new BlockingOperationService(ConnectionFactory, Clock);
        MigrationRunner = new MigrationRunner(ConnectionFactory, Path.Combine(AppContext.BaseDirectory, "migrations"));
        Library = new LibraryIdentityService(ConnectionFactory, Clock);
        LibraryRevisions = new LibraryRevisionService(ConnectionFactory);
        LibraryItems = new LibraryItemQueryService(ConnectionFactory);
        Items = new ItemService(ConnectionFactory, Library, Clock, LibraryRevisions);
        Tags = new ItemTagService(ConnectionFactory, LibraryRevisions);
        Collections = new CollectionService(ConnectionFactory, Library, Clock, LibraryRevisions);
        LibraryItemCache = new LibraryItemCache(LibraryItems, Tags);
        LibraryRevisionMonitor = new LibraryRevisionMonitor(LibraryRevisions, LibraryItemCache,
            reportUnexpectedException: _reportUnexpectedException);
        MergeItems = new ItemMergeService(ConnectionFactory, Clock, Library, LibraryRevisions);
        DuplicateItemDetection = new DuplicateItemDetectionService(ConnectionFactory, Library);
        _cslCatalogHttpClient = new HttpClient();
        CslCatalog = new CslStyleCatalog(ConnectionFactory, _cslCatalogHttpClient);
        CslStore = new CslStyleStore(ConnectionFactory, Clock, blockingOperations: BlockingOperations,
            revisions: LibraryRevisions);
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
        Files = new FileAssetService(ConnectionFactory, Library, Clock, revisions: LibraryRevisions);
        Documents = new DocumentInstanceService(ConnectionFactory, Clock, LibraryRevisions);
        INativeFileAccessAdapter? nativeAdapter = OperatingSystem.IsMacOS()
            ? new MacOSNativeFileAccessAdapter()
            : null;
        FileSearchRootAccess = new FileSearchRootAccess(nativeAdapter,
            settings.FileScanning.ExclusionPatterns);
        AppSettingsDeviceRootBindingStore rootBindings = new(settingsPath);
        FileResolution = new FileResolutionService(ConnectionFactory, Library, Clock,
            blockingOperations: BlockingOperations, rootAccess: FileSearchRootAccess, rootBindings: rootBindings,
            revisions: LibraryRevisions);
        BiblatexHelper = new BiblatexHelperClient();
        BiblatexImport = new BiblatexImportService(BiblatexHelper, Items, Files, Documents, activityTracker,
            LifetimeToken);
        ConflictActions = new ConflictActionExecutorRegistry(
        [
            new FileConflictActionExecutor(FileResolution, ConflictCode.FileRelocationMultipleCandidates),
            new FileConflictActionExecutor(FileResolution, ConflictCode.SourceFileChangedOrBBoxBasisStale),
            new BiblatexConflictActionExecutor(ConflictCode.BiblatexItemFieldConflict),
            new BiblatexConflictActionExecutor(ConflictCode.BiblatexBatchLinkCandidates)
        ]);
        Pages = new PageService(ConnectionFactory, Clock);
        Markdown = new MarkdigMarkdownEngine();
        CompiledMarkdownCache = new CompiledMarkdownCache();
        DocumentTrees = new DocumentTreeService(ConnectionFactory, Clock, Markdown, LibraryRevisions,
            CompiledMarkdownCache);
        DocumentTreeEditor = (IDocumentTreeEditor)DocumentTrees;
        Overlaps = new OverlapProjectionService();
        DocumentMarkdown = new CachedDocumentMarkdownCompiler(
            new DocumentMarkdownCompiler(DocumentTrees, Markdown), CompiledMarkdownCache);
        PageTranslationCache pageTranslationCache = new();
        PageTranslations = new PageTranslationService(
            ConnectionFactory,
            DocumentTrees,
            DocumentMarkdown,
            Markdown,
            new CachedPageTranslationCompiler(
                new PageTranslationCompiler(ConnectionFactory, DocumentTrees, Markdown), pageTranslationCache),
            pageTranslationCache,
            Clock, LibraryRevisions);
        OcrPresets = new OcrPresetService(ConnectionFactory, Library, Clock);
        OcrDefaults = new OcrPresetDefaults(OcrPresets, OcrStorage, () => LlmSettings);
        ModelPathValidator = new OcrModelPathValidator();
        Credentials = new CredentialStore(settingsPath);
        OcrAdapterRegistry adapterRegistry = new();
        if (settings.Runtime.UseMockOcrOnly)
        {
            adapterRegistry.RegisterAdapter(new MockOcrAdapter());
            adapterRegistry.RegisterAdapter(new LocalPlaceholderOcrAdapter(ModelPathValidator));
        }

        adapterRegistry.RegisterAdapter(new MinerUOcrAdapter());
        adapterRegistry.RegisterAdapter(new LayoutAwareMultimodalOcrAdapter(
            new MultimodalLlmOcrAdapter(new MultimodalLlmOcrRuntime(() => LlmSettings, Credentials)),
            _documentLayoutDetector, () => LlmSettings, OcrStorage.GetLayoutModelPath));
        adapterRegistry.RegisterAdapter(new NdlKotenOcrAdapter(ModelPathValidator));
        adapterRegistry.RegisterAdapter(new NdlLiteOcrAdapter(ModelPathValidator));
        adapterRegistry.RegisterAdapter(new RapidOcrOcrAdapter(ModelPathValidator, OcrStorage.RapidOcrModelsDirectory));

        OcrAdapters = adapterRegistry;
        _ndlKotenModelHttpClient = new HttpClient();
        NdlKotenModelDownload =
            new NdlKotenModelDownloadService(_ndlKotenModelHttpClient, OcrStorage.NdlKotenModelsDirectory);
        _ndlLiteModelHttpClient = new HttpClient();
        NdlLiteModelDownload =
            new NdlLiteModelDownloadService(_ndlLiteModelHttpClient, OcrStorage.NdlLiteModelsDirectory);
        _rapidOcrModelHttpClient = new HttpClient();
        RapidOcrModelDownload =
            new RapidOcrModelDownloadService(_rapidOcrModelHttpClient, OcrStorage.RapidOcrModelsDirectory);
        PdfiumPdfPageRenderer pdfRenderer = new();
        PdfPreviewRenderer = pdfRenderer;
        PageRenders = new PageRenderService(ConnectionFactory, Library, FileResolution, pdfRenderer, Clock,
            Path.Combine(appPaths.CacheDirectory, "page-renders"), FileSearchRootAccess);
        SourceFingerprintValidation = new SourceFingerprintValidationService();
        PageCoordinates = new PageCoordinateService(ConnectionFactory, SourceFingerprintValidation);
        SearchUnitBuilder searchUnitBuilder = new(ConnectionFactory, Clock, Markdown);
        SearchUnits = searchUnitBuilder;
        SearchIndex = new SearchIndexRebuilder(ConnectionFactory, Clock, activityTracker, LifetimeToken);
        OcrDocumentTreeImporter ocrTreeImporter = new(DocumentTrees);
        SearchProfileService searchProfiles = new(ConnectionFactory, Library, Clock, new OpenccTextConverter());
        SearchProfiles = searchProfiles;
        QueryRewriter = searchProfiles;
        Search = new SqliteSearchService(ConnectionFactory, searchProfiles);
        VersionedEvidenceReader =
            new VersionedEvidenceReader(ConnectionFactory, Library, DocumentTrees, DocumentMarkdown);
        MinerUImporter = new MinerUResultImporter(ConnectionFactory, Clock, ocrTreeImporter);
        IOcrEngine pageOcrEngine = settings.Runtime.UseMockOcrOnly ? new MockOcrEngine() : new UnavailableOcrEngine();
        _ocrEngine = new OcrRunEngine(ConnectionFactory, Clock, Credentials.GetActiveSecretForProviderAsync,
            pageOcrEngine, searchUnitBuilder, ocrTreeImporter,
            adapterRegistry, PageRenders, PageCoordinates, MinerUImporter,
            configuration => (MinerUClientFactoryOverride ?? CreateMinerUClient)(configuration),
            OcrStorage.MinerUWorkDirectory,
            fileResolution: FileResolution, fileMaterialization: FileSearchRootAccess, revisions: LibraryRevisions);
        OcrQueueTaskExecutor ocrQueueExecutor = new(_ocrEngine, SearchUnits, SearchIndex, activityTracker);
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
                _reportUnexpectedException(exception, "ocr-scheduler", "scheduler-loop"),
            activityTracker: activityTracker);
        Ocr = new QueuedOcrRunCoordinator(ocrQueueScheduler, _ocrEngine, () => LlmSettings.OcrSelection.ProviderId);
        // The public start/cancel/retry orchestration behind send OCR Actions (issue #16): page
        // ranges, engine/preset resolution with pinned versions, readiness preflight and atomic
        // request dedup all live here; transports only speak IMcpOcrCommandApi.
        OcrCommands = new OcrCommandService(
            ocrQueueScheduler,
            _ocrEngine,
            OcrPresets,
            OcrDefaults,
            adapterRegistry,
            () => StartupSettings.OcrEngines.DocumentOcrEngine);
        // Issue #20 (B1): the direct batch translation engine is host-owned state, not an Agent or
        // workflow. Every run reads the live LLM/Translation settings when it starts; runs are
        // volatile and keep the engine's per-page outcomes for MCP observation and cancellation.
        TranslationRuns = new TranslationRunService(
            Pages,
            DocumentTrees,
            PageTranslations,
            () => LlmSettings,
            Credentials,
            hostLifetime: LifetimeToken);
        LogicalPageOcr = new LogicalPageOcrService(Ocr, DocumentTrees);
        DocumentPageMaps = new DocumentPageMapService(ConnectionFactory, Clock, Documents, Pages, DocumentTrees,
            LibraryRevisions);
        ChapterScanner = new ChapterScanService(Pages, DocumentTrees, DocumentMarkdown, Markdown);
        McpSettings = new McpServerSettingsService(settingsPath, Clock, BlockingOperations);
        Mcp = new McpReadApi(
            ConnectionFactory, Search, PageCoordinates, CslStore, CslRenderer, Markdown, DocumentMarkdown,
            CompiledMarkdownCache, PageTranslations, DocumentPageMaps);
        McpWrites = new McpWriteApi(Items, BiblatexHelper, CslStore, PageTranslations, DocumentTrees,
            DocumentPageMaps);
        CliPath = new CliPathService();
        // Every MCP client acts through the same MCP command surface: identical validation,
        // permissions, atomic write set and error vocabulary.
        McpCommands = new McpCommandService(Mcp, McpWrites, BiblatexImport, Items, VersionedEvidenceReader,
            permissionSettings: async cancellationToken =>
            {
                Result<McpServerSettings> permissions =
                    await McpSettings.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
                return permissions.IsSuccess
                    ? permissions.Value
                    : throw new InvalidOperationException(permissions.ErrorMessage);
            },
            ocrCommands: OcrCommands,
            translationRuns: new McpHostTranslationRunsApi(TranslationRuns));
        SnapshotPublisher = new SnapshotPublisher(Clock);
        SnapshotImporter = new SnapshotImporter(BlockingOperations);
        BranchInspection = new SnapshotBranchInspectionService(SnapshotImporter, ConnectionFactory, Library);
        SnapshotSyncSettingsStore snapshotSyncSettingsStore =
            new(runtimeDatabasePath, settingsPath, settings.Runtime.DefaultStagingRoot);
        SnapshotSync = new SnapshotSyncCoordinator(
            SnapshotPublisher,
            SnapshotImporter,
            BranchInspection,
            snapshotSyncSettingsStore,
            Clock,
            activityTracker,
            LifetimeToken);
        PurgeItems =
            new ItemPurgeService(ConnectionFactory, Clock, Library, snapshotSyncSettingsStore, LibraryRevisions);
        FileAssetGc = new FileAssetGcService(ConnectionFactory, snapshotSyncSettingsStore, logger);
        PdfMetadataReader pdfMetadata = new();
        PdfMetadata = pdfMetadata;
        PdfDiscovery = new PdfDiscoveryService(FileSearchRootAccess, activityTracker, LifetimeToken);
        PdfImport = new PdfImportWorkflow(
            new ImportBatchWriter(ConnectionFactory, LibraryRevisions),
            pdfMetadata,
            Clock,
            Library,
            itemTypeInferenceService: ItemTypeInference,
            activityTracker: activityTracker,
            hostLifetime: LifetimeToken,
            pageInfoReader: pdfMetadata,
            maxFailedPageRatio: settings.Import.MaxFailedPageRatio);
        // Issue #11: shared local file import core (MCP add, CLI, drag-drop). The behavior
        // settings are device-local; the recovery store lives in the runtime database.
        FileHandlingBehaviorSettings =
            new FileHandlingBehaviorSettingsService(settingsPath, Clock);
        // Issue #15: application auto-update (direct GitHub source; a future
        // reverse proxy only needs to implement IUpdateSource).
        UpdateCheckSettings = new UpdateCheckSettingsService(settingsPath, Clock);
        UpdateSource = new GitHubReleaseSource(_updateApiHttpClient);
        UpdateCoordinator = new UpdateCoordinator(
            UpdateSource,
            UpdateCheckSettings,
            new UpdateDownloader(_updateDownloadHttpClient),
            new UpdateLauncher(),
            Clock,
            Patchouli.Core.BuildInfo.Version);
        CslLocalInstall = new CslLocalFileInstallService(CslStore);
        FileImport = new FileImportService(
            PdfImport,
            CslLocalInstall,
            FileHandlingBehaviorSettings,
            FileResolution,
            new FileFingerprintService(),
            new FileImportRecoveryStore(ConnectionFactory),
            Clock);
        // Issue #11: source-image reading for the view tool.
        PageImageView = new PageImageViewService(Pages, DocumentTrees, PageRenders);
        McpVerification = new McpVerificationService(ConnectionFactory, Mcp);
        FirstRunWorkflow = new FirstRunWorkflow(Library, PdfDiscovery, PdfImport, BlockingOperations);
    }

    public string RuntimeDatabasePath { get; }
    public IHostActivityTracker? ActivityTracker { get; }
    public CancellationToken LifetimeToken => _lifetimeCancellation.Token;

    /// <summary>Settings whose effects are applied while this host is constructed.</summary>
    public PatchouliAppSettings StartupSettings { get; }

    /// <summary>Live LLM settings shared by OCR and translation without rebuilding the Library host.</summary>
    public LlmAppSettings LlmSettings => Volatile.Read(ref _llmSettings);

    public void UpdateLlmSettings(LlmAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Volatile.Write(ref _llmSettings, settings);
    }

    /// <summary>Live Agent integration settings shared by detection, launch and the settings UI.</summary>
    public AgentIntegrationAppSettings AgentSettings => Volatile.Read(ref _agentSettings);

    public void UpdateAgentSettings(AgentIntegrationAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Volatile.Write(ref _agentSettings, settings);
        AgentDetection.InvalidateCache();
    }

    public PlatformAgentEnvironment AgentPlatformEnvironment { get; }
    public IProcessRunner AgentProcessRunner { get; }
    public IAgentInstallOfferProvider AgentInstallOffers { get; }
    public AgentDetectionService AgentDetection { get; }
    public AgentWorkspaceLocator AgentWorkspace { get; }
    public AgentLaunchService AgentLauncher { get; }
    public AgentMcpConfigService AgentMcpConfig { get; }

    public SqliteConnectionFactory ConnectionFactory { get; }
    public IClock Clock { get; }
    public IBlockingOperationService BlockingOperations { get; }
    public MigrationRunner MigrationRunner { get; }
    public ILibraryIdentityService Library { get; }
    public ILibraryRevisionService LibraryRevisions { get; }
    public ILibraryItemQueryService LibraryItems { get; }
    public IItemService Items { get; }
    public IItemTagService Tags { get; }
    public ICollectionService Collections { get; }
    public LibraryItemCache LibraryItemCache { get; }
    public LibraryRevisionMonitor LibraryRevisionMonitor { get; }
    public IItemMergeService MergeItems { get; }
    public IDuplicateItemDetectionService DuplicateItemDetection { get; }
    public IItemPurgeService PurgeItems { get; }
    public IFileAssetGcService FileAssetGc { get; }
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
    public IOverlapProjectionService Overlaps { get; }
    public ICompiledMarkdownCache CompiledMarkdownCache { get; }
    public IDocumentMarkdownCompiler DocumentMarkdown { get; }
    public IPageTranslationService PageTranslations { get; }
    public IOcrPresetService OcrPresets { get; }
    public OcrPresetDefaults OcrDefaults { get; }
    public IOcrModelPathValidator ModelPathValidator { get; }
    public IOcrAdapterRegistry OcrAdapters { get; }
    public OcrStorageLocations OcrStorage { get; }
    public LayoutModelDownloadService LayoutModelDownload { get; }
    public INdlKotenModelDownloadService NdlKotenModelDownload { get; }
    public INdlLiteModelDownloadService NdlLiteModelDownload { get; }
    public IRapidOcrModelDownloadService RapidOcrModelDownload { get; }
    public IPageRenderService PageRenders { get; }
    public IPdfPagePixelBufferRenderer PdfPreviewRenderer { get; }
    public IPageCoordinateService PageCoordinates { get; }
    public ISourceFingerprintValidationService SourceFingerprintValidation { get; }
    public IOcrRunCoordinator Ocr { get; }
    public IMcpOcrCommandApi OcrCommands { get; }
    public ITranslationRunService TranslationRuns { get; }
    public Func<MinerUConfiguration, IMinerUClient>? MinerUClientFactoryOverride { get; set; }
    public ILogicalPageOcrService LogicalPageOcr { get; }
    public IDocumentPageMapService DocumentPageMaps { get; }
    public IChapterScanService ChapterScanner { get; }
    public ISearchUnitBuilder SearchUnits { get; }
    public ISearchIndexRebuilder SearchIndex { get; }
    public ISearchService Search { get; }
    public ISearchProfileService SearchProfiles { get; }
    public IQueryRewriter QueryRewriter { get; }
    public IVersionedEvidenceReader VersionedEvidenceReader { get; }
    public IMcpReadApi Mcp { get; }
    public IMcpWriteApi McpWrites { get; }
    public IMcpServerSettingsService McpSettings { get; }
    public McpCommandService McpCommands { get; }
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
    public IFileHandlingBehaviorSettingsService FileHandlingBehaviorSettings { get; }
    public IUpdateCheckSettingsService UpdateCheckSettings { get; }
    public IUpdateSource UpdateSource { get; }
    public UpdateCoordinator UpdateCoordinator { get; }
    public ICslLocalFileInstallService CslLocalInstall { get; }
    public IFileImportService FileImport { get; }
    public IPageImageViewService PageImageView { get; }
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

    public void ObserveSettingsStore(IAppSettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        if (ReferenceEquals(_observedSettingsStore, settingsStore))
        {
            return;
        }

        if (_observedSettingsStore is not null)
        {
            _observedSettingsStore.SettingsCommitted -= OnSettingsCommitted;
        }

        lock (_settingsCommitGate)
        {
            _appliedSettingsCommitSequences.Clear();
            _metadataLookupApplyGeneration++;
        }

        _observedSettingsStore = settingsStore;
        settingsStore.SettingsCommitted += OnSettingsCommitted;
    }

    private void OnSettingsCommitted(object? sender, AppSettingsCommittedEventArgs change)
    {
        if (change.Categories.Contains("Sync", StringComparer.OrdinalIgnoreCase))
        {
            lock (_settingsCommitGate)
            {
                if (AcceptSettingsCommitLocked("Sync", change.CommitSequence) &&
                    AcceptMetadataLookupDependencyLocked(change.CommitSequence))
                {
                    _ = ApplyCommittedMetadataLookupAsync(change.Snapshot,
                        ++_metadataLookupApplyGeneration);
                }
            }
        }

        if (change.Categories.Contains("Llm", StringComparer.OrdinalIgnoreCase) &&
            change.CommitSequence > 0)
        {
            lock (_settingsCommitGate)
            {
                if (AcceptSettingsCommitLocked("Llm", change.CommitSequence))
                {
                    UpdateLlmSettings(change.Snapshot.Llm);
                }
            }
        }

        if (change.Categories.Contains("Agents", StringComparer.OrdinalIgnoreCase) &&
            change.CommitSequence > 0)
        {
            lock (_settingsCommitGate)
            {
                if (AcceptSettingsCommitLocked("Agents", change.CommitSequence))
                {
                    UpdateAgentSettings(change.Snapshot.Agents);
                }
            }
        }

        if (change.Categories.Contains("MetadataLookup", StringComparer.OrdinalIgnoreCase) &&
            change.CommitSequence > 0)
        {
            lock (_settingsCommitGate)
            {
                if (AcceptMetadataLookupDependencyLocked(change.CommitSequence))
                {
                    _ = ApplyCommittedMetadataLookupAsync(change.Snapshot,
                        ++_metadataLookupApplyGeneration);
                }
            }
        }

        if (change.Categories.Contains("FileScanning", StringComparer.OrdinalIgnoreCase) &&
            change.CommitSequence > 0)
        {
            lock (_settingsCommitGate)
            {
                if (AcceptSettingsCommitLocked("FileScanning", change.CommitSequence))
                {
                    UpdateFileScanExclusions(change.Snapshot.FileScanning);
                }
            }
        }
    }

    private bool AcceptSettingsCommitLocked(string category, long commitSequence)
    {
        if (_appliedSettingsCommitSequences.TryGetValue(category, out long applied) &&
            commitSequence <= applied)
        {
            return false;
        }

        _appliedSettingsCommitSequences[category] = commitSequence;
        return true;
    }

    private bool AcceptMetadataLookupDependencyLocked(long commitSequence)
    {
        if (_appliedSettingsCommitSequences.TryGetValue("MetadataLookup", out long applied) &&
            commitSequence <= applied)
        {
            return false;
        }

        _appliedSettingsCommitSequences["MetadataLookup"] = commitSequence;
        return true;
    }

    private async Task ApplyCommittedMetadataLookupAsync(PatchouliAppSettings settings, long generation)
    {
        try
        {
            Result<LibraryMetadata> library = await Library.GetCurrentLibraryAsync(LifetimeToken)
                .ConfigureAwait(false);
            if (library.IsFailure)
            {
                return;
            }

            if (settings.Sync.IsSettingEnabled(LibrarySettingKeys.MetadataLookup, library.Value.LibraryId))
            {
                Result<MetadataLookupAppSettings?> synced = await GetSyncedMetadataLookupAsync(LifetimeToken)
                    .ConfigureAwait(false);
                if (synced.IsSuccess && synced.Value is not null)
                {
                    lock (_settingsCommitGate)
                    {
                        if (_metadataLookupApplyGeneration == generation)
                        {
                            UpdateMetadataLookupPreferences(synced.Value);
                        }
                    }
                }

                return;
            }

            lock (_settingsCommitGate)
            {
                if (_metadataLookupApplyGeneration == generation)
                {
                    UpdateMetadataLookupPreferences(settings.MetadataLookup);
                }
            }
        }
        catch (OperationCanceledException) when (LifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _reportUnexpectedException(exception, "settings-commit", "apply-metadata-lookup");
        }
    }

    private static IReadOnlyList<Core.Bibliography.MetadataLookup.MetadataSourcePreference>
        ToMetadataLookupPreferences(MetadataLookupAppSettings settings)
    {
        return settings.Sources.Select((source, index) =>
            new Core.Bibliography.MetadataLookup.MetadataSourcePreference(source.SourceId, source.Enabled,
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
            BaseUrl = configuration.BaseUrl ?? StartupSettings.MinerU.BaseUrl,
            ModelVersion = configuration.ModelVersion ?? StartupSettings.MinerU.ModelVersion,
            IsOcr = configuration.IsOcr,
            EnableTable = configuration.EnableTable,
            EnableFormula = configuration.EnableFormula,
            PollingTimeoutSeconds = configuration.PollingTimeoutSeconds
        });
    }

    public static async Task<HostServices> CreateAsync(string path, PatchouliAppSettings? settings = null,
        string? settingsPath = null, IProgress<MigrationProgress>? migrationProgress = null,
        IAppLogger? logger = null, Action<Exception, string, string?>? reportUnexpectedException = null,
        IProgress<StartupStage>? startupProgress = null, IHostActivityTracker? activityTracker = null,
        RuntimeHostLease? runtimeHostLease = null)
    {
        Action<Exception, string, string?> reportUnexpected =
            reportUnexpectedException ?? FallbackUnexpectedExceptionReporter;
        runtimeHostLease?.ValidateOwnership(path);
        settingsPath ??= PatchouliAppSettings.ResolvePath();
        settings ??= PatchouliAppSettings.Load(settingsPath);
        startupProgress?.Report(StartupStage.ValidatingPaths);
        AppPathGuard.ValidateDatabasePath(path, settings.Runtime.DefaultSyncRoot);
        AppPathGuard.ValidateMutablePath(settings.Runtime.LogDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        IAppLogger startupLogger = logger ?? new SimpleFileLogger(settings.Runtime.LogDirectory);
        try
        {
            await startupLogger.LogAsync("startup", $"Opening runtime database {path}");
        }
        catch (Exception exception)
        {
            reportUnexpected(exception, "operation-log", "startup");
        }

        startupProgress?.Report(StartupStage.ComposingServices);
        HostServices services = new(path, settings, settingsPath, startupLogger, reportUnexpected, activityTracker);

        // Dapper runs synchronous I/O under the covers; offload the whole blocking DB bootstrap
        // onto the thread pool so the UI thread stays responsive while the loading page is shown.
        await Task.Run(async () =>
        {
            startupProgress?.Report(StartupStage.ApplyingMigrations);
            await services.MigrationRunner.RunAsync(CancellationToken.None, migrationProgress);
            if (runtimeHostLease is not null)
            {
                try
                {
                    await PageEditSessionRecovery.RecoverAsync(services.ConnectionFactory, runtimeHostLease, path);
                }
                catch
                {
                    await services.ShutdownAsync();
                    throw;
                }
            }

            Result ftsCache = await services.SearchIndex.EnsureCacheAsync();
            if (ftsCache.IsFailure)
            {
                try
                {
                    await startupLogger.LogAsync("search-index",
                        ftsCache.ErrorMessage ?? "FTS cache initialization failed.");
                }
                catch (Exception exception)
                {
                    reportUnexpected(exception, "operation-log", "search-index-cache");
                }
            }

            if (services.FileResolution is FileResolutionService fileResolution)
            {
                startupProgress?.Report(StartupStage.AdoptingRootBindings);
                Result adopted = await fileResolution.AdoptLegacyDeviceRootBindingsAsync();
                if (adopted.IsFailure)
                {
                    try
                    {
                        await startupLogger.LogAsync("migration",
                            adopted.ErrorMessage ?? "Legacy root binding migration failed.");
                    }
                    catch (Exception exception)
                    {
                        reportUnexpected(exception, "operation-log", "legacy-root-binding-migration");
                    }
                }
            }

            startupProgress?.Report(StartupStage.ReconcilingOcrRuns);
            Result ocrReconcile = await services._ocrEngine.ReconcileInterruptedRunsAsync();
            if (ocrReconcile.IsFailure)
            {
                try
                {
                    await startupLogger.LogAsync("ocr-reconcile",
                        ocrReconcile.ErrorMessage ?? "OCR startup reconciliation failed.");
                }
                catch (Exception exception)
                {
                    reportUnexpected(exception, "operation-log", "ocr-reconcile");
                }
            }

            try
            {
                ImportResidueGcService importResidueGc = new(
                    services.ConnectionFactory,
                    services.Clock,
                    services.LibraryRevisions,
                    services.FileAssetGc,
                    startupLogger);
                ImportResidueGcResult residueResult = await importResidueGc.RunOnceAsync();
                await startupLogger.LogAsync("import-residue-gc",
                    $"Startup import residue GC finished: instancesRemoved={residueResult.InstancesRemoved}, " +
                    $"itemsRemoved={residueResult.ItemsRemoved}, fileAssetsRemoved={residueResult.FileAssetsRemoved}.");
            }
            catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                                  exception,
                                                  "host.composition",
                                                  "import-residue-gc"))
            {
            }
        });

        startupProgress?.Report(StartupStage.StartingOcrQueue);
        await ((QueuedOcrRunCoordinator)services.Ocr).Queue.StartAsync();

        startupProgress?.Report(StartupStage.ApplyingSyncedSettings);
        await services.ApplySyncedMetadataLookupAsync(settings);

        try
        {
            await startupLogger.LogAsync("migration", "Pending migrations completed.");
        }
        catch (Exception exception)
        {
            reportUnexpected(exception, "operation-log", "migration");
        }

        return services;
    }

    /// <summary>Quiesces background work before a runtime-host ownership lease is released.</summary>
    public Task ShutdownAsync()
    {
        lock (_shutdownGate)
        {
            return _shutdownTask ??= ShutdownCoreAsync();
        }
    }

    private async Task ShutdownCoreAsync()
    {
        if (_observedSettingsStore is not null)
        {
            _observedSettingsStore.SettingsCommitted -= OnSettingsCommitted;
            _observedSettingsStore = null;
        }

        _lifetimeCancellation.Cancel();
        await ((QueuedOcrRunCoordinator)Ocr).Queue.StopAsync();
        if (TranslationRuns is IAsyncDisposable translationRuns)
        {
            await translationRuns.DisposeAsync();
        }

        await LibraryRevisionMonitor.DisposeAsync();
        _documentLayoutDetector.Dispose();
        _layoutModelHttpClient.Dispose();
        _updateApiHttpClient.Dispose();
        _updateDownloadHttpClient.Dispose();
        ConnectionFactory.ClearPools();
    }
}
