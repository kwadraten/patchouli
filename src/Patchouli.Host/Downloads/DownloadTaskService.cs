using Patchouli.Core.Downloads;
using Patchouli.Core.Results;

namespace Patchouli.Host.Downloads;

public sealed record DownloadTaskStatus(
    Guid TaskId,
    string DisplayName,
    string State,
    double Progress,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? FinishedAt,
    string? ErrorCode,
    string? ErrorMessage)
{
    public Guid? RetryTaskId { get; init; }

    public IReadOnlyList<DownloadFileProgress> Files { get; init; } = [];

    public bool IsIndeterminate { get; init; }

    public bool CanRetry { get; init; } = true;
}

public sealed record DownloadTaskHandle(Guid TaskId, Task<Result> Completion);

public sealed class DownloadTaskService : IDisposable, IAsyncDisposable, IDownloadTaskRunner
{
    public const string QueuedState = "queued";
    public const string RunningState = "running";
    public const string SucceededState = "succeeded";
    public const string FailedState = "failed";
    public const string CancelledState = "cancelled";

    public const string CancelledErrorCode = "download_cancelled";
    public const string FailedErrorCode = "download_failed";
    public const string ClosedErrorCode = "download_closed";
    public const string NotFoundErrorCode = "download_not_found";
    public const string InvalidStateErrorCode = "download_invalid_state";

    private readonly object _lifecycle = new();
    private readonly Dictionary<Guid, DownloadEntry> _tasks = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;

    private readonly TaskCompletionSource _drained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _shutdownCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _active;
    private bool _closed;
    private bool _shutdownStarted;

    public DownloadTaskService()
    {
        _shutdownToken = _shutdown.Token;
    }

    public event EventHandler? Changed;

    public DownloadTaskHandle Start(string displayName,
        Func<IProgress<double>?, CancellationToken, Task<Result>> download,
        CancellationToken cancellationToken = default,
        IReadOnlyList<DownloadFileProgress>? files = null,
        bool canRetry = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(download);
        DownloadEntry entry;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            entry = AdmitLocked(displayName, download, cancellationToken, files);
            entry.CanRetry = canRetry;
        }

