using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Ocr;

/// <summary>
/// One host-built OCR job: the frozen input selection plus the lazy image loader the job serves to
/// the extension. <paramref name="MaxInFlightImages"/> bounds how many input images may be
/// materialized and undisposed at once. <paramref name="BaselineProbe"/> is the host-mediated
/// settlement sink: it returns the page's newer committed identity when the frozen baseline moved
/// since issue, which produces a terminal <see cref="OcrSubmissionState.Conflict"/> receipt — the
/// host never rebases an image result onto a newer edit, so no page is reissued.
/// </summary>
public sealed record OcrDomainJobRequest(
    string JobId,
    DocumentInstanceId DocumentId,
    IReadOnlyList<OcrJobPage> Pages,
    Func<OcrJobInput, CancellationToken, ValueTask<OcrInputImage>> ReadImage,
    OcrJobOptions Options,
    IProgress<ExtensionProgress>? Progress = null,
    int MaxInFlightImages = 4,
    Func<ExtensionPageIdentity, CancellationToken, ValueTask<ExtensionPageIdentity?>>? BaselineProbe = null);

/// <summary>Stable settlement error codes produced by the host job layer.</summary>
public static class OcrSettlementCodes
{
    /// <summary>
    /// The page's committed source revision moved while the job ran; the result is a terminal
    /// conflict — the host never rebases an image result onto a newer manual edit.
    /// </summary>
    public const string SourceRevisionConflict = "source_revision_conflict";
}

/// <summary>The terminal outcome the host recorded for one requested page.</summary>
public sealed record OcrPageSettlement(
    ExtensionPageIdentity Page,
    OcrPageSettlementState State,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    int AcceptedBoxCount = 0,
    int FailedInputCount = 0);

/// <summary>Accepted per-page candidate sets (in settlement order) and every page's outcome.</summary>
public sealed record OcrDomainRunResult(
    IReadOnlyList<OcrExtensionCandidate> AcceptedCandidates,
    IReadOnlyList<OcrPageSettlement> Settlements);

/// <summary>
/// Read-only engine metadata surface for presets/UI/listing. Implementations must not create
/// engine sessions or activate extension packages: every member is declarative metadata.
/// </summary>
public interface IOcrEngineCatalog
{
    IReadOnlyList<OcrEngineDefinition> ListDefinitions();

    /// <summary>The registered package identity, independent of the product engine id.</summary>
    string? GetExtensionId(string engineId)
    {
        return null;
    }

    /// <summary>The registered package identity for an independent PDF text provider.</summary>
    string? GetTextLayerExtensionId(string providerId)
    {
        return null;
    }

    /// <summary>
    /// Declarative PDF text-layer provider metadata for preset/UI pickers — schema fields and
    /// defaults arrive on each <see cref="PdfTextLayerDefinition.DefaultConfig"/>; listing never
    /// activates a package or opens a session. Empty when no provider catalog is wired.
    /// </summary>
    IReadOnlyList<PdfTextLayerDefinition> ListTextLayerDefinitions()
    {
        return [];
    }

