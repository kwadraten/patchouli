using System.Collections.Concurrent;
using System.Text.Json;
using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Host.Metadata;

/// <summary>
///     Host-side entry point for the metadata endpoint messages. Lookup/apply/enrich complete
///     synchronously; enrich_batch runs as a host-managed background task with a volatile status
///     projection, per-item outcomes, cancellation and request_id deduplication. The task registry is
///     in-memory only: tasks never enter a snapshot and disappear with the host process; committed
///     item metadata survives because apply goes through the Core write path.
/// </summary>
public sealed class MetadataRunService : IMetadataRunService, IAsyncDisposable
{
    private const int BatchConcurrency = 3;
    private readonly IMetadataLookupService _lookup;
    private readonly CancellationToken _hostLifetime;
    private readonly ConcurrentDictionary<Guid, MetadataBatchTracker> _tasks = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);

    private readonly Dictionary<string, (string Fingerprint, MetadataBatchRunStatus Receipt)> _requests =
        new(StringComparer.Ordinal);

    private long _taskSequence;
    private bool _disposed;

    public MetadataRunService(IMetadataLookupService lookup, CancellationToken hostLifetime = default)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        _lookup = lookup;
        _hostLifetime = hostLifetime;
    }

    /// <inheritdoc />
    public Task<Result<MetadataCandidateProposal>> LookupAsync(ItemId itemId, string scheme, string value,
        CancellationToken cancellationToken = default)
    {
        return _lookup.LookupCandidateAsync(itemId, scheme, value, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<MetadataLookupOutcome>> ApplyAsync(MetadataCandidateProposal proposal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return _lookup.ApplyCandidateAsync(proposal, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<MetadataLookupOutcome>> EnrichAsync(ItemId itemId,
        CancellationToken cancellationToken = default)
    {
        return _lookup.EnrichAsync(itemId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<MetadataBatchRunStatus>> StartBatchAsync(IReadOnlyList<ItemId> itemIds,
        string? requestId = null, CancellationToken cancellationToken = default)
    {
        ItemId[] unique = itemIds.Distinct().ToArray();
        if (unique.Length == 0)
        {
            return Result<MetadataBatchRunStatus>.Failure(AppErrorCodes.InvalidArgument,
                "enrich_batch requires at least one item id.");
        }

        string? id = requestId?.Trim();
        if (id is not null && (id.Length == 0 || id.Length > 256))
        {
            return Result<MetadataBatchRunStatus>.Failure(AppErrorCodes.InvalidArgument,
                "request_id must contain 1 through 256 characters.");
        }

        string fingerprint = JsonSerializer.Serialize(new
        {
            endpoint = "runs/metadata/control", action = "enrich_batch",
            items = unique.Select(value => value.ToString()).Order(StringComparer.Ordinal).ToArray()
        });
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (id is not null &&
                _requests.TryGetValue(id, out (string Fingerprint, MetadataBatchRunStatus Receipt) previous))
            {
                return previous.Fingerprint == fingerprint
                    ? Result<MetadataBatchRunStatus>.Success(previous.Receipt with { Reused = true })
                    : Result<MetadataBatchRunStatus>.Failure(AppErrorCodes.Conflict,
                        "request_id was already used for a different metadata message.");
            }

            if (_disposed)
            {
                return Result<MetadataBatchRunStatus>.Failure(AppErrorCodes.InvalidState,
                    "The metadata run service has been disposed.");
            }

            Guid taskId = Guid.NewGuid();
            CancellationTokenSource cancellation = _hostLifetime.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(_hostLifetime)
                : new CancellationTokenSource();
            MetadataBatchTracker state = new(taskId, unique, Interlocked.Increment(ref _taskSequence),
                cancellation);
            state.RunTask = Task.Run(() => ExecuteBatchAsync(state), CancellationToken.None);
            _tasks[taskId] = state;
            MetadataBatchRunStatus receipt = state.Snapshot();
            if (id is not null)
            {
                _requests[id] = (fingerprint, receipt);
            }

            return Result<MetadataBatchRunStatus>.Success(receipt);
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<MetadataBatchRunStatus>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<MetadataBatchRunStatus> tasks = _tasks.Values
            .OrderByDescending(static task => task.Sequence)
            .Select(static task => task.Snapshot())
            .ToArray();
        return Task.FromResult(Result<IReadOnlyList<MetadataBatchRunStatus>>.Success(tasks));
    }

    /// <inheritdoc />
    public Task<Result<MetadataBatchRunStatus>> GetAsync(Guid taskId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_tasks.TryGetValue(taskId, out MetadataBatchTracker? task)
            ? Result<MetadataBatchRunStatus>.Success(task.Snapshot())
            : Result<MetadataBatchRunStatus>.Failure(AppErrorCodes.NotFound,
                $"The metadata task '{taskId}' does not exist."));
    }

    /// <inheritdoc />
    public Task<Result<MetadataBatchRunStatus>> CancelAsync(Guid taskId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_tasks.TryGetValue(taskId, out MetadataBatchTracker? task))
        {
            return Task.FromResult(Result<MetadataBatchRunStatus>.Failure(AppErrorCodes.NotFound,
                $"The metadata task '{taskId}' does not exist."));
        }

        if (task.TryMarkCancelled())
        {
            task.Cancellation.Cancel();
        }

        return Task.FromResult(Result<MetadataBatchRunStatus>.Success(task.Snapshot()));
    }

    /// <inheritdoc />
    public async Task<Result<MetadataBatchRunStatus>> RetryAsync(Guid taskId,
        CancellationToken cancellationToken = default)
    {
        if (!_tasks.TryGetValue(taskId, out MetadataBatchTracker? task))
        {
            return Result<MetadataBatchRunStatus>.Failure(AppErrorCodes.NotFound,
                $"The metadata task '{taskId}' does not exist.");
        }

        MetadataBatchRunStatus status = task.Snapshot();
        if (status.State == MetadataRunState.Running)
        {
            return Result<MetadataBatchRunStatus>.Failure(AppErrorCodes.InvalidState,
                "A running metadata task cannot be retried.");
        }

        ItemId[] failed = status.Items
            .Where(item => !item.IsSuccess)
            .Select(item => item.ItemId)
            .Distinct()
            .ToArray();
        if (failed.Length == 0)
        {
            return Result<MetadataBatchRunStatus>.Failure(AppErrorCodes.InvalidState,
                "No failed metadata batch items to retry.");
        }

        return await StartBatchAsync(failed, $"queue-retry:{taskId}", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Cancels every in-flight task, waits for the background loops to settle, then releases tokens.</summary>
    public async ValueTask DisposeAsync()
    {
        MetadataBatchTracker[] tasks;
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            tasks = _tasks.Values.ToArray();
        }
        finally
        {
            _startGate.Release();
        }

        foreach (MetadataBatchTracker task in tasks)
        {
            if (task.TryMarkCancelled())
            {
                task.Cancellation.Cancel();
            }
        }

        foreach (MetadataBatchTracker task in tasks)
        {
            try
            {
                await task.RunTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                                  exception, "host.metadata-run"))
            {
            }
        }

        foreach (MetadataBatchTracker task in tasks)
        {
            task.Cancellation.Dispose();
        }
    }

    private async Task ExecuteBatchAsync(MetadataBatchTracker state)
    {
        using SemaphoreSlim gate = new(BatchConcurrency, BatchConcurrency);
        try
        {
            Task[] workers = state.ItemIds.Select((itemId, index) => Task.Run(async () =>
            {
                try
                {
                    // Task.WhenAll below observes every worker before the semaphore leaves its using scope.
                    // ReSharper disable once AccessToDisposedClosure
                    await gate.WaitAsync(state.Cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
                {
                    state.RecordItem(index,
                        new MetadataBatchItemResult(itemId, false, null, "cancelled", "The task was cancelled."));
                    return;
                }

                try
                {
                    Result<MetadataLookupOutcome> outcome =
                        await _lookup.EnrichAsync(itemId, state.Cancellation.Token).ConfigureAwait(false);
                    state.RecordItem(index, outcome.IsSuccess
                        ? new MetadataBatchItemResult(itemId, true, outcome.Value.Candidate.SourceId, null, null)
                        : new MetadataBatchItemResult(itemId, false, null, outcome.ErrorCode, outcome.ErrorMessage));
                }
                catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
                {
                    state.RecordItem(index,
                        new MetadataBatchItemResult(itemId, false, null, "cancelled", "The task was cancelled."));
                }
                finally
                {
                    // ReSharper disable once AccessToDisposedClosure
                    gate.Release();
                }
            }, CancellationToken.None)).ToArray();

            await Task.WhenAll(workers).ConfigureAwait(false);
            if (state.Cancellation.IsCancellationRequested)
            {
                state.CompleteCancelled();
            }
            else
            {
                state.Complete();
            }
        }
        catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
        {
            state.CompleteCancelled();
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "host.metadata-run"))
        {
            state.Fail(AppErrorCodes.InvalidState, $"The metadata batch failed: {exception.Message}");
        }
    }
}
