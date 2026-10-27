using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Core.Ids;

namespace Patchouli.Host.Metadata;

/// <summary>
/// Owns one batch's resources and protects its phase and per-item outcome buffer with one lock.
/// </summary>
internal sealed class MetadataBatchTracker
{
    private readonly object _gate = new();
    private readonly MetadataBatchItemResult?[] _items;
    private MetadataRunPhase _phase = MetadataRunPhase.Started;

    public MetadataBatchTracker(Guid taskId, IReadOnlyList<ItemId> itemIds, long sequence,
        CancellationTokenSource cancellation)
    {
        TaskId = taskId;
        ItemIds = [.. itemIds];
        Sequence = sequence;
        _items = new MetadataBatchItemResult?[ItemIds.Count];
        Cancellation = cancellation;
    }

    public Guid TaskId { get; }
    public IReadOnlyList<ItemId> ItemIds { get; }
    public long Sequence { get; }
    public CancellationTokenSource Cancellation { get; }
    public Task RunTask { get; set; } = Task.CompletedTask;

    public void RecordItem(int index, MetadataBatchItemResult result)
    {
        lock (_gate)
        {
            _items[index] = result;
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _phase = _phase.Completed();
        }
    }

    public void CompleteCancelled()
    {
        lock (_gate)
        {
            _phase = _phase.Cancelled();
            for (int i = 0; i < _items.Length; i++)
            {
                _items[i] ??= new MetadataBatchItemResult(ItemIds[i], false, null,
                    MetadataRunPhase.ItemCancelledErrorCode, MetadataRunPhase.ItemCancelledErrorMessage);
            }
        }
    }

    public void Fail(string errorCode, string errorMessage)
    {
        lock (_gate)
        {
            _phase = _phase.Failed(errorCode, errorMessage);
            for (int i = 0; i < _items.Length; i++)
            {
                _items[i] ??= new MetadataBatchItemResult(ItemIds[i], false, null, errorCode, errorMessage);
            }
        }
    }

    public bool TryMarkCancelled()
    {
        lock (_gate)
        {
            MetadataRunPhase? cancelled = _phase.TryCancelled();
            if (cancelled is null)
            {
                return false;
            }

            _phase = cancelled;
            return true;
        }
    }

    public MetadataBatchRunStatus Snapshot()
    {
        lock (_gate)
        {
            return _phase.Project(TaskId, ItemIds, _items);
        }
    }
}
