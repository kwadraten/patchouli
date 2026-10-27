using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;

namespace Patchouli.Ocr;

/// <summary>
/// OCR run execution scopes carried by <see cref="OcrRunExecutionRequest"/>. These describe the
/// shape of one accepted execution (which inputs it uses), not the persisted queue task kind.
/// </summary>
public static class OcrRunExecutionScope
{
    public const string Document = "document";
    public const string Pages = "pages";
    public const string Region = "region";
}

/// <summary>
/// Frozen per-execution OCR request. The accepted page set, the optional region input, and the
/// pinned <see cref="OcrPresetVersionId"/> (when present) are fixed at queue-accept time so a
/// preset that changes while the task waits cannot alter what the attempt executes. The page set
/// is copied into a read-only snapshot at construction because callers own the original list and
/// may mutate it while awaits are in flight. One request shape covers every OCR branch so it can
/// later carry the unified <c>IOcrJob</c> contract.
/// </summary>
public sealed record OcrRunExecutionRequest
{
    public OcrRunExecutionRequest(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        IReadOnlyList<PageId> pageIds,
        string scope,
        OcrPresetVersionId? presetVersionId = null,
        NormalizedBBox? regionBBox = null,
        bool workingDraft = false,
        IOcrEngineLease? engineLease = null)
    {
        ArgumentNullException.ThrowIfNull(pageIds);
        DocumentInstanceId = documentInstanceId;
        PresetId = presetId;
        PageIds = Array.AsReadOnly(pageIds.ToArray());
        Scope = scope;
        PresetVersionId = presetVersionId;
        RegionBBox = regionBBox;
        WorkingDraft = workingDraft;
        EngineLease = engineLease;
    }

    public DocumentInstanceId DocumentInstanceId { get; }
    public OcrPresetId PresetId { get; }
    public IReadOnlyList<PageId> PageIds { get; }
    public string Scope { get; }
    public OcrPresetVersionId? PresetVersionId { get; }
    public NormalizedBBox? RegionBBox { get; }

    /// <summary>
    /// When set, the run's results stay working revisions regardless of preset auto-apply — the
    /// page-scope draft consumption path (edit-mode entries), distinct from commit-on-completion.
    /// </summary>
    public bool WorkingDraft { get; }

    /// <summary>
    /// The generation pin acquired at queue acceptance for this task. Extension-backed engines
    /// execute through it; built-in engines (MinerU) ignore it. When present it is always the
    /// acceptance-time selection — the engine never resolves a newer generation inside the run.
    /// </summary>
    public IOcrEngineLease? EngineLease { get; }
}

/// <summary>
/// Typed context for exactly one queue attempt: <see cref="TaskId"/> is the public status identity
/// and <see cref="Attempt"/> is the private invocation generation (the task's attempt count at claim
/// time). The engine reports a freshly created <see cref="OcrRunId"/> through
/// <see cref="ReportRunCreated"/> immediately after the run row exists and before any model
/// execution or page progress, so the run is bound to its actual task rather than inferred by
/// document identity. Repeated identical bindings are idempotent; a conflicting or superseded
/// attempt binding fails so the engine can terminalize the orphaned run deterministically.
/// </summary>
public sealed class OcrRunExecutionContext
{
    private readonly Func<OcrRunId, OcrPresetVersionId?, Result> _runCreated;

    public OcrRunExecutionContext(OcrQueueTaskId taskId, int attempt, Func<OcrRunId, Result> runCreated)
        : this(taskId, attempt, Adapt(runCreated))
    {
    }

    public OcrRunExecutionContext(OcrQueueTaskId taskId, int attempt,
        Func<OcrRunId, OcrPresetVersionId?, Result> runCreated)
    {
        ArgumentNullException.ThrowIfNull(runCreated);
        TaskId = taskId;
        Attempt = attempt;
        _runCreated = runCreated;
    }

    /// <summary>Public queue/status identity; never used to infer run ownership by document.</summary>
    public OcrQueueTaskId TaskId { get; }

    /// <summary>Invocation generation: callbacks stamped with any other attempt are stale.</summary>
    public int Attempt { get; }

    /// <summary>
    /// Binds a just-created persistent run to this invocation. Success means the task now owns the
    /// run identity; failure means the attempt is superseded or the run conflicts with an already
    /// bound run, in which case the engine must not proceed with model work.
    /// </summary>
    public Result ReportRunCreated(OcrRunId runId, OcrPresetVersionId? presetVersionId = null)
    {
        return _runCreated(runId, presetVersionId);
    }

    private static Func<OcrRunId, OcrPresetVersionId?, Result> Adapt(Func<OcrRunId, Result> runCreated)
    {
        ArgumentNullException.ThrowIfNull(runCreated);
        return (runId, _) => runCreated(runId);
    }
}
