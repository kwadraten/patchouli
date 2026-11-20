using Patchouli.Core.Diagnostics;

namespace Patchouli.Core.Library;

/// <summary>Who owns a Library while a session holds it open.</summary>
public enum LibrarySessionOwnership
{
    /// <summary>No runtime-host lease: single-process ownership only.</summary>
    None,

    /// <summary>A desktop process owns the Library and publishes it for local clients.</summary>
    Desktop,

    /// <summary>A headless process owns the Library until a desktop host takes over.</summary>
    Headless
}

/// <summary>
/// Everything the Host composition needs to open one Library session. The Host-specific
/// slots stay <see cref="object"/>-typed so this contract can live in Core without a Host
/// reference; the Host implementation validates and casts each slot exactly once and a
/// mismatched slot throws an <see cref="ArgumentException"/> naming the field.
/// </summary>
public sealed record LibrarySessionOpenRequest(
    string DatabasePath,
    LibrarySessionOwnership Ownership,
    object? Settings = null,
    string? SettingsPath = null,
    IProgress<object>? MigrationProgress = null,
    IProgress<object>? StartupProgress = null,
    Action<Exception, string, string?>? ReportUnexpected = null,
    IHostActivityTracker? ActivityTracker = null,
    IAppLogger? WatcherLogger = null,
    object? OcrComposition = null,
    object? AppPaths = null,
    object? Infrastructure = null,
    Func<object, object>? CitationBridgeFactory = null,
    object? ImportConflictPrompt = null,
    object? BiblatexImportPrompt = null,
    bool CreateWatcherAndOrchestrator = false,
    string? LeaseDirectory = null);

/// <summary>
/// The Host-owned session contract for one open Library (issue #84, ADR 0058 resource
/// ownership): the composition root and every per-Library host component, surfaced with the
/// Host's own types through closed generic arguments so business UI consumes them without
/// casts. The generic arguments are declared covariant because every member is read-only;
/// the Host closes them with its concrete component types.
/// </summary>
public interface ILibrarySession<out TServices, out TMcpHost, out TCitationInteropHost,
    out TFileSearchRootWatcher, out TImportOrchestrator> : IAsyncDisposable
    where TServices : class
    where TMcpHost : class
    where TCitationInteropHost : class
    where TFileSearchRootWatcher : class
    where TImportOrchestrator : class
{
    /// <summary>The opened composition root.</summary>
    TServices Services { get; }

    /// <summary>The per-Library MCP server host (start is explicit, never implicit).</summary>
    TMcpHost McpHost { get; }

    /// <summary>The per-Library citation interop host.</summary>
    TCitationInteropHost CitationInteropHost { get; }

    /// <summary>The file-search-root watcher fleet, when the session creates one (desktop).</summary>
    TFileSearchRootWatcher? FileSearchRootWatcher { get; }

    /// <summary>The import orchestrator, when the session creates one (desktop).</summary>
    TImportOrchestrator? ImportOrchestrator { get; }

    /// <summary>Live MCP admission counts for status display.</summary>
    (long Active, long Total) McpConnectionCounts { get; }

    /// <summary>
    /// Starts the MCP listener from persisted settings; when it is still not running and
    /// <paramref name="allowEphemeralFallback"/> is set, retries once on an ephemeral
    /// loopback port while keeping the persisted settings applied.
    /// </summary>
    Task<bool> StartMcpFromPersistedSettingsAsync(bool allowEphemeralFallback,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stamps the health-endpoint identity (instance/library) and publishes the
    /// runtime-host discovery record. No-ops when the session is disposed, holds no lease,
    /// or the listener is not running.
    /// </summary>
    Task PublishAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Opening a Library whose runtime-host lease is already held by another live process
/// (headless ownership probe). Desktop acquisition instead waits and throws
/// <c>RuntimeHostUnavailableException</c> from the Host layer.
/// </summary>
public sealed class LibrarySessionAlreadyOwnedException : InvalidOperationException
{
    public LibrarySessionAlreadyOwnedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Composition seam (issue #84): the concrete Library session opener is registered by the
/// Host layer as soon as its assembly loads (Host module initializer), so business UI can
/// open Libraries through this Core seam without referencing the Host implementation.
/// The delegate is intentionally object-typed: Core cannot name the Host's concrete session
/// or its closed <see cref="ILibrarySession{TServices,TMcpHost,TCitationInteropHost,TFileSearchRootWatcher,TImportOrchestrator}"/>.
/// </summary>
public static class LibrarySessionRuntime
{
    private static Func<LibrarySessionOpenRequest, CancellationToken, Task<object>>? _opener;

    /// <summary>
    /// Registers the Host composition's session opener. Re-registering the same delegate is
    /// a no-op (idempotent module initialization); registering a different one throws,
    /// because exactly one Host composition may own Library sessions per process.
    /// </summary>
    public static void Register(Func<LibrarySessionOpenRequest, CancellationToken, Task<object>> opener)
    {
        ArgumentNullException.ThrowIfNull(opener);
        Func<LibrarySessionOpenRequest, CancellationToken, Task<object>>? existing =
            Interlocked.CompareExchange(ref _opener, opener, null);
        if (existing is not null && !ReferenceEquals(existing, opener))
        {
            throw new InvalidOperationException(
                "A different LibrarySession opener is already registered; only one Host " +
                "composition may own Library sessions in a process.");
        }
    }

    /// <summary>Opens a Library session through the registered Host composition.</summary>
    public static Task<object> Open(LibrarySessionOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        Func<LibrarySessionOpenRequest, CancellationToken, Task<object>>? opener = Volatile.Read(ref _opener);
        if (opener is null)
        {
            throw new InvalidOperationException(
                "No LibrarySession opener is registered: the Patchouli.Host assembly has not " +
                "been loaded, so no Host composition is available to own a Library session.");
        }

        return opener(request, cancellationToken);
    }
}
