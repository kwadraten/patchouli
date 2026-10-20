using Patchouli.Core.Results;

namespace Patchouli.Core.Translation;

/// <summary>Cooperative controls for volatile translation runs displayed in the task queue.</summary>
public interface ITranslationTaskControls
{
    Task<Result> SetQueueRunningAsync(bool running, CancellationToken cancellationToken = default);
    Task<Result> SetPausedAsync(bool paused, Guid? runId = null, CancellationToken cancellationToken = default);
    Task<Result<TranslationRunStatus>> RetryAsync(Guid runId, CancellationToken cancellationToken = default);
}
