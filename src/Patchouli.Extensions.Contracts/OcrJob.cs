namespace Patchouli.Extensions.Contracts;

using Core.Ids;
using Core.Layout;
using Core.Documents;
using Core.Results;

/// <summary>
/// Run-pinned job options for one OCR task. These values are frozen by the host when the job is
/// accepted; extensions read them but the host never mutates them while the job lives.
/// </summary>
public sealed record OcrJobOptions(
    /// <summary>The resolution the host renders page inputs at; null for non-rendered image inputs.</summary>
    int? Dpi = null,
    /// <summary>The host's bound for SDK-managed parallel work within this job.</summary>
    int Concurrency = 1);

/// <summary>
/// Immutable facts about one OCR job. <paramref name="JobId"/> identifies this attempt; retries of a
/// logical task get a fresh job and a fresh id. <paramref name="DocumentId"/> is the document
/// instance every issued page belongs to. <paramref name="Config"/> is the job-pinned configuration
/// snapshot resolved by the host; it is never the extension package's live mutable configuration.
/// </summary>
public sealed record OcrJobInfo(
    string JobId,
    DocumentInstanceId DocumentId,
    OcrJobOptions Options,
    ExtensionConfigSnapshot Config);

/// <summary>
/// One lazily readable image input on a physical page. <paramref name="Region"/> is the normalized
/// page-coordinate region the image covers; candidate geometry is expressed in the same normalized
/// page coordinates and must stay inside this region. Image bytes are deliberately not embedded:
/// the extension materializes them on demand through <see cref="IOcrJob.ReadInputImageAsync"/>.
/// </summary>
public sealed record OcrJobInput(
    OcrInputId Id,
    NormalizedBBox Region,
    string ContentType,
    string Kind = "page_image");

/// <summary>
/// One issued unit of work: a physical page with the one or more image inputs the host prepared
/// for it. A page is the settlement unit — the host settles each issued page exactly once, and
/// candidates for one page are submitted together in a single
/// <see cref="IOcrJob.SubmitCandidatesAsync"/> call. <paramref name="Inputs"/> is the issued
/// descriptor collection; the host hands it out as a read-only view, so an extension cannot
/// swap descriptors through collection casts and must use the issued instances as-is.
/// </summary>
public sealed record OcrJobPage(
    ExtensionPageIdentity Page,
    IReadOnlyList<OcrJobInput> Inputs);

/// <summary>
/// Materialized image bytes for one issued input. Disposing releases the host lease on the
/// underlying buffer deterministically and drops this object's reference to the bytes; reading
/// <see cref="Data"/> after disposal throws <see cref="ObjectDisposedException"/>, so ownership
/// and disposed state are unambiguous. The image must not be retained across page submissions.
/// </summary>
public sealed class OcrInputImage : IDisposable
{
    private ReadOnlyMemory<byte> _data;
    private Action? _release;
    private int _disposed;

    public OcrInputImage(OcrInputId id, string contentType, ReadOnlyMemory<byte> data, Action? release = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        if (data.IsEmpty)
        {
            throw new ArgumentException("OCR input images require non-empty image data.", nameof(data));
        }

        Id = id;
        ContentType = contentType;
        _data = data;
        _release = release;
    }

    public OcrInputId Id { get; }
    public string ContentType { get; }

    /// <summary>True once <see cref="Dispose"/> has run; <see cref="Data"/> is then unreadable.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>The image bytes; valid only while the lease is undisposed.</summary>
    /// <exception cref="ObjectDisposedException">The lease was already disposed.</exception>
    public ReadOnlyMemory<byte> Data
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _data;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _data = default;
            Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }
}

/// <summary>The declared outcome for one issued <see cref="OcrJobInput"/> inside a submission.</summary>
public enum OcrInputSettlementState
{
    /// <summary>
    /// The input was processed by the engine, including the case where it produced no boxes.
    /// A recognized input carries no error and is the only state boxes may attach to.
    /// </summary>
    Recognized,

    /// <summary>The input could not be processed; it must carry a non-empty error code.</summary>
    Failed,

    /// <summary>The engine deliberately did not process this input; it must carry no error.</summary>
    Skipped
}

