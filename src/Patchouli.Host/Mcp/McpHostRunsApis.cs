using System.Globalization;
using Patchouli.Core.Results;
using Patchouli.Core.Translation;
using Patchouli.Mcp;
using Patchouli.Ocr;

namespace Patchouli.Host.Mcp;

/// <summary>
///     Host-side adapter over the OCR queue's public row service: the read-only
///     <c>runs/ocr/{task-id}/status</c> projection. The queue keeps its own name and state
///     vocabulary; nothing here unifies it with translation runs.
/// </summary>
public sealed class McpHostOcrRunsApi(
    Func<CancellationToken, Task<Result<IOcrQueueRowService>>> rowsFactory) : IMcpOcrRunsApi
{
    public async Task<Result<IReadOnlyList<McpOcrTaskStatusProjection>>> ListTasksAsync(
        CancellationToken cancellationToken = default)
    {
        Result<IOcrQueueRowService> rows = await rowsFactory(cancellationToken).ConfigureAwait(false);
        if (rows.IsFailure)
        {
            return Result<IReadOnlyList<McpOcrTaskStatusProjection>>.Failure(
                rows.ErrorCode!, rows.ErrorMessage ?? "The OCR queue is unavailable.");
        }

        Result<IReadOnlyList<OcrQueueRow>> listed =
            await rows.Value.ListRowsAsync(true, cancellationToken).ConfigureAwait(false);
        return listed.IsFailure
            ? Result<IReadOnlyList<McpOcrTaskStatusProjection>>.Failure(
                listed.ErrorCode!, listed.ErrorMessage ?? "Listing OCR tasks failed.")
            : Result<IReadOnlyList<McpOcrTaskStatusProjection>>.Success(
                listed.Value.Select(Project).ToArray());
    }

    public async Task<Result<McpOcrTaskStatusProjection>> TryGetTaskStatusAsync(string taskId,
        CancellationToken cancellationToken = default)
    {
        Result<IReadOnlyList<McpOcrTaskStatusProjection>> tasks = await ListTasksAsync(cancellationToken)
            .ConfigureAwait(false);
        if (tasks.IsFailure)
        {
            return Result<McpOcrTaskStatusProjection>.Failure(tasks.ErrorCode!, tasks.ErrorMessage!);
        }

        McpOcrTaskStatusProjection? match = tasks.Value.FirstOrDefault(task =>
            string.Equals(task.TaskId, taskId, StringComparison.Ordinal));
        return match is null
            ? Result<McpOcrTaskStatusProjection>.Failure(AppErrorCodes.NotFound,
                $"The OCR task '{taskId}' does not exist.")
            : Result<McpOcrTaskStatusProjection>.Success(match);
    }

    private static McpOcrTaskStatusProjection Project(OcrQueueRow row)
    {
        return new McpOcrTaskStatusProjection(
            row.TaskId.ToString(),
            row.ItemTitle,
            row.TaskKind,
            row.State,
            row.EngineId,
            row.Task.Source,
            row.PageCount,
            row.PageProgress is { } progress
                ? new McpOcrPageProgress(progress.Succeeded, progress.Failed, progress.Processing, progress.Total)
                : null,
            row.LastErrorCode,
            row.Outcome?.RecognitionState,
            row.Outcome?.Committed,
            row.Outcome?.CommittedPageCount,
            row.Outcome?.IndexState);
    }
}

/// <summary>
///     Host-side adapter over the volatile direct translation runs: the
///     <c>runs/translation/{run-id}/status</c> projection and the start/cancel channel used by the
///     translation endpoint start/cancel messages. It only maps data and parses the
///     shared one-based page selector; validation and execution stay in the host run service.
/// </summary>
public sealed class McpHostTranslationRunsApi(ITranslationRunService runs) : IMcpTranslationRunsApi
{
    private const int MaxPageSelectorLength = 4096;
    private const int MaxSelectedPages = 100_000;

    public async Task<Result<McpTranslationRunStatusProjection>> StartAsync(
        McpTranslationRunStartRequest request, CancellationToken cancellationToken = default)
    {
        Result<IReadOnlyList<int>> pages = ParsePages(request.Pages);
        if (pages.IsFailure)
        {
            return Result<McpTranslationRunStatusProjection>.Failure(pages.ErrorCode!, pages.ErrorMessage!);
        }

        Result<TranslationRunStatus> started = await runs.StartAsync(
            new TranslationRunStartRequest(
                request.DocumentInstanceId,
                pages.Value,
                request.TargetLanguage,
                request.ForceRetranslate, request.RequestId),
            cancellationToken).ConfigureAwait(false);
        return started.IsFailure
            ? Result<McpTranslationRunStatusProjection>.Failure(started.ErrorCode!, started.ErrorMessage!)
            : Result<McpTranslationRunStatusProjection>.Success(Project(started.Value));
    }

    public async Task<Result<IReadOnlyList<McpTranslationRunStatusProjection>>> ListRunsAsync(
        CancellationToken cancellationToken = default)
    {
        Result<IReadOnlyList<TranslationRunStatus>> listed =
            await runs.ListAsync(cancellationToken).ConfigureAwait(false);
        return listed.IsFailure
            ? Result<IReadOnlyList<McpTranslationRunStatusProjection>>.Failure(
                listed.ErrorCode!, listed.ErrorMessage!)
            : Result<IReadOnlyList<McpTranslationRunStatusProjection>>.Success(
                listed.Value.Select(Project).ToArray());
    }

