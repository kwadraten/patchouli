using System.Globalization;
using Microsoft.FSharp.Core;
using Patchouli.Core.Results;
using Patchouli.Core.Translation;
using Patchouli.Host.Agent;
using Patchouli.Host.Workflows;
using Patchouli.Mcp;
using Patchouli.Ocr;
using Patchouli.Workflows;
using Patchouli.Workflows.Scripting;

namespace Patchouli.Host.Mcp;

/// <summary>
///     Host-side adapter projecting <see cref="AgentSessionService" /> onto the MCP runs/agent
///     surface: snapshots, the monotonic event log and the inbox/cancel/resume control channel.
///     It only maps data; every state-legality decision stays in <see cref="McpCommandService" />.
/// </summary>
public sealed class McpHostAgentRunsApi(AgentSessionService sessions) : IMcpAgentRunsApi
{
    public async Task<Result<IReadOnlyList<McpAgentSessionStatusProjection>>> ListSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AgentSessionSnapshot> snapshots = await sessions.ListSessionsAsync(cancellationToken)
            .ConfigureAwait(false);
        return Result<IReadOnlyList<McpAgentSessionStatusProjection>>.Success(
            snapshots.Select(Project).ToArray());
    }

    public async Task<Result<McpAgentSessionStatusProjection>> TryGetSessionAsync(string sessionId,
        CancellationToken cancellationToken = default)
    {
        AgentSessionSnapshot? snapshot = sessions.TryGetSnapshot(sessionId) ??
                                         await sessions.TryOpenAsync(sessionId, cancellationToken)
                                             .ConfigureAwait(false);
        return snapshot is null
            ? Result<McpAgentSessionStatusProjection>.Failure(AppErrorCodes.NotFound,
                $"The agent session '{sessionId}' does not exist.")
            : Result<McpAgentSessionStatusProjection>.Success(Project(snapshot));
    }

    public async Task<Result<IReadOnlyList<McpAgentSessionEvent>>> ReadSessionEventsAsync(string sessionId,
        CancellationToken cancellationToken = default)
    {
        Result<McpAgentSessionStatusProjection> session = await TryGetSessionAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        if (session.IsFailure)
        {
            return Result<IReadOnlyList<McpAgentSessionEvent>>.Failure(session.ErrorCode!, session.ErrorMessage!);
        }

        IReadOnlyList<AgentSessionLogEntry> entries =
            await sessions.ReadEventLogAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return Result<IReadOnlyList<McpAgentSessionEvent>>.Success(entries.Select(entry =>
            new McpAgentSessionEvent(
                entry.Seq,
                entry.Kind,
                entry.Payload,
                entry.RecordedAt.ToUniversalTime().ToString("O"))).ToArray());
    }

    public async Task<Result<McpAgentMessageAck>> SendSessionMessageAsync(string sessionId, string messageId,
        string text, CancellationToken cancellationToken = default)
    {
        AgentSessionSnapshot? snapshot = sessions.TryGetSnapshot(sessionId) ??
                                         await sessions.TryOpenAsync(sessionId, cancellationToken)
                                             .ConfigureAwait(false);
        if (snapshot is null)
        {
            return Result<McpAgentMessageAck>.Failure(AppErrorCodes.NotFound,
                $"The agent session '{sessionId}' does not exist.");
        }

        AgentMessageSendResult result = await sessions.SendAsync(
            sessionId, AgentInboxMessage.Create(messageId, text), cancellationToken).ConfigureAwait(false);
        return Result<McpAgentMessageAck>.Success(
            new McpAgentMessageAck(result.Accepted, result.Deduplicated, result.EventSeq));
    }

    public async Task<Result<McpAgentSessionStatusProjection>> CancelSessionAsync(string sessionId,
        CancellationToken cancellationToken = default)
    {
        AgentSessionSnapshot? snapshot = sessions.TryGetSnapshot(sessionId) ??
                                         await sessions.TryOpenAsync(sessionId, cancellationToken)
                                             .ConfigureAwait(false);
        if (snapshot is null)
        {
            return Result<McpAgentSessionStatusProjection>.Failure(AppErrorCodes.NotFound,
                $"The agent session '{sessionId}' does not exist.");
        }

        AgentSessionSnapshot cancelled = await sessions.CancelAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        return Result<McpAgentSessionStatusProjection>.Success(Project(cancelled));
    }

    public async Task<Result<McpAgentSessionStatusProjection>> ResumeSessionAsync(string sessionId,
        CancellationToken cancellationToken = default)
    {
        AgentSessionSnapshot? snapshot = sessions.TryGetSnapshot(sessionId) ??
                                         await sessions.TryOpenAsync(sessionId, cancellationToken)
                                             .ConfigureAwait(false);
        if (snapshot is null)
        {
            return Result<McpAgentSessionStatusProjection>.Failure(AppErrorCodes.NotFound,
                $"The agent session '{sessionId}' does not exist.");
        }

        AgentSessionSnapshot resumed = await sessions.ResumeAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        return Result<McpAgentSessionStatusProjection>.Success(Project(resumed));
    }

    private static McpAgentSessionStatusProjection Project(AgentSessionSnapshot snapshot)
    {
        return new McpAgentSessionStatusProjection(
            snapshot.SessionId,
            StatusName(snapshot.Status),
            snapshot.EventSeq,
            snapshot.HistoryCount,
            snapshot.PendingInboxCount,
            snapshot.PendingEventCount,
            snapshot.Detail,
            snapshot.UpdatedAt.ToUniversalTime().ToString("O"));
    }

    private static string StatusName(AgentSessionStatus status)
    {
        return status switch
        {
            AgentSessionStatus.Running => "running",
            AgentSessionStatus.AwaitingEffect => "awaiting_effect",
            AgentSessionStatus.Cancelled => "cancelled",
            AgentSessionStatus.Stopped => "stopped",
            AgentSessionStatus.Finished => "finished",
            AgentSessionStatus.Failed => "failed",
            _ => "unknown"
        };
    }
}