    Task<Result<OcrEngineDefinition>> GetDefinitionAsync(string engineId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Host policy for pages where the pinned text-layer provider cannot produce usable native
/// output — a reported <c>native_text_missing</c>, an empty result, unusable or damaged text,
/// an unreadable source, or an error-level diagnostic. The provider itself never synthesizes
/// a whole-page OCR request — the host turns its honest failure into a full-page image input
/// only under this explicit composition-level policy.
/// </summary>
public static class TextLayerMissingTextPolicies
{
    /// <summary>Default: the page is issued to the selected image OCR engine as a whole-page input.</summary>
    public const string PageOcr = "page-ocr";

    /// <summary>Strict: the page fails with its provider error; no image OCR is requested.</summary>
    public const string Fail = "fail";
}

/// <summary>
/// One accepted task's frozen engine selection: the preset version plus the already-resolved
/// extension package/config/text-provider choices. The runtime resolves effective configuration
/// and provider selection at acquisition time from <see cref="PresetVersion"/> when the optional
/// fields are absent; explicit fields are for tests and composed callers.
/// </summary>
public sealed record OcrEngineLeaseRequest(
    OcrPresetVersion PresetVersion,
    string? EngineExtensionId = null,
    ExtensionConfigSnapshot? EngineConfig = null,
    string? TextLayerProviderId = null,
    string? TextLayerExtensionId = null,
    ExtensionConfigSnapshot? TextLayerConfig = null,
    string? Scope = null,
    string? TextLayerMissingTextPolicy = null);

/// <summary>
/// The generation pin held for one accepted queue task: image engine + optional independent
/// text-layer provider, acquired at queue acceptance and retained while the task is queued,
/// running, and draining — including across auto-requeue retries of the same task. Provenance
/// members carry fingerprints/config versions/model identity, never weights or secrets.
/// </summary>
public interface IOcrEngineLease : IAsyncDisposable
{
    OcrEngineDefinition Definition { get; }
    string EngineId { get; }

    /// <summary>Actual frozen image-model provider, when the component declares a hosted model slot.</summary>
    string? ProviderId => null;

    /// <summary>True for built-in engines (MinerU) that run without an extension generation.</summary>
    bool IsBuiltIn { get; }

    string? EngineExtensionId { get; }
    string? EngineFingerprint { get; }
    int? EngineConfigurationVersion { get; }
    string? ModelIdentity { get; }
    string? ModelDigest { get; }
    ExtensionConfigSnapshot? EngineConfig { get; }

    string? TextLayerProviderId { get; }
    string? TextLayerExtensionId { get; }
    string? TextLayerFingerprint { get; }
    int? TextLayerConfigurationVersion { get; }
    ExtensionConfigSnapshot? TextLayerConfig { get; }

    /// <summary>
    /// The composition-level policy applied to content-level provider failures — a reported
    /// <c>native_text_missing</c>, an empty result, unusable or damaged text, an unreadable
    /// source, an error-level diagnostic: <see cref="TextLayerMissingTextPolicies.PageOcr"/>
    /// (default) routes the page to the image engine as a whole-page input;
    /// <see cref="TextLayerMissingTextPolicies.Fail"/> records an explicit page failure.
    /// Provider-side infrastructure failures (no pinned provider, a released generation)
    /// never fall back — the image engine cannot substitute for a provider that never ran.
    /// </summary>
    string TextLayerMissingTextPolicy => TextLayerMissingTextPolicies.PageOcr;

    Task<Result> CheckReadinessAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests only files needed by the actual input scope, after branch selection.</summary>
    Task<Result> EnsureManagedFilesAsync(string scope, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result.Success());
    }

    /// <summary>One task-level invocation over the accepted scope; never call per page.</summary>
    Task<Result<OcrDomainRunResult>> RunOcrJobAsync(OcrDomainJobRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the pinned independent text-layer provider for one page. Fails when no provider is
    /// pinned; a provider failure is returned, never silently replaced by another provider.
    /// </summary>
    Task<Result<PdfTextLayerResult>> ParseTextLayerAsync(DocumentInstanceId documentId,
        PageId pageId, int pageIndex, DocumentTreeRevisionId revisionId, NormalizedBBox? region,
        CancellationToken cancellationToken = default);

    Task<Result<PdfTextLayerResult>> ParseTextLayerAsync(DocumentInstanceId documentId,
        PageId pageId, int pageIndex, DocumentTreeRevisionId revisionId, NormalizedBBox? region,
        string scope, CancellationToken cancellationToken = default)
    {
        return ParseTextLayerAsync(documentId, pageId, pageIndex, revisionId, region, cancellationToken);
    }

    /// <summary>
    /// A built-in engine's region recognizer for explicit provider-declared
    /// <see cref="PdfTextRequiredRegion"/> inputs and for whole-page inputs the host declared
    /// under the missing-text policy (e.g. MinerU + a text-layer provider). Only invoked for
    /// inputs explicitly forwarded to image OCR under the same accepted task — never an
    /// undeclared fallback. Null on extension engines and on built-ins that cannot recognize
    /// such inputs; callers must fail those inputs honestly instead of silently substituting
    /// another engine.
    /// </summary>
    Func<OcrJobPage, OcrJobInput, OcrPresetVersion, CancellationToken,
        Task<Result<IReadOnlyList<OcrExtensionBoxCandidate>>>>? BuiltInRegionRunner => null;
}

/// <summary>
/// Engine catalog + generation acquisition. Acquisitions are the only session-adjacent operation
/// and must still keep native model sessions lazy: an acquired lease means a pinned compiled
/// generation and a successful non-executing readiness probe, not a live recognizer.
/// </summary>
public interface IOcrEngineRuntime : IOcrEngineCatalog
{
    Task<Result<IOcrEngineLease>> AcquireAsync(OcrEngineLeaseRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Task-keyed engine lease store. The scheduler acquires the lease inside queue acceptance and
/// releases it exactly once: queued cancellation, terminal completion, task removal, explicit
/// retry of a completed attempt, or shutdown drain. A requeued attempt of the same task keeps
/// the pinned generation — retries re-run the same acceptance-time selection.
/// </summary>
public sealed class OcrEngineLeaseRegistry : IAsyncDisposable
{
    private readonly IOcrEngineRuntime? _runtime;
    private readonly Func<OcrPresetVersionId, CancellationToken, Task<Result<OcrPresetVersion>>>? _versions;
    private readonly object _gate = new();
    private readonly Dictionary<OcrQueueTaskId, IOcrEngineLease> _leases = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly TaskCompletionSource _operationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _operations;
    private bool _disposed;

    public OcrEngineLeaseRegistry(IOcrEngineRuntime? runtime,
        Func<OcrPresetVersionId, CancellationToken, Task<Result<OcrPresetVersion>>>? versionResolver = null)
    {
        _runtime = runtime;
        _versions = versionResolver;
        _shutdownToken = _shutdown.Token;
    }

    /// <summary>True when pinning is wired; unpinned installs degrade to legacy behavior.</summary>
    public bool SupportsPinning => _runtime is not null && _versions is not null;

    /// <summary>
    /// Pins the engine generation for one accepted task. Returns a null lease when the task is
    /// unpinned (mock/dev paths) or pinning is not configured. The lease is stored under the task
    /// id only on success; a failed acquisition leaves nothing to release.
    /// </summary>
    public async Task<Result<IOcrEngineLease?>> AcquireAsync(OcrQueueTask task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!EnterOperation())
        {
            return Result<IOcrEngineLease?>.Failure(AppErrorCodes.InvalidState,
                "The OCR engine lease registry is disposed.");
        }

        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _shutdownToken);
            CancellationToken token = linked.Token;
            token.ThrowIfCancellationRequested();
            if (_runtime is null || _versions is null || task.PresetVersionId is not { } versionId)
            {
                return Result<IOcrEngineLease?>.Success(null);
            }

            Result<OcrPresetVersion> version = await _versions(versionId, token).ConfigureAwait(false);
            if (version.IsFailure)
            {
                return Result<IOcrEngineLease?>.Failure(version.ErrorCode!, version.ErrorMessage!);
            }

            Result<IOcrEngineLease> lease = await _runtime.AcquireAsync(
                new OcrEngineLeaseRequest(version.Value, Scope: task.TaskKind), token).ConfigureAwait(false);
            if (lease.IsFailure)
            {
                return Result<IOcrEngineLease?>.Failure(lease.ErrorCode!, lease.ErrorMessage!);
            }

            IOcrEngineLease? previous = null;
            bool accepted;
            lock (_gate)
            {
                accepted = !_disposed && !token.IsCancellationRequested;
                if (accepted)
                {
                    _leases.Remove(task.TaskId, out previous);
                    _leases[task.TaskId] = lease.Value;
                }
            }

            if (!accepted)
            {
                await lease.Value.DisposeAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return Result<IOcrEngineLease?>.Failure(AppErrorCodes.InvalidState,
                    "The OCR engine lease registry is disposed.");
            }

            if (previous is not null)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            return Result<IOcrEngineLease?>.Success(lease.Value);
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>The pinned lease for a task, or null when unpinned/unknown/released/drained.</summary>
    public IOcrEngineLease? Get(OcrQueueTaskId taskId)
    {
        lock (_gate)
        {
            return _disposed || !_leases.TryGetValue(taskId, out IOcrEngineLease? lease)
                ? null
                : lease;
        }
    }

    /// <summary>Releases one task's pin if still held; safe to call for any task id.</summary>
    public async Task ReleaseAsync(OcrQueueTaskId taskId)
    {
        if (!EnterOperation())
        {
            return;
        }

        try
        {
            IOcrEngineLease? lease;
            lock (_gate)
            {
                _leases.Remove(taskId, out lease);
            }

            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>
    /// Shutdown drain: marks the registry disposed, releases every retained pin, and shares the
    /// completion through <see cref="Drained"/> so callers can await drain without a second
    /// dispose race. The gate is intentionally not disposed — late callbacks can still arrive.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        IOcrEngineLease[] held;
        lock (_gate)
        {
            if (_disposed)
            {
                return new ValueTask(_drained.Task);
            }

            _disposed = true;
            held = [.. _leases.Values];
            _leases.Clear();
            if (_operations == 0)
            {
                _operationsDrained.TrySetResult();
            }
        }

        _ = DrainAsync(held);
        return new ValueTask(_drained.Task);
    }

    private async Task DrainAsync(IOcrEngineLease[] held)
    {
        try
        {
            try
            {
                await _shutdown.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                // Reserve before detaching: shutdown also owns releases already removed from
                // the dictionary and acquisitions still inside cancellation-ignoring code.
                await Task.WhenAll(held.Select(async lease =>
                        await lease.DisposeAsync().ConfigureAwait(false))
                    .Append(_operationsDrained.Task)).ConfigureAwait(false);
            }

            _drained.TrySetResult();
        }
        catch (Exception exception)
        {
            _drained.TrySetException(exception);
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private bool EnterOperation()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _operations++;
            return true;
        }
    }

    private void ExitOperation()
    {
        lock (_gate)
        {
            if (--_operations == 0 && _disposed)
            {
                _operationsDrained.TrySetResult();
            }
        }
    }

    /// <summary>Completes once every retained pin has been released.</summary>
    public Task Drained => _drained.Task;

    private readonly TaskCompletionSource _drained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