    public async Task<Result<McpTranslationRunStatusProjection>> TryGetRunStatusAsync(string taskId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(taskId, out Guid runId))
        {
            return Result<McpTranslationRunStatusProjection>.Failure(
                AppErrorCodes.InvalidArgument,
                $"The translation run id '{taskId}' must be a UUID.");
        }

        Result<TranslationRunStatus> run = await runs.GetAsync(runId, cancellationToken).ConfigureAwait(false);
        return run.IsFailure
            ? Result<McpTranslationRunStatusProjection>.Failure(run.ErrorCode!, run.ErrorMessage!)
            : Result<McpTranslationRunStatusProjection>.Success(Project(run.Value));
    }

    public async Task<Result<McpTranslationRunStatusProjection>> CancelAsync(string taskId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(taskId, out Guid runId))
        {
            return Result<McpTranslationRunStatusProjection>.Failure(
                AppErrorCodes.InvalidArgument,
                $"The translation run id '{taskId}' must be a UUID.");
        }

        Result<TranslationRunStatus> run = await runs.CancelAsync(runId, cancellationToken).ConfigureAwait(false);
        return run.IsFailure
            ? Result<McpTranslationRunStatusProjection>.Failure(run.ErrorCode!, run.ErrorMessage!)
            : Result<McpTranslationRunStatusProjection>.Success(Project(run.Value));
    }

    /// <summary>
    ///     Parses the shared one-based page selector (<c>1,3-5</c>). A blank selector selects the whole
    ///     document and is passed to the run service as an empty list.
    /// </summary>
    private static Result<IReadOnlyList<int>> ParsePages(string? pages)
    {
        if (string.IsNullOrWhiteSpace(pages))
        {
            return Result<IReadOnlyList<int>>.Success([]);
        }

        string trimmed = pages.Trim();
        if (trimmed.Length > MaxPageSelectorLength)
        {
            return InvalidSelection("The pages selector is too long.");
        }

        SortedSet<int> selected = [];
        foreach (string rawToken in trimmed.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            string token = rawToken.Trim();
            if (token.Length == 0 ||
                token.Any(character => !char.IsAsciiDigit(character) && character != '-'))
            {
                return InvalidSelection($"Invalid page range '{token}'. Use one-based physical pages like '1,3-5'.");
            }

            string[] bounds = token.Split('-');
            if (bounds.Length is < 1 or > 2 || bounds.Any(static bound => bound.Length == 0) ||
                !TryParsePageNumber(bounds[0], out int first))
            {
                return InvalidSelection($"Invalid page range '{token}'. Use one-based physical pages like '1,3-5'.");
            }

            int last = first;
            if (bounds.Length == 2 && !TryParsePageNumber(bounds[1], out last))
            {
                return InvalidSelection($"Invalid page range '{token}'. Use one-based physical pages like '1,3-5'.");
            }

            if (last < first)
            {
                return InvalidSelection($"Invalid page range '{token}'. Use one-based physical pages like '1,3-5'.");
            }

            long added = (long)last - first + 1;
            if (selected.Count + added > MaxSelectedPages)
            {
                return InvalidSelection($"The pages selector expands to more than {MaxSelectedPages} pages.");
            }

            for (int page = first;; page++)
            {
                selected.Add(page);
                if (page == last)
                {
                    break;
                }
            }
        }

        return Result<IReadOnlyList<int>>.Success(selected.ToArray());
    }

    private static Result<IReadOnlyList<int>> InvalidSelection(string message)
    {
        return Result<IReadOnlyList<int>>.Failure(AppErrorCodes.InvalidArgument, message);
    }

    private static bool TryParsePageNumber(string value, out int page)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out page) && page > 0;
    }

    private static McpTranslationRunStatusProjection Project(TranslationRunStatus run)
    {
        return new McpTranslationRunStatusProjection(
            run.RunId.ToString("D"),
            run.DocumentInstanceId.ToString(),
            StateName(run.State),
            run.TotalPages,
            run.CompletedPages,
            run.Succeeded,
            run.Partial,
            run.Failed,
            run.Skipped,
            run.CancelledPages,
            run.Pages.Select(page => new McpTranslationPageStatusProjection(
                page.PageIndex,
                PageStateName(page.State),
                page.CommittedBoxCount,
                page.ErrorCode,
                page.ErrorMessage)).ToArray(),
            run.ErrorCode,
            run.ErrorMessage, run.Reused);
    }

    private static string StateName(TranslationRunLifecycle state)
    {
        return state switch
        {
            TranslationRunLifecycle.Running => "running",
            TranslationRunLifecycle.Completed => "completed",
            TranslationRunLifecycle.Cancelled => "cancelled",
            TranslationRunLifecycle.Failed => "failed",
            _ => "unknown"
        };
    }

    private static string PageStateName(TranslationPageRunState state)
    {
        return state switch
        {
            TranslationPageRunState.Skipped => "skipped",
            TranslationPageRunState.Committed => "committed",
            TranslationPageRunState.Partial => "partial",
            TranslationPageRunState.Failed => "failed",
            TranslationPageRunState.Cancelled => "cancelled",
            _ => "unknown"
        };
    }
}
