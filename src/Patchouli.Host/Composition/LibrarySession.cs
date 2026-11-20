using Patchouli.Core.Diagnostics;
using Patchouli.Core.Library;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Host.Caching;
using Patchouli.Host.CitationInterop;
using Patchouli.Host.Import;
using Patchouli.Host.Lifecycle;
using Patchouli.Host.Mcp;
using Patchouli.Host.Watching;
using Patchouli.Infrastructure.Migrations;
using Patchouli.UI;

namespace Patchouli.Host.Composition;

/// <summary>
/// Owns one opened Library and the lifetime of its runtime resources (issue #84, ADR 0058
/// resource ownership): the runtime-host lease, <see cref="HostServices"/>, the MCP server
/// host, the citation interop host, the file-search-root watcher fleet, the import
/// orchestrator and the <see cref="LibraryRevisionMonitor"/> stop. Desktop and headless
/// hosts share this single open → attach → dispose sequence; view models subscribe to the
/// exposed components and never create or dispose them. Disposal is ordered, idempotent and
/// always releases the lease, including when the open itself fails partway; partial-open
/// failure and normal disposal run the same teardown routine.
/// </summary>
public sealed class LibrarySession
    : ILibrarySession<HostServices, McpServerHost, CitationInteropHost,
        FileSearchRootWatcherService, LibraryImportOrchestrator>
{
    private static readonly Action<Exception, string, string?> FallbackReport =
        static (exception, boundary, operation) =>
            System.Diagnostics.Trace.WriteLine(
                $"Patchouli unexpected error at {boundary}/{operation}: {exception}");

    private readonly RuntimeHostLease? _lease;
    private readonly LibraryRevisionMonitor _monitor;
    private readonly Action<Exception, string, string?> _reportUnexpected;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    private LibrarySession(HostServices services, RuntimeHostLease? lease, McpServerHost mcp,
        CitationInteropHost citation, FileSearchRootWatcherService? watcher,
        LibraryImportOrchestrator? orchestrator, LibraryRevisionMonitor monitor,
        Action<Exception, string, string?> reportUnexpected)
    {
        Services = services;
        _lease = lease;
        Mcp = mcp;
        CitationInterop = citation;
        Watcher = watcher;
        ImportOrchestrator = orchestrator;
        _monitor = monitor;
        _reportUnexpected = reportUnexpected;
        // Keep /health identity correct across MCP restarts (e.g. a settings-apply
        // restart): the lease instance id is re-stamped whenever the listener runs again.
        Mcp.StatusChanged += OnOwnMcpStatusChanged;
    }

    /// <summary>The opened composition root.</summary>
    public HostServices Services { get; }

    /// <summary>The per-Library MCP server host (created by the session; start is explicit).</summary>
    public McpServerHost Mcp { get; }

    /// <summary>The per-Library citation interop host.</summary>
    public CitationInteropHost CitationInterop { get; }

    /// <summary>The file-search-root watcher fleet, when the session creates one (desktop).</summary>
    public FileSearchRootWatcherService? Watcher { get; }

    /// <summary>The import orchestrator, when the session creates one (desktop).</summary>
    public LibraryImportOrchestrator? ImportOrchestrator { get; }

    /// <inheritdoc />
    public (long Active, long Total) McpConnectionCounts =>
        (Mcp.Server?.ActiveConnectionCount ?? 0, Mcp.Server?.TotalConnectionCount ?? 0);

    McpServerHost ILibrarySession<HostServices, McpServerHost, CitationInteropHost,
        FileSearchRootWatcherService, LibraryImportOrchestrator>.McpHost => Mcp;

    CitationInteropHost ILibrarySession<HostServices, McpServerHost, CitationInteropHost,
        FileSearchRootWatcherService, LibraryImportOrchestrator>.CitationInteropHost => CitationInterop;

    FileSearchRootWatcherService? ILibrarySession<HostServices, McpServerHost, CitationInteropHost,
        FileSearchRootWatcherService, LibraryImportOrchestrator>.FileSearchRootWatcher => Watcher;

    LibraryImportOrchestrator? ILibrarySession<HostServices, McpServerHost, CitationInteropHost,
        FileSearchRootWatcherService, LibraryImportOrchestrator>.ImportOrchestrator => ImportOrchestrator;

    /// <summary>
    /// Opens the session: acquires the lease when <paramref name="request"/> asks for one,
    /// runs the version gate and service composition via <see cref="HostServices.CreateAsync"/>
    /// (the #62 gate runs inside, before any composition), then creates the per-Library host
    /// wrappers. Any failure after the lease was acquired runs the shared teardown and
    /// releases the lease before the exception is rethrown. Cancellation is observed at the
    /// open boundaries (lease, before/after composition) because <see cref="HostServices.CreateAsync"/>
    /// takes no token.
    /// </summary>
    public static async Task<LibrarySession> OpenAsync(LibrarySessionOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CreateWatcherAndOrchestrator && request.WatcherLogger is null)
        {
            throw new ArgumentException(
                "WatcherLogger is required when the watcher and orchestrator are created.",
                nameof(request));
        }

        Action<Exception, string, string?> report = request.ReportUnexpected ?? FallbackReport;

        RuntimeHostLease? lease = request.Ownership switch
        {
            LibrarySessionOwnership.Desktop => await RuntimeHostCoordinator.AcquireDesktopAsync(
                    request.DatabasePath, TimeSpan.FromSeconds(10), request.LeaseDirectory, cancellationToken)
                .ConfigureAwait(false),
            LibrarySessionOwnership.Headless => RuntimeHostCoordinator.TryAcquire(
                                                    request.DatabasePath, RuntimeHostKind.Headless,
                                                    request.LeaseDirectory)
                                                ?? throw new LibrarySessionAlreadyOwnedException(
                                                    "the Library is already owned by another runtime host"),
            _ => null
        };

        HostServices? services = null;
        McpServerHost? mcp = null;
        CitationInteropHost? citation = null;
        FileSearchRootWatcherService? watcher = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The Core seam reports progress as IProgress<object>; adapt per report type
            // (variance does not apply to value types such as the StartupStage enum).
            IProgress<MigrationProgress>? migrationProgress = WrapReport<MigrationProgress>(request.MigrationProgress);
            IProgress<StartupStage>? startupProgress = WrapReport<StartupStage>(request.StartupProgress);
            services = await HostServices.CreateAsync(
                lease?.DatabasePath ?? request.DatabasePath,
                CastRequestField<PatchouliAppSettings>(request.Settings, nameof(request.Settings)),
                request.SettingsPath,
                migrationProgress,
                reportUnexpectedException: report,
                startupProgress: startupProgress,
                activityTracker: request.ActivityTracker,
                runtimeHostLease: lease,
                ocrComposition: CastRequestField<HostOcrCompositionOptions>(request.OcrComposition,
                    nameof(request.OcrComposition)),
                appPaths: CastRequestField<IAppPaths>(request.AppPaths, nameof(request.AppPaths)),
                infrastructure: CastRequestField<HostInfrastructureOptions>(request.Infrastructure,
                    nameof(request.Infrastructure))).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            mcp = new McpServerHost(services.CreateMcpServerHostDependencies(), report);
            ICitationDesktopBridge bridge = request.CitationBridgeFactory is { } bridgeFactory
                ? CastRequestField<ICitationDesktopBridge>(bridgeFactory(services),
                      $"{nameof(request.CitationBridgeFactory)} result")
                  ?? throw new ArgumentNullException(nameof(request.CitationBridgeFactory),
                      "The citation bridge factory returned null.")
                : new UnavailableCitationDesktopBridge();
            citation = new CitationInteropHost(services, bridge, report);

            LibraryImportOrchestrator? orchestrator = null;
            if (request.CreateWatcherAndOrchestrator)
            {
                watcher = new FileSearchRootWatcherService(services, request.WatcherLogger!);
                orchestrator = new LibraryImportOrchestrator(services,
                    CastRequestField<IImportConflictPrompt>(request.ImportConflictPrompt,
                        nameof(request.ImportConflictPrompt)),
                    CastRequestField<IBiblatexImportPrompt>(request.BiblatexImportPrompt,
                        nameof(request.BiblatexImportPrompt)));
            }

            return new LibrarySession(services, lease, mcp, citation, watcher, orchestrator,
                services.LibraryRevisionMonitor, report);
        }
        catch
        {
            // A failed or cancelled open must not leak the lease or half-started services:
            // the same teardown routine as disposal runs before the exception propagates.
            await TeardownComponentsAsync("open-failed", report, mcp, watcher,
                services?.LibraryRevisionMonitor, services, citation, lease).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> StartMcpFromPersistedSettingsAsync(bool allowEphemeralFallback,
        CancellationToken cancellationToken = default)
    {
        await Mcp.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!Mcp.IsRunning && allowEphemeralFallback)
        {
            Result<McpServerSettings> configured =
                await Services.McpSettings.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
            if (configured.IsSuccess)
            {
                McpServerSettings fallbackSettings = configured.Value with
                {
                    Port = RuntimeHostCoordinator.ReserveEphemeralLoopbackPort()
                };
                await Mcp.StartAsync(fallbackSettings, cancellationToken,
                    configured.Value).ConfigureAwait(false);
            }
        }

        return Mcp.IsRunning;
    }

    /// <inheritdoc />
    public async Task PublishAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisposed || _lease is not { } lease || !Mcp.IsRunning)
        {
            return;
        }

        Result<LibraryMetadata> library =
            await Services.Library.GetCurrentLibraryAsync(cancellationToken).ConfigureAwait(false);
        if (!library.IsSuccess || IsDisposed)
        {
            return;
        }

        // Set health endpoint identity BEFORE publishing the discovery record so /health
        // answers with the instance/library binding immediately.
        if (Mcp.Server is not null)
        {
            Mcp.Server.HostInstanceId = lease.InstanceId;
            Mcp.Server.HostLibraryId = library.Value.LibraryId.ToString();
        }

        await lease.PublishAsync(library.Value.LibraryId.ToString(), Mcp.Endpoint, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until a desktop host requests takeover of this Library (headless sessions only;
    /// requires the headless runtime-host lease). Disposing the session cancels the wait
    /// through the caller's token.
    /// </summary>
    public Task WaitForTakeoverRequestAsync(CancellationToken cancellationToken = default)
    {
        if (_lease is null)
        {
            throw new InvalidOperationException(
                "Only a session with a headless runtime-host lease accepts desktop takeover requests.");
        }

        return _lease.WaitForTakeoverRequestAsync(cancellationToken);
    }

    private bool IsDisposed => _disposeTask is not null;

    /// <summary>
    /// Stops admission, drains and disposes every owned resource in a fixed order, then
    /// releases the lease. Idempotent: concurrent and repeated calls observe the same
    /// disposal task. Every step is failure-isolated and the lease is released last, so no
    /// step failure can skip the release; collected failures are reported afterwards
    /// through the same unexpected-exception path as the rest of the session.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        // Stop reacting to MCP status first: a restart mid-teardown must not re-stamp the
        // health identity while the listener is being torn down.
        Mcp.StatusChanged -= OnOwnMcpStatusChanged;
        await TeardownComponentsAsync("dispose", _reportUnexpected, Mcp, Watcher, _monitor,
            Services, CitationInterop, _lease).ConfigureAwait(false);
    }

    /// <summary>
    /// The single teardown routine shared by <see cref="DisposeAsync"/> and a failed or
    /// cancelled <see cref="OpenAsync"/>. Steps run in a fixed order; each is failure-isolated
    /// into <paramref name="failures"/> and the lease is always released last.
    /// </summary>
    private static async Task TeardownComponentsAsync(string reason,
        Action<Exception, string, string?> report, McpServerHost? mcp,
        FileSearchRootWatcherService? watcher, LibraryRevisionMonitor? monitor,
        HostServices? services, CitationInteropHost? citation, RuntimeHostLease? lease)
    {
        List<Exception> failures = [];
        if (mcp is not null)
        {
            await RunStep(failures, "stop-mcp",
                    () => mcp.StopAsync("Library session is shutting down.", CancellationToken.None))
                .ConfigureAwait(false);
        }

        if (citation is not null)
        {
            // Stop accepting interop requests (and cancel in-flight picks) before services shut
            // down, so no request can reach a half-closed Library. Awaited until fully stopped.
            await RunStep(failures, "stop-citation-interop", () => citation.ShutdownAsync())
                .ConfigureAwait(false);
        }

        if (watcher is not null)
        {
            await RunStep(failures, "stop-watchers", () => watcher.StopAsync()).ConfigureAwait(false);
        }

        if (monitor is not null)
        {
            await RunStep(failures, "stop-revision-monitor", () =>
            {
                monitor.Stop();
                monitor.Dispose();
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }

        if (services is not null)
        {
            // Issue #46: shutdown does blocking drain work; keep the caller unblocked.
            await RunStep(failures, "shutdown-services", () => Task.Run(services.ShutdownAsync))
                .ConfigureAwait(false);
        }

        if (citation is not null)
        {
            // Awaited (not fire-and-forget) so switch/close teardown completes deterministically.
            await RunStep(failures, "dispose-citation-interop",
                () => citation.DisposeAsync().AsTask()).ConfigureAwait(false);
        }

        if (mcp is not null)
        {
            await RunStep(failures, "dispose-mcp-host", () => mcp.DisposeAsync().AsTask())
                .ConfigureAwait(false);
        }

        if (watcher is not null)
        {
            await RunStep(failures, "dispose-root-watcher", () => watcher.DisposeAsync().AsTask())
                .ConfigureAwait(false);
        }

        // The lease is released last and never skipped: a failed step above must not leave
        // the Library owned by a dead session.
        if (lease is not null)
        {
            await RunStep(failures, "release-lease", () => lease.DisposeAsync().AsTask())
                .ConfigureAwait(false);
        }

        foreach (Exception failure in failures)
        {
            report(failure, "library-session", $"teardown/{reason}");
        }
    }

    private static async Task RunStep(List<Exception> failures, string step, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private void OnOwnMcpStatusChanged(object? sender, McpServerHostStatusChangedEventArgs change)
    {
        // Re-stamp the instance id after every (re)start; the library id is stamped by
        // PublishAsync, which owns the library lookup.
        if (change.Status == McpServerHostStatus.Running && _lease is not null && Mcp.Server is not null)
        {
            Mcp.Server.HostInstanceId = _lease.InstanceId;
        }
    }

    private static IProgress<T>? WrapReport<T>(IProgress<object>? progress)
    {
        return progress is null ? null : new ObjectReportAdapter<T>(progress);
    }

    /// <summary>
    /// Casts one object-typed <see cref="LibrarySessionOpenRequest"/> slot to its Host-side
    /// contract type; a slot of the wrong type throws an <see cref="ArgumentException"/> that
    /// names the field (never a bare <see cref="InvalidCastException"/> or NRE downstream).
    /// </summary>
    private static T? CastRequestField<T>(object? value, string fieldName)
        where T : class
    {
        if (value is null)
        {
            return null;
        }

        if (value is not T typed)
        {
            throw new ArgumentException(
                $"LibrarySessionOpenRequest.{fieldName} must be a {typeof(T).Name}; " +
                $"got {value.GetType().Name}.", fieldName);
        }

        return typed;
    }

    private sealed class ObjectReportAdapter<T>(IProgress<object> inner) : IProgress<T>
    {
        public void Report(T value)
        {
            inner.Report(value!);
        }
    }
}
