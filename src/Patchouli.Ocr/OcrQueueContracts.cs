using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;

namespace Patchouli.Ocr;

public static class OcrQueueTaskKind
{
    public const string Document = "document";
    public const string MockPages = "mock_pages";
    public const string ImagePage = "image_page";
    public const string RenderedPdfPage = "rendered_pdf_page";
    public const string Region = "region";
}

public static class OcrQueuePriority
{
    public const string InteractiveCurrentPage = "interactive_current_page";
    public const string InteractiveSelectedPages = "interactive_selected_pages";
    public const string UserStartedDocument = "user_started_document";
    public const string BackgroundRetry = "background_retry";
    public const string BatchCollection = "batch_collection";
    public const string Maintenance = "maintenance";
}

public static class OcrQueueTaskState
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Blocked = "blocked";
    public const string Paused = "paused";
}

public static class OcrPauseScope
{
    public const string Global = "global";
    public const string Local = "local";
    public const string Cloud = "cloud";
    public const string Provider = "provider";
    public const string Task = "task";
}

public static class OcrRetryClassification
{
    public const string TransientRetryable = "transient_retryable";
    public const string ManualRepairRequired = "manual_repair_required";
    public const string NonRetryable = "non_retryable";
}

public static class OcrQueueChangeKind
{
    public const string Enqueued = "enqueued";
    public const string Updated = "updated";
    public const string Started = "started";
    public const string Stopped = "stopped";
    public const string Progress = "progress";
}

public static class OcrTaskStage
{
    public const string Preparing = "preparing";
    public const string Recognizing = "recognizing";
    public const string Uploading = "uploading";
    public const string WaitingCloud = "waiting_cloud";
    public const string Downloading = "downloading";
    public const string Importing = "importing";
}

public sealed record OcrQueueTask(
    OcrQueueTaskId TaskId,
    LibraryId LibraryId,
    DocumentInstanceId DocumentInstanceId,
    OcrPresetId PresetId,
    IReadOnlyList<PageId> PageIds,
    string TaskKind,
    string EngineId,
    string AdapterKind,
    string? ProviderId,
    string Priority,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string State,
    int AttemptCount,
    int MaxAttempts,
    OcrQueueTaskId? RetryOfTaskId,
    string? LastErrorCode,
    string? LastErrorMessage,
    DateTimeOffset? ScheduledAfter,
    string? ImagePath,
    int? Dpi,
    OcrRunId? RunId = null,
    int CompletedPageCount = 0,
    int FailedPageCount = 0,
    NormalizedBBox? RegionBBox = null,
    bool CommitOnCompletion = true);

public sealed record OcrQueueTaskRequest(
    DocumentInstanceId DocumentInstanceId,
    OcrPresetId PresetId,
    IReadOnlyList<PageId> PageIds,
    string TaskKind,
    string EngineId,
    string AdapterKind,
    string? ProviderId,
    string Priority,
    string? ImagePath = null,
    int? Dpi = null,
    int MaxAttempts = 3,
    NormalizedBBox? RegionBBox = null,
    bool CommitOnCompletion = true);

public sealed record OcrQueueLimits(
    int GlobalMaxConcurrent,
    int LocalMaxConcurrent,
    int CloudMaxConcurrent,
    int PerProviderMaxConcurrent,
    int PerEngineMaxConcurrent)
{
    public static OcrQueueLimits Default => new(Math.Min(4, Math.Max(2, Environment.ProcessorCount / 4)), 1, 2, 1, 1);
}

public sealed record OcrQueueTaskFilter(
    string? State = null,
    string? EngineId = null,
    string? ProviderId = null,
    bool IncludeCompleted = true);

public sealed record OcrQueueStatus(
    bool IsRunning,
    int Queued,
    int Running,
    int Succeeded,
    int Failed,
    int Cancelled,
    int Blocked,
    IReadOnlyList<string> PausedScopes,
    OcrQueueLimits Limits,
    IReadOnlyDictionary<string, int> RunningByEngine,
    IReadOnlyDictionary<string, int> RunningByProvider);

public sealed record OcrQueueExecutionResult(
    bool Succeeded,
    bool Cancelled,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    OcrRunId? RunId = null,
    int CompletedPageCount = 0,
    int FailedPageCount = 0,
    string? ResultText = null);

public sealed record OcrQueueChangedEventArgs(OcrQueueTask? Task, string ChangeKind);

public sealed record OcrTaskProgressReport(
    OcrQueueTaskId TaskId,
    string Stage,
    double? Fraction,
    string? Detail);

public sealed record OcrTaskStageProgress(
    string Stage,
    double? Fraction,
    string? Detail);

public interface IOcrQueueTaskExecutor
{
    Task<OcrQueueExecutionResult> ExecuteAsync(OcrQueueTask task, CancellationToken cancellationToken,
        IProgress<OcrTaskProgressReport>? progress = null);
}

public interface IOcrRetryPolicy
{
    string Classify(string? errorCode);
    bool ShouldRetry(OcrQueueTask task, string? errorCode);
    TimeSpan GetNextDelay(int attemptCount);
}

public interface IOcrQueueScheduler
{
    event EventHandler<OcrQueueChangedEventArgs>? Changed;
    Task<Result<OcrQueueTask>> EnqueueAsync(OcrQueueTaskRequest request, CancellationToken c = default);

    Task<Result<OcrQueueTask>> EnqueueDocumentAsync(DocumentInstanceId d, OcrPresetId p, IReadOnlyList<PageId> pages,
        string engineId, string adapterKind, string? providerId, string priority, CancellationToken c = default);

    Task<Result<OcrQueueTask>> EnqueueMockPagesAsync(DocumentInstanceId d, OcrPresetId p, IReadOnlyList<PageId> pages,
        string priority, CancellationToken c = default);

    Task<Result<OcrQueueTask>> EnqueueImagePageAsync(DocumentInstanceId d, OcrPresetId p, PageId page, string imagePath,
        string priority, CancellationToken c = default);

    Task<Result<OcrQueueTask>> EnqueueRenderedPdfPageAsync(DocumentInstanceId d, OcrPresetId p, PageId page, int dpi,
        string priority, CancellationToken c = default);

    Task<Result<OcrQueueTask>> EnqueueRegionAsync(DocumentInstanceId d, OcrPresetId p, PageId page,
        NormalizedBBox regionBBox, string engineId, string adapterKind, string? providerId, string priority,
        CancellationToken c = default);

    Task StartAsync(CancellationToken c = default);
    Task StopAsync(CancellationToken c = default);
    Task WaitForIdleAsync(CancellationToken c = default);
    Task<Result> PauseAsync(string scope, string? target = null, CancellationToken c = default);
    Task<Result> ResumeAsync(string scope, string? target = null, CancellationToken c = default);
    Task<Result> CancelTaskAsync(OcrQueueTaskId id, CancellationToken c = default);
    Task<Result<OcrQueueTask>> RetryTaskAsync(OcrQueueTaskId id, CancellationToken c = default);
    Task<Result<OcrQueueTask>> GetTaskAsync(OcrQueueTaskId id, CancellationToken c = default);
    Task<Result<IReadOnlyList<OcrQueueTask>>> ListTasksAsync(OcrQueueTaskFilter filter, CancellationToken c = default);
    Task<Result<OcrQueueStatus>> GetQueueStatusAsync(CancellationToken c = default);
    Task RunOneSchedulingTickAsync(CancellationToken c = default);
    OcrTaskProgressReport? GetTaskProgress(OcrQueueTaskId taskId);
    DateTimeOffset? GetTaskFinishedAt(OcrQueueTaskId taskId);
    void ClearFinishedTasks();
}