/// <summary>
/// One issued input's declared outcome inside an <see cref="OcrExtensionCandidate"/>. A submission
/// must account for every input the page was issued with exactly once — this is what keeps a
/// partially recognized page from being reported as fully succeeded or silently adopted.
/// </summary>
public sealed record OcrInputSettlement(
    OcrInputId InputId,
    OcrInputSettlementState State,
    string? ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>The host's decision for one candidate submission or explicit page failure.</summary>
public enum OcrSubmissionState
{
    /// <summary>
    /// Candidates passed host validation and were recorded for the page; the page is settled.
    /// This records a validated candidate set — it is not a claim of full recognition success;
    /// consumers inspect <see cref="OcrSubmissionReceipt.FailedInputCount"/> and the submitted
    /// <see cref="OcrExtensionCandidate.Inputs"/> for partial outcomes.
    /// </summary>
    Accepted,

    /// <summary>
    /// The submission or failure record was refused (wrong identity, invalid geometry, foreign or
    /// dangling box references, or a page that was already settled). The page is not settled; the
    /// extension may submit a corrected set for the same issued page.
    /// </summary>
    Rejected,

    /// <summary>
    /// The page's source revision moved since the host issued it. Nothing was recorded; a host that
    /// can reissue the page returns it in <see cref="OcrSubmissionReceipt.RetryPage"/> so the
    /// extension may recognize it again and resubmit.
    /// </summary>
    Conflict,

    /// <summary>Recorded through <see cref="IOcrJob.FailPageAsync"/>: the page is settled as failed.</summary>
    Failed
}

/// <summary>
/// The result of one <see cref="IOcrJob.SubmitCandidatesAsync"/> or <see cref="IOcrJob.FailPageAsync"/>
/// call. A <see cref="OcrSubmissionState.Conflict"/> receipt carries the reissued page when the host
/// can provide one; without <see cref="RetryPage"/> the conflict is terminal for this job.
/// <see cref="FailedInputCount"/> reports how many of the page's issued inputs the accepted
/// submission declared <see cref="OcrInputSettlementState.Failed"/> or
/// <see cref="OcrInputSettlementState.Skipped"/>, so a partially recognized page is never
/// represented as a full success.
/// </summary>
public sealed record OcrSubmissionReceipt(
    OcrSubmissionState State,
    int AcceptedBoxCount = 0,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    OcrJobPage? RetryPage = null,
    int FailedInputCount = 0)
{
    public static OcrSubmissionReceipt Accepted(int boxCount, int failedInputCount = 0)
    {
        return new OcrSubmissionReceipt(OcrSubmissionState.Accepted, boxCount,
            FailedInputCount: failedInputCount);
    }

    public static OcrSubmissionReceipt Rejected(string errorCode, string errorMessage)
    {
        return new OcrSubmissionReceipt(OcrSubmissionState.Rejected, 0, errorCode, errorMessage);
    }
}

/// <summary>
/// The terminal outcome the host records for each requested page, whether the extension settled it
/// or not. Issued pages that are still unsettled when the job ends settle as
/// <see cref="Failed"/> (or <see cref="Cancelled"/> when the job is cancelled); pages the extension
/// never took settle as <see cref="Cancelled"/>.
/// </summary>
public enum OcrPageSettlementState
{
    /// <summary>The extension submitted candidates the host accepted and recorded.</summary>
    Accepted,

    /// <summary>
    /// The extension settled the page through <see cref="IOcrJob.FailPageAsync"/>, or left it
    /// unsettled when the job ended without cancellation.
    /// </summary>
    Failed,

    /// <summary>The page was never taken, or was still unsettled when the job was cancelled.</summary>
    Cancelled
}

/// <summary>
/// The task-level host interface for one OCR job. The extension owns input order, cross-input and
/// cross-page batching, concurrency and request-level retry policy; the host owns page/input
/// identity, frozen baselines, lazy bounded image access, per-page exactly-once candidate
/// settlement and cancellation. A submitted candidate is staged for validation and adoption —
/// extensions never write canonical document text directly.
/// </summary>
public interface IOcrJob
{
    /// <summary>Run-pinned job facts, including the frozen configuration snapshot.</summary>
    OcrJobInfo Info { get; }

    /// <summary>Optional progress sink; page-scoped events set <see cref="ExtensionProgress.ItemId"/> to the page index.</summary>
    IProgress<ExtensionProgress>? Progress { get; }

    /// <summary>
    /// Returns the next page still requiring recognition, in request order, or null when none
    /// remains. The host serializes the call, so concurrent callers observe the same order.
    /// Every returned page must eventually be settled with exactly one
    /// <see cref="SubmitCandidatesAsync"/> or <see cref="FailPageAsync"/> call; a page left
    /// unsettled when the job ends settles as failed, or cancelled when the job was cancelled.
    /// </summary>
    ValueTask<OcrJobPage?> NextPageAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Materializes the image bytes of one input this job issued. Inputs are read on demand and a
    /// per-job in-flight budget applies, so a job covering many pages never holds the whole
    /// document's images at once; a call that would exceed the budget waits — cancellation-aware —
    /// until another input's lease is disposed. The input must be the same descriptor instance the
    /// job issued for a page that has not yet been settled. The returned lease must be disposed
    /// when the input is done being consumed; the host also releases any lease still outstanding
    /// when the job ends, deterministically. A read still in flight when the job ends is not
    /// abandoned either: the job cancels a job-owned read lifetime linked into the call's token
    /// and the invocation — including its generation lease — stays alive until the read actually
    /// exits. A loader that ignores that cancellation still runs to completion under the pinned
    /// lease; an image it returns after closure is disposed immediately.
    /// </summary>
    /// <exception cref="ArgumentException">The input is not the descriptor this job issued.</exception>
    /// <exception cref="InvalidOperationException">
    /// The job is no longer running, or the input's page is already settled.
    /// </exception>
    ValueTask<OcrInputImage> ReadInputImageAsync(OcrJobInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and records the candidate set for one issued, unsettled page. The submission's
    /// <see cref="OcrExtensionCandidate.Inputs"/> must account for every input the page was issued
    /// with, each exactly once — a successful input that produced no boxes is
    /// <see cref="OcrInputSettlementState.Recognized"/>, not omitted — and boxes may only attach to
    /// <see cref="OcrInputSettlementState.Recognized"/> inputs. Candidate geometry is expressed in
    /// normalized page coordinates and must stay inside the referenced input's region. Non-null
    /// seed <see cref="DocumentBoxSeed.BoxId"/> values are job-allocated identities: they must be
    /// unique across the job, a <see cref="DocumentBoxSeed.ParentBoxId"/> must resolve inside the
    /// same submission, and a <see cref="DocumentBoxSeed.ContinuesFromBoxId"/> may resolve either
    /// inside the same submission or to a box id accepted earlier in this job. That second case is
    /// how cross-page continuations are expressed, and it is settlement-order sensitive: a page
    /// whose continuation targets a not-yet-accepted box is rejected, so engines that settle pages
    /// concurrently cannot emit forward continuation references. An accepted submission settles
    /// the page exactly once; a duplicate is rejected, never merged.
    /// </summary>
    ValueTask<OcrSubmissionReceipt> SubmitCandidatesAsync(OcrExtensionCandidate candidate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Settles one issued, unsettled page as failed without submitting candidates. Use it when an
    /// input cannot be recognized at all so the host records an explicit failure instead of an
    /// ambiguous empty candidate set.
    /// </summary>
    ValueTask<OcrSubmissionReceipt> FailPageAsync(ExtensionPageIdentity page, string errorCode,
        string? errorMessage = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// One OCR engine. There is exactly one entry point: the extension receives the whole task as an
/// <see cref="IOcrJob"/> and owns input order, batching, concurrency and retry policy. The host
/// supplies pages, lazily materialized inputs, and validates each per-page candidate settlement.
/// There is no per-page recognition method and no optional second interface.
/// </summary>
public interface IOcrExtension : IExtensionDefinition
{
    /// <summary>
    /// Declarative engine metadata used by the host's engine catalog and preset routing. Extensions
    /// compiled against older contracts expose <see cref="OcrEngineDefinition.Unknown"/>; the host
    /// resolves the registered definition for the pinned package when one exists.
    /// </summary>
    OcrEngineDefinition EngineDefinition => OcrEngineDefinition.Unknown;

    /// <summary>
    /// Non-executing readiness probe against the run-pinned configuration snapshot: model files,
    /// credentials, endpoints, and support resources. It must not start a model session, download
    /// weights, or render anything; recognition resources may stay lazy until
    /// <see cref="RunAsync"/> actually consumes them.
    /// </summary>
    Task<Result> CheckReadinessAsync(ExtensionConfigSnapshot config, IExtensionResources resources,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result.Success());
    }

    Task RunAsync(IOcrJob job, IExtensionResources resources,
        CancellationToken cancellationToken = default);
}