        Launch(entry);
        return new DownloadTaskHandle(entry.Id, entry.Completion.Task);
    }

    public Task<Result> RunAsync(string displayName,
        Func<IProgress<double>?, CancellationToken, Task<Result>> download,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<DownloadFileProgress>? files = null)
    {
        return Start(displayName,
                (worker, token) => download(
                    worker is null ? progress : new CompositeProgress(worker, progress), token),
                cancellationToken, files)
            .Completion;
    }

    private sealed class CompositeProgress(IProgress<double> inner, IProgress<double>? outer)
        : IDownloadProgress
    {
        public void Report(double value)
        {
            inner.Report(value);
            outer?.Report(value);
        }

        public void ReportFile(DownloadFileProgress value)
        {
            if (inner is IDownloadProgress rich)
            {
                rich.ReportFile(value);
            }
        }
    }

    public IReadOnlyList<DownloadTaskStatus> List()
    {
        lock (_lifecycle)
        {
            return _tasks.Values
                .OrderBy(entry => entry.CreatedAt)
                .Select(entry => entry.Snapshot())
                .ToArray();
        }
    }

    public Result Cancel(Guid taskId)
    {
        DownloadEntry entry;
        CancellationTokenSource? cancellation;
        lock (_lifecycle)
        {
            if (!_tasks.TryGetValue(taskId, out entry!))
            {
                return Result.Failure(NotFoundErrorCode, $"Unknown download task '{taskId}'.");
            }

            if (entry.IsTerminalLocked)
            {
                return Result.Failure(InvalidStateErrorCode,
                    $"Download task '{taskId}' already finished ({entry.State}).");
            }

            entry.CancelRequested = true;
            cancellation = entry.Cancellation;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException) when (IsTerminal(entry))
        {
            return Result.Success();
        }

        return Result.Success();
    }

    private bool IsTerminal(DownloadEntry entry)
    {
        lock (_lifecycle)
        {
            return entry.IsTerminalLocked;
        }
    }

    public Result<DownloadTaskHandle> Retry(Guid taskId)
    {
        DownloadEntry retry;
        lock (_lifecycle)
        {
            if (_closed)
            {
                return Result<DownloadTaskHandle>.Failure(ClosedErrorCode,
                    "The download service is shutting down.");
            }

            if (!_tasks.TryGetValue(taskId, out DownloadEntry? entry))
            {
                return Result<DownloadTaskHandle>.Failure(NotFoundErrorCode,
                    $"Unknown download task '{taskId}'.");
            }

            if (entry.RetryTaskId is { } existing &&
                _tasks.TryGetValue(existing, out DownloadEntry? replacement))
            {
                return Result<DownloadTaskHandle>.Success(
                    new DownloadTaskHandle(replacement.Id, replacement.Completion.Task));
            }

            if (entry.State != FailedState)
            {
                return Result<DownloadTaskHandle>.Failure(InvalidStateErrorCode,
                    $"Only a failed download can be retried (task '{taskId}' is {entry.State}).");
            }

            if (!entry.CanRetry)
            {
                return Result<DownloadTaskHandle>.Failure(InvalidStateErrorCode,
                    $"Download task '{taskId}' cannot be retried.");
            }

            retry = AdmitLocked(entry.DisplayName, entry.Download, CancellationToken.None,
                entry.SeedFiles);
            entry.RetryTaskId = retry.Id;
        }

        Launch(retry);

        return Result<DownloadTaskHandle>.Success(
            new DownloadTaskHandle(retry.Id, retry.Completion.Task));
    }

    public void ClearFinished()
    {
        bool removed = false;
        lock (_lifecycle)
        {
            foreach (Guid id in _tasks
                         .Where(pair => pair.Value.IsTerminalLocked)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _tasks.Remove(id);
                removed = true;
            }
        }

        if (removed)
        {
            OnChanged();
        }
    }

    private DownloadEntry AdmitLocked(string displayName,
        Func<IProgress<double>?, CancellationToken, Task<Result>> download,
        CancellationToken cancellationToken, IReadOnlyList<DownloadFileProgress>? files)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DownloadEntry entry = new(Guid.NewGuid(), displayName, download,
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken),
            now, files);
        _tasks.Add(entry.Id, entry);
        _active++;
        return entry;
    }

    private void Launch(DownloadEntry entry)
    {
        entry.Worker = Task.Run(() => RunAsync(entry), CancellationToken.None);
        _ = entry.Worker.ContinueWith(_ => Release(entry), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        OnChanged();
    }

    private async Task<Result> RunAsync(DownloadEntry entry)
    {
        ProgressReporter reporter = new(this, entry);
        Result result;
        try
        {
            lock (_lifecycle)
            {
                entry.State = RunningState;
                entry.UpdatedAt = DateTimeOffset.UtcNow;
            }

            OnChanged();
            entry.Token.ThrowIfCancellationRequested();
            result = await entry.Download(reporter, entry.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (entry.IsCancellationObserved)
        {
            result = Result.Failure(CancelledErrorCode, $"Download of '{entry.DisplayName}' was cancelled.");
        }
        catch (Exception exception)
        {
            result = Result.Failure(FailedErrorCode,
                $"Download of '{entry.DisplayName}' failed: {ClassifyError(exception)}.");
        }

        lock (_lifecycle)
        {
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            entry.FinishedAt = entry.UpdatedAt;
            if (result.IsSuccess)
            {
                entry.Progress = 1;
                entry.State = SucceededState;
            }
            else
            {
                entry.State = result.ErrorCode == CancelledErrorCode ? CancelledState : FailedState;
                entry.ErrorCode = result.ErrorCode;
                entry.ErrorMessage = result.ErrorMessage;
            }

            entry.FinishFilesLocked(result.IsSuccess, entry.State, result.ErrorMessage);
        }

        entry.Completion.TrySetResult(result);
        OnChanged();
        return result;
    }

    private void ReportProgress(DownloadEntry entry, double value)
    {
        bool changed = false;
        lock (_lifecycle)
        {
            if (!entry.IsTerminalLocked && !entry.HasFileReports &&
                !double.IsNaN(value) && !double.IsInfinity(value))
            {
                entry.Progress = Math.Clamp(value, 0, 1);
                entry.UpdatedAt = DateTimeOffset.UtcNow;
                changed = true;
            }
        }

        if (changed)
        {
            OnChanged();
        }
    }

    private void ReportFile(DownloadEntry entry, DownloadFileProgress value)
    {
        bool changed = false;
        lock (_lifecycle)
        {
            if (!entry.IsTerminalLocked)
            {
                entry.ApplyFileReportLocked(value);
                changed = true;
            }
        }

        if (changed)
        {
            OnChanged();
        }
    }

    private void Release(DownloadEntry entry)
    {
        lock (_lifecycle)
        {
            entry.Cancellation?.Dispose();
            if (--_active == 0 && _closed)
            {
                _drained.TrySetResult();
            }
        }
    }

    private void OnChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string ClassifyError(Exception exception)
    {
        return exception switch
        {
            HttpRequestException http => DownloadNetworkErrors.Describe(http),
            UnauthorizedAccessException => "access denied",
            IOException => "I/O error",
            _ => "internal error"
        };
    }

    public void Dispose()
    {
        _ = BeginShutdown();
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(BeginShutdown());
    }

    private Task BeginShutdown()
    {
        Task drain;
        lock (_lifecycle)
        {
            if (_shutdownStarted)
            {
                return _shutdownCompleted.Task;
            }

            _shutdownStarted = true;
            _closed = true;
            if (_active == 0)
            {
                _drained.TrySetResult();
            }

            drain = _drained.Task;
        }

        _ = FinishShutdownAsync(drain);
        return _shutdownCompleted.Task;
    }

    private async Task FinishShutdownAsync(Task drain)
    {
        try
        {
            try
            {
                await _shutdown.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                await drain.ConfigureAwait(false);
                _shutdown.Dispose();
            }

            _shutdownCompleted.TrySetResult();
        }
        catch (Exception exception)
        {
            _shutdownCompleted.TrySetException(exception);
        }
    }

    private sealed class ProgressReporter(DownloadTaskService owner, DownloadEntry entry)
        : IDownloadProgress
    {
        public void Report(double value)
        {
            owner.ReportProgress(entry, value);
        }

        public void ReportFile(DownloadFileProgress value)
        {
            owner.ReportFile(entry, value);
        }
    }

    private sealed class DownloadEntry(
        Guid id,
        string displayName,
        Func<IProgress<double>?, CancellationToken, Task<Result>> download,
        CancellationTokenSource cancellation,
        DateTimeOffset createdAt,
        IReadOnlyList<DownloadFileProgress>? seedFiles)
    {
        private readonly CancellationToken _token = cancellation.Token;

        public Guid Id { get; } = id;
        public string DisplayName { get; } = displayName;
        public Func<IProgress<double>?, CancellationToken, Task<Result>> Download { get; } = download;
        public CancellationTokenSource? Cancellation { get; set; } = cancellation;
        public CancellationToken Token => _token;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public DateTimeOffset UpdatedAt { get; set; } = createdAt;
        public DateTimeOffset? FinishedAt { get; set; }
        public string State { get; set; } = QueuedState;
        public double Progress { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public Guid? RetryTaskId { get; set; }
        public bool CancelRequested { get; set; }
        public Task<Result>? Worker { get; set; }

        public TaskCompletionSource<Result> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<DownloadFileProgress>? SeedFiles { get; } =
            seedFiles?.Select(file => file with
            {
                State = QueuedState,
                DownloadedBytes = 0,
                ErrorMessage = null
            }).ToArray();

        public List<DownloadFileProgress>? Files { get; private set; } =
            seedFiles?.Select(file => file with
            {
                State = QueuedState,
                DownloadedBytes = 0,
                ErrorMessage = null
            }).ToList();

        public bool HasFileReports { get; private set; } = seedFiles is { Count: > 0 };

        public bool IsIndeterminate =>
            Files?.Any(file =>
                file.State is QueuedState or RunningState or "verifying" &&
                file.TotalBytes is not > 0) ?? false;

        public bool CanRetry { get; set; } = true;

        public bool IsTerminalLocked =>
            State is SucceededState or FailedState or CancelledState;

        public bool IsCancellationObserved =>
            CancelRequested || _token.IsCancellationRequested;

        public void ApplyFileReportLocked(DownloadFileProgress value)
        {
            Files ??= [];
            int index = Files.FindIndex(file =>
                string.Equals(file.RelativePath, value.RelativePath, StringComparison.Ordinal));
            DownloadFileProgress reported = value with
            {
                DownloadedBytes = Math.Max(0, value.DownloadedBytes),
                TotalBytes = value.TotalBytes is > 0 ? value.TotalBytes : null
            };
            if (index < 0)
            {
                Files.Add(reported);
            }
            else
            {
                Files[index] = reported;
            }

            HasFileReports = true;
            RecomputeAggregateLocked();
            UpdatedAt = DateTimeOffset.UtcNow;
        }

        public void FinishFilesLocked(bool succeeded, string terminalState, string? errorMessage)
        {
            if (Files is null)
            {
                return;
            }

            for (int i = 0; i < Files.Count; i++)
            {
                DownloadFileProgress file = Files[i];
                if (file.State == SucceededState)
                {
                    continue;
                }

                Files[i] = file with
                {
                    State = succeeded ? SucceededState : terminalState,
                    DownloadedBytes = succeeded && file.TotalBytes is { } total
                        ? total
                        : file.DownloadedBytes,
                    ErrorMessage = succeeded ? file.ErrorMessage : errorMessage ?? file.ErrorMessage
                };
            }

            RecomputeAggregateLocked();
        }

        private void RecomputeAggregateLocked()
        {
            if (Files is not { Count: > 0 } files)
            {
                return;
            }

            bool allTotals = files.All(file => file.TotalBytes is > 0);
            double aggregate;
            if (allTotals)
            {
                double total = files.Sum(file => (double)file.TotalBytes!.Value);
                double downloaded = files.Sum(file =>
                    (double)Math.Min(file.DownloadedBytes, file.TotalBytes!.Value));
                aggregate = total > 0 ? downloaded / total : 1;
            }
            else
            {
                aggregate = files.Average(file => file.State switch
                {
                    SucceededState => 1.0,
                    _ when file.TotalBytes is > 0 =>
                        Math.Clamp((double)file.DownloadedBytes / file.TotalBytes.Value, 0, 1),
                    _ => 0.0
                });
            }

            Progress = State == SucceededState ? 1 : Math.Min(0.999, Math.Clamp(aggregate, 0, 1));
        }

        public DownloadTaskStatus Snapshot()
        {
            return new DownloadTaskStatus(Id, DisplayName, State, Progress, CreatedAt, UpdatedAt,
                FinishedAt, ErrorCode, ErrorMessage)
            {
                RetryTaskId = RetryTaskId,
                Files = Files is null ? [] : Files.ToArray(),
                IsIndeterminate = IsIndeterminate,
                CanRetry = CanRetry
            };
        }
    }
}