/// <summary>
///     Host-side adapter over <see cref="WorkflowSessionRunner" /> (and its <see cref="WorkflowStore" />):
///     read-only workflow metadata for <c>workflows/</c> and the <c>send start</c> launch channel.
/// </summary>
public sealed class McpHostWorkflowRunsApi(WorkflowSessionRunner runner) : IMcpWorkflowRunsApi
{
    public async Task<Result<IReadOnlyList<McpWorkflowSummary>>> ListWorkflowsAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<WorkflowDefinition> definitions =
            await runner.Store.ListDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        List<McpWorkflowSummary> summaries = new(definitions.Count);
        foreach (WorkflowDefinition definition in definitions)
        {
            WorkflowConfigurationSnapshot config = await runner.Configuration.ReadAsync(definition.Id,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            WorkflowDeclarationInfo info = config.Analysis.Info;
            summaries.Add(new McpWorkflowSummary(definition.Id,
                config.Analysis.Succeeded ? info.Name : definition.Name,
                config.Analysis.Succeeded ? info.Description : definition.Description));
        }

        return Result<IReadOnlyList<McpWorkflowSummary>>.Success(summaries);
    }

    public async Task<Result<McpWorkflowDetail>> TryGetWorkflowAsync(string workflowId,
        CancellationToken cancellationToken = default)
    {
        FSharpOption<WorkflowDefinition>? loaded = await runner.Store.TryLoadAsync(workflowId, cancellationToken)
            .ConfigureAwait(false);
        if (loaded is null)
        {
            return Result<McpWorkflowDetail>.Failure(AppErrorCodes.NotFound,
                $"The workflow '{workflowId}' does not exist.");
        }

        WorkflowConfigurationSnapshot configuration = await runner.Configuration.ReadAsync(workflowId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return configuration.Analysis.Succeeded
            ? Result<McpWorkflowDetail>.Success(Project(loaded.Value, configuration.Analysis))
            : Result<McpWorkflowDetail>.Failure(AppErrorCodes.InvalidArgument,
                ValidationSummary(configuration.Issues), details: ValidationDetails(configuration.Issues));
    }

    public async Task<Result<McpWorkflowStartResult>> StartAsync(string workflowId,
        IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        FSharpOption<WorkflowDefinition>? loaded = await runner.Store.TryLoadAsync(workflowId, cancellationToken)
            .ConfigureAwait(false);
        if (loaded is null)
        {
            return Result<McpWorkflowStartResult>.Failure(AppErrorCodes.NotFound,
                $"The workflow '{workflowId}' does not exist.");
        }

        try
        {
            WorkflowSessionResult result = await runner.StartAsync(
                    new WorkflowSessionRequest(workflowId, parameters, WorkflowSelections.empty), cancellationToken)
                .ConfigureAwait(false);
            return Result<McpWorkflowStartResult>.Success(new McpWorkflowStartResult(result.Session.SessionId));
        }
        catch (KeyNotFoundException exception)
        {
            return Result<McpWorkflowStartResult>.Failure(AppErrorCodes.NotFound, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Result<McpWorkflowStartResult>.Failure(AppErrorCodes.InvalidArgument, exception.Message);
        }
        catch (WorkflowConfigurationValidationException exception)
        {
            return Result<McpWorkflowStartResult>.Failure(AppErrorCodes.InvalidArgument,
                ValidationSummary(exception.Issues), details: ValidationDetails(exception.Issues));
        }
    }

    private static McpWorkflowDetail Project(WorkflowDefinition definition,
        WorkflowDeclarationAnalysis analysis)
    {
        WorkflowDeclarationInfo info = analysis.Info;
        return new McpWorkflowDetail(
            definition.Id,
            info.Name,
            info.Description,
            info.EntryPoint,
            info.SelectionScope.ToString(),
            definition.Locked,
            definition.BuiltIn,
            new McpWorkflowMenuPlacement(
                info.MenuPath, info.MenuOrder, info.ShowInMenu),
            analysis.Fields
                .Select(field => new McpWorkflowParameter(
                    field.Key,
                    field.Type.ToString(),
                    field.Required,
                    field.Description,
                    field.DefaultValue,
                    field.Label,
                    field.HasDefault,
                    field.Choices,
                    field.Minimum.HasValue ? field.Minimum.Value : null,
                    field.Maximum.HasValue ? field.Maximum.Value : null,
                    string.IsNullOrWhiteSpace(field.ContextBinding) ? null : field.ContextBinding))
                .ToArray());
    }

    private static string ValidationSummary(IReadOnlyList<WorkflowValidationIssue> issues)
    {
        return issues.Count == 0
            ? "The workflow declaration or launch configuration is invalid."
            : string.Join(Environment.NewLine, issues.Select(issue => issue.Message));
    }

    private static McpWorkflowValidationFailureDetails ValidationDetails(
        IReadOnlyList<WorkflowValidationIssue> issues)
    {
        return new McpWorkflowValidationFailureDetails(issues.Select(issue => new McpWorkflowValidationIssue(
            issue.Key, issue.Code, issue.Message, issue.Line, issue.Column, issue.ScopeTarget)).ToArray());
    }
}

/// <summary>
///     Host-side adapter over the OCR queue's public row service: the read-only
///     <c>runs/ocr/{task-id}/status</c> projection. The queue keeps its own name and state
///     vocabulary; nothing here unifies it with agent sessions (D7).
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
///     <c>translation.start</c> and <c>translation.cancel</c> Actions. It only maps data and parses the
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
                request.ForceRetranslate),
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
            run.ErrorMessage);
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
