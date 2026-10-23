using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Cli;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Core.Search;
using Patchouli.Host.Composition;
using Patchouli.Host.Mcp;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Database;
using Patchouli.Mcp;
using Patchouli.Ocr;
using Patchouli.UI;

namespace Patchouli.Tests;

public sealed class OcrHostCommandIndependentTests
{
    [Fact]
    public async Task Legacy_unpinned_document_retry_pins_resolved_version_before_current_preset_changes()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion original =
            (await context.Services.OcrPresets.GetCurrentVersionAsync(context.Preset.PresetId)).Value;
        IReadOnlyList<Page> pages = (await context.Services.Pages.ListPagesAsync(context.DocumentId)).Value;
        Result<OcrQueueTask> legacy = await context.Queue.EnqueueDocumentAsync(context.DocumentId,
            context.Preset.PresetId, pages.Select(page => page.PageId).ToArray(), OcrEngineIds.Mock,
            OcrAdapterKind.Mock, null, OcrQueuePriority.UserStartedDocument);
        legacy.IsSuccess.Should().BeTrue(legacy.ErrorMessage);
        legacy.Value.PresetVersionId.Should().BeNull();
        (await context.Queue.CancelTaskAsync(legacy.Value.TaskId)).IsSuccess.Should().BeTrue();

        Result<McpOcrCommandResult> retried = await context.Services.OcrCommands.RetryAsync(legacy.Value.TaskId,
            "legacy-before-recognition");
        retried.IsSuccess.Should().BeTrue(retried.ErrorMessage);
        retried.Value.PresetVersionId.Should().Be(original.PresetVersionId.ToString());
        (await context.Services.OcrPresets.CreatePresetVersionAsync(context.Preset.PresetId, OcrEngineIds.Mock,
            original.ModelId, original.ModelPath, "{\"new_current\":true}", false)).IsSuccess.Should().BeTrue();
        await context.Queue.ResumeAsync(OcrPauseScope.Global);
        await context.Queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));

        OcrQueueTask actualTask = (await context.Queue.GetTaskAsync(OcrQueueTaskId.Parse(retried.Value.TaskId))).Value;
        actualTask.State.Should().Be(OcrQueueTaskState.Succeeded, actualTask.LastErrorMessage);
        actualTask.RunId.Should().NotBeNull();
        OcrRun actualRun = (await context.Services.Ocr.GetRunAsync(actualTask.RunId!.Value)).Value;
        actualRun.PresetVersionId.ToString().Should().Be(retried.Value.PresetVersionId);
    }

    [Fact]
    public async Task
        Legacy_document_retry_reuses_retained_v1_recognition_and_receipts_v1_after_current_preset_becomes_v2()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion original =
            (await context.Services.OcrPresets.GetCurrentVersionAsync(context.Preset.PresetId)).Value;
        IReadOnlyList<Page> pages = (await context.Services.Pages.ListPagesAsync(context.DocumentId)).Value;
        PageEditSession draft = (await context.Services.DocumentTrees.BeginPageEditAsync(context.DocumentId,
            pages[0].PageId)).Value;
        Result<OcrQueueTask> legacy = await context.Queue.EnqueueDocumentAsync(context.DocumentId,
            context.Preset.PresetId, pages.Select(page => page.PageId).ToArray(), OcrEngineIds.Mock,
            OcrAdapterKind.Mock, null, OcrQueuePriority.UserStartedDocument);
        legacy.IsSuccess.Should().BeTrue(legacy.ErrorMessage);
        legacy.Value.PresetVersionId.Should().BeNull();
        await context.Queue.ResumeAsync(OcrPauseScope.Global);
        await context.Queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));
        OcrQueueTask failed = (await context.Queue.GetTaskAsync(legacy.Value.TaskId)).Value;
        failed.State.Should().BeOneOf(OcrQueueTaskState.Failed, OcrQueueTaskState.Blocked);
        failed.RunId.Should().NotBeNull();
        OcrRun retained = (await context.Services.Ocr.GetRunAsync(failed.RunId!.Value)).Value;
        retained.PresetVersionId.Should().Be(original.PresetVersionId);
        retained.State.Should().Be(OcrRunState.Completed);
        (await context.Services.DocumentTrees.DiscardPageEditAsync(draft.SessionId)).IsSuccess.Should().BeTrue();
        await context.Queue.PauseAsync(OcrPauseScope.Global);
        (await context.Services.OcrPresets.CreatePresetVersionAsync(context.Preset.PresetId, OcrEngineIds.Mock,
            original.ModelId, original.ModelPath, "{\"version\":2}", false)).IsSuccess.Should().BeTrue();

        Result<McpOcrCommandResult> retried = await context.Services.OcrCommands.RetryAsync(legacy.Value.TaskId,
            "legacy-retained-recognition");

        retried.IsSuccess.Should().BeTrue(retried.ErrorMessage);
        retried.Value.PresetVersionId.Should().Be(original.PresetVersionId.ToString());
        await context.Queue.ResumeAsync(OcrPauseScope.Global);
        await context.Queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));
        OcrQueueTask actual = (await context.Queue.GetTaskAsync(OcrQueueTaskId.Parse(retried.Value.TaskId))).Value;
        actual.State.Should().Be(OcrQueueTaskState.Succeeded, actual.LastErrorMessage);
        actual.RunId.Should().Be(failed.RunId, "retry should reuse the retained successful recognition");
        await using SqliteConnection connection = context.Services.ConnectionFactory.CreateReadConnection();
        await connection.OpenAsync();
        (await connection.ExecuteScalarAsync<int>("select count(*) from ocr_runs where document_instance_id=@Doc;",
            new { Doc = context.DocumentId.ToString() })).Should().Be(1);
    }

    [Fact]
    public async Task
        Auto_apply_preset_dirty_marker_exception_preserves_committed_run_before_executor_receives_recognition_result()
    {
        await using Context context = await Context.CreateAsync();
        OcrPreset auto = (await context.Services.OcrPresets.CreatePresetAsync("Auto-apply fault fixture", null,
            OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", true)).Value;
        PostCommitDirtyMarker marker = new(context.Services.ConnectionFactory);
        OcrRunEngine engine = new(context.Services.ConnectionFactory, context.Services.Clock, new MockOcrEngine(),
            marker);
        OcrQueueScheduler queue = new(LibraryId.New(), context.Services.Clock,
            new OcrQueueTaskExecutor(engine, context.Services.SearchUnits, new FailedIndex()));
        try
        {
            IReadOnlyList<Page> pages = (await context.Services.Pages.ListPagesAsync(context.DocumentId)).Value;
            Result<OcrQueueTask> accepted = await queue.EnqueueAsync(new OcrQueueTaskRequest(context.DocumentId,
                auto.PresetId, pages.Select(page => page.PageId).ToArray(), OcrQueueTaskKind.Document,
                OcrEngineIds.Mock, OcrAdapterKind.Mock, null, OcrQueuePriority.UserStartedDocument, MaxAttempts: 1));
            accepted.IsSuccess.Should().BeTrue(accepted.ErrorMessage);

            await queue.StartAsync();
            await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));

            marker.CommittedRunId.Should().NotBeNull("the dirty-marker exception follows an actual candidate adoption");
            OcrQueueTask task = (await queue.GetTaskAsync(accepted.Value.TaskId)).Value;
            task.State.Should().Be(OcrQueueTaskState.Failed);
            task.RunId.Should().Be(marker.CommittedRunId);
            OcrQueueRowService rows = new(queue, context.Services.ConnectionFactory);
            McpHostOcrRunsApi publicRuns = new(_ => Task.FromResult(Result<IOcrQueueRowService>.Success(rows)));
            Result<McpOcrTaskStatusProjection> status = await publicRuns.TryGetTaskStatusAsync(task.TaskId.ToString());
            status.IsSuccess.Should().BeTrue(status.ErrorMessage);
            status.Value.RecognitionState.Should().Be(OcrRunState.Completed);
            status.Value.Committed.Should().BeTrue();
            status.Value.CommittedPageCount.Should().Be(3);
            status.Value.IndexState.Should().NotBeNullOrWhiteSpace().And.NotBe("indexed");
            JsonSerializer.Serialize(status.Value).Should().NotContain("FAKE_SECRET_INDEPENDENT").And
                .NotContain("private");
            foreach (Page page in pages)
            {
                (await context.Services.DocumentTrees.GetCurrentRevisionAsync(context.DocumentId, page.PageId))
                    .IsSuccess.Should().BeTrue();
            }
        }
        finally
        {
            await queue.StopAsync();
        }
    }

    private sealed class PostCommitDirtyMarker(SqliteConnectionFactory database) : ISearchDirtyMarker
    {
        public OcrRunId? CommittedRunId { get; private set; }

        public async Task<Result> MarkDocumentInstanceDirtyAsync(DocumentInstanceId documentInstanceId,
            CancellationToken cancellationToken = default)
        {
            await using SqliteConnection connection = database.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            string? run = await connection.ExecuteScalarAsync<string>(
                "select a.ocr_run_id from ocr_candidate_adoptions a join ocr_runs r on r.ocr_run_id=a.ocr_run_id where r.document_instance_id=@Doc limit 1;",
                new { Doc = documentInstanceId.ToString() });
            CommittedRunId = run is null ? null : OcrRunId.Parse(run);
            throw new InvalidOperationException(
                "Dirty marker failed C:\\private\\cache; api_key=FAKE_SECRET_INDEPENDENT");
        }
    }

    [Fact]
    public async Task Explicit_second_ocr_can_replace_existing_ocr_body_when_no_concurrent_edit_occurs()
    {
        await using Context context = await Context.CreateAsync();
        Result<McpOcrCommandResult> first = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "mock", "first-ocr"));
        first.IsSuccess.Should().BeTrue(first.ErrorMessage);
        await context.Queue.ResumeAsync(OcrPauseScope.Global);
        await context.Queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));
        IReadOnlyList<Page> pages = (await context.Services.Pages.ListPagesAsync(context.DocumentId)).Value;
        List<DocumentTreeRevisionId> originalRevisions = [];
        foreach (Page page in pages)
        {
            originalRevisions.Add((await context.Services.DocumentTrees.GetCurrentRevisionAsync(context.DocumentId,
                page.PageId)).Value.TreeRevisionId);
        }

        await context.Queue.PauseAsync(OcrPauseScope.Global);

        Result<McpOcrCommandResult> second = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "mock", "second-ocr"));
        second.IsSuccess.Should().BeTrue(second.ErrorMessage);
        await context.Queue.ResumeAsync(OcrPauseScope.Global);
        await context.Queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));

        second.Value.TaskId.Should().NotBe(first.Value.TaskId);
        OcrQueueTask task = (await context.Queue.GetTaskAsync(OcrQueueTaskId.Parse(second.Value.TaskId))).Value;
        task.State.Should().Be(OcrQueueTaskState.Succeeded, task.LastErrorMessage);
        for (int index = 0; index < pages.Count; index++)
        {
            (await context.Services.DocumentTrees.GetCurrentRevisionAsync(context.DocumentId, pages[index].PageId))
                .Value.TreeRevisionId.Should().NotBe(originalRevisions[index]);
        }
    }

    [Fact]
    public async Task Manual_commit_while_recognition_is_gated_conflicts_with_baseline_captured_before_engine_work()
    {
        await using Context context = await Context.CreateAsync();
        Result<McpOcrCommandResult> initial = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "mock", "initial-for-concurrent-edit"));
        initial.IsSuccess.Should().BeTrue(initial.ErrorMessage);
        await context.Queue.ResumeAsync(OcrPauseScope.Global);
        await context.Queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Page page = (await context.Services.Pages.ListPagesAsync(context.DocumentId)).Value[0];
        GatedMockEngine adapter = new();
        OcrRunEngine engine = new(context.Services.ConnectionFactory, context.Services.Clock, adapter);
        Task<Result<OcrRun>> recognizing = engine.RunPresetOnDocumentAsync(context.DocumentId, context.Preset.PresetId);
        DocumentTreeRevisionId manualRevision = default;
        try
        {
            await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            PageEditSession edit = (await context.Services.DocumentTrees.BeginPageEditAsync(context.DocumentId,
                page.PageId)).Value;
            (await context.Services.DocumentTreeEditor.DrawAndInsertLeafAsync(edit.SessionId,
                new InsertLeafCommand(null, null, DocumentBoxType.Text, null, new NormalizedBBox(.1, .1, .2, .2),
                    new TextBoxPayload("Saved while OCR engine was running")))).IsSuccess.Should().BeTrue();
            (await context.Services.DocumentTrees.CommitPageEditAsync(edit.SessionId)).IsSuccess.Should().BeTrue();
            manualRevision = edit.DraftRevisionId;
        }
        finally
        {
            adapter.Release.TrySetResult();
            await recognizing.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Result<OcrRun> candidate = await recognizing;
        candidate.IsSuccess.Should().BeTrue(candidate.ErrorMessage);

        Result<OcrCandidateCommit> commit = await engine.CommitCandidateRunAsync(candidate.Value.OcrRunId);

        commit.IsFailure.Should().BeTrue();
        commit.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        (await context.Services.DocumentTrees.GetCurrentRevisionAsync(context.DocumentId, page.PageId))
            .Value.TreeRevisionId.Should().Be(manualRevision);
        (await context.Services.DocumentTrees.ListBoxesAsync(manualRevision)).Value.Should().Contain(box =>
            box.Payload == new TextBoxPayload("Saved while OCR engine was running"));
    }

    private sealed class GatedMockEngine : IOcrEngine
    {
        private readonly MockOcrEngine _inner = new();
        private int _started;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string EngineId => OcrEngineIds.Mock;

        public async Task<OcrEnginePageResult> RunPageAsync(Page page, OcrPresetVersion presetVersion,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return await _inner.RunPageAsync(page, presetVersion, cancellationToken);
        }
    }

    [Fact]
    public async Task Historical_adoption_does_not_claim_its_superseded_revisions_are_currently_indexed()
    {
        await using Context context = await Context.CreateAsync();
        Result<McpOcrCommandResult> accepted = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "mock", "historical-adoption"));
        accepted.IsSuccess.Should().BeTrue(accepted.ErrorMessage);
        await context.Queue.ResumeAsync(OcrPauseScope.Global);
        await context.Queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));
        McpHostOcrRunsApi publicRuns = new(context.Services.GetOcrQueueRowsAsync);
        Result<McpOcrTaskStatusProjection> before = await publicRuns.TryGetTaskStatusAsync(accepted.Value.TaskId);
        before.IsSuccess.Should().BeTrue(before.ErrorMessage);
        before.Value.Committed.Should().BeTrue();
        before.Value.CommittedPageCount.Should().Be(3);
        before.Value.IndexState.Should().Be("indexed");
        IReadOnlyList<Page> pages = (await context.Services.Pages.ListPagesAsync(context.DocumentId)).Value;
        foreach (Page page in pages)
        {
            PageEditSession edit = (await context.Services.DocumentTrees.BeginPageEditAsync(context.DocumentId,
                page.PageId)).Value;
            (await context.Services.DocumentTreeEditor.DrawAndInsertLeafAsync(edit.SessionId,
                    new InsertLeafCommand(null, null, DocumentBoxType.Text, null,
                        new NormalizedBBox(.05, .05, .2, .2), new TextBoxPayload("New manual correction"))))
                .IsSuccess.Should().BeTrue();
            (await context.Services.DocumentTrees.CommitPageEditAsync(edit.SessionId)).IsSuccess.Should().BeTrue();
            (await context.Services.DocumentTrees.GetCurrentRevisionAsync(context.DocumentId, page.PageId))
                .Value.TreeRevisionId.Should().Be(edit.DraftRevisionId);
        }

        (await context.Services.SearchUnits.RebuildForDocumentInstanceAsync(context.DocumentId)).IsSuccess.Should()
            .BeTrue();
        (await context.Services.SearchIndex.RebuildFtsForDocumentInstanceAsync(context.DocumentId)).IsSuccess.Should()
            .BeTrue();

        Result<McpOcrTaskStatusProjection> historical = await publicRuns.TryGetTaskStatusAsync(accepted.Value.TaskId);

        historical.IsSuccess.Should().BeTrue(historical.ErrorMessage);
        historical.Value.RecognitionState.Should().Be(OcrRunState.Completed);
        historical.Value.Committed.Should().BeTrue("the original adoption remains historically committed");
        historical.Value.CommittedPageCount.Should().Be(3);
        JsonElement wire = JsonSerializer.SerializeToElement(historical.Value);
        bool supersededState = historical.Value.IndexState is "superseded" or "not_current";
        bool explicitlyScopedCurrentIndex = wire.TryGetProperty("index_scope", out JsonElement scope)
                                            && scope.GetString() == "current_document"
                                            && wire.TryGetProperty("adoption_current", out JsonElement current)
                                            && current.ValueKind == JsonValueKind.False;
        (supersededState || explicitlyScopedCurrentIndex).Should().BeTrue(
            "current-document index readiness must not be attributed to an OCR adoption whose revisions were superseded");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exception_or_cancellation_after_durable_commit_retains_run_identity_and_public_outcome(
        bool cancel)
    {
        await using Context context = await Context.CreateAsync();
        OcrRunEngine realEngine = new(context.Services.ConnectionFactory, context.Services.Clock, new MockOcrEngine());
        IOcrRunEngine proxy = DispatchProxy.Create<IOcrRunEngine, PostCommitFaultEngine>();
        PostCommitFaultEngine fault = (PostCommitFaultEngine)(object)proxy;
        fault.Inner = realEngine;
        fault.Cancel = cancel;
        OcrQueueScheduler queue = new(LibraryId.New(), context.Services.Clock,
            new OcrQueueTaskExecutor(proxy, context.Services.SearchUnits, context.Services.SearchIndex),
            new NeverRetry());
        try
        {
            IReadOnlyList<Page> pages = (await context.Services.Pages.ListPagesAsync(context.DocumentId)).Value;
            Result<OcrQueueTask> accepted = await queue.EnqueueAsync(new OcrQueueTaskRequest(context.DocumentId,
                context.Preset.PresetId, pages.Select(page => page.PageId).ToArray(), OcrQueueTaskKind.Document,
                OcrEngineIds.Mock, OcrAdapterKind.Mock, null, OcrQueuePriority.UserStartedDocument, MaxAttempts: 1));
            accepted.IsSuccess.Should().BeTrue(accepted.ErrorMessage);

            await queue.StartAsync();
            await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));

            fault.CommittedRunId.Should().NotBeNull("the injected fault must happen after an actual DB commit");
            OcrQueueTask task = (await queue.GetTaskAsync(accepted.Value.TaskId)).Value;
            task.State.Should().Be(cancel ? OcrQueueTaskState.Cancelled : OcrQueueTaskState.Failed);
            task.RunId.Should().Be(fault.CommittedRunId);
            OcrQueueRowService rows = new(queue, context.Services.ConnectionFactory);
            McpHostOcrRunsApi publicRuns = new(_ => Task.FromResult(Result<IOcrQueueRowService>.Success(rows)));
            Result<McpOcrTaskStatusProjection> status = await publicRuns.TryGetTaskStatusAsync(task.TaskId.ToString());
            status.IsSuccess.Should().BeTrue(status.ErrorMessage);
            status.Value.RecognitionState.Should().Be(OcrRunState.Completed);
            status.Value.Committed.Should().BeTrue();
            status.Value.CommittedPageCount.Should().Be(3);
            status.Value.IndexState.Should().NotBeNullOrWhiteSpace().And.NotBe("indexed");
            JsonSerializer.Serialize(status.Value).Should().NotContain("FAKE_SECRET_INDEPENDENT").And
                .NotContain("private");
            foreach (Page page in pages)
            {
                (await context.Services.DocumentTrees.GetCurrentRevisionAsync(context.DocumentId, page.PageId))
                    .IsSuccess.Should().BeTrue();
            }
        }
        finally
        {
            await queue.StopAsync();
        }
    }

    private class PostCommitFaultEngine : DispatchProxy
    {
        public IOcrRunEngine Inner { get; set; } = null!;
        public bool Cancel { get; set; }
        public OcrRunId? CommittedRunId { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            object? result = targetMethod!.Invoke(Inner, args);
            return targetMethod.Name == nameof(IOcrRunEngine.CommitCandidateRunAsync)
                ? FailAfterCommit((Task<Result<OcrCandidateCommit>>)result!)
                : result;
        }

        private async Task<Result<OcrCandidateCommit>> FailAfterCommit(Task<Result<OcrCandidateCommit>> operation)
        {
            Result<OcrCandidateCommit> result = await operation;
            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            CommittedRunId = result.Value.OcrRunId;
            const string sensitive = "Post-commit failure C:\\private\\cache; api_key=FAKE_SECRET_INDEPENDENT";
            if (Cancel)
            {
                throw new OperationCanceledException(sensitive, new CancellationToken(true));
            }

            throw new InvalidOperationException(sensitive);
        }
    }

    private sealed class NeverRetry : IOcrRetryPolicy
    {
        public string Classify(string? errorCode)
        {
            return OcrRetryClassification.NonRetryable;
        }

        public bool ShouldRetry(OcrQueueTask task, string? errorCode)
        {
            return false;
        }

        public TimeSpan GetNextDelay(int attemptCount)
        {
            return TimeSpan.Zero;
        }
    }

    [Fact]
    public async Task Durable_queue_outcome_reports_committed_body_when_only_fts_rebuild_fails()
    {
        await using Context context = await Context.CreateAsync();
        OcrRunEngine engine = new(context.Services.ConnectionFactory, context.Services.Clock, new MockOcrEngine());
        OcrQueueScheduler queue = new(LibraryId.New(), context.Services.Clock,
            new OcrQueueTaskExecutor(engine, context.Services.SearchUnits, new FailedIndex()));
        try
        {
            IReadOnlyList<Page> pages = (await context.Services.Pages.ListPagesAsync(context.DocumentId)).Value;
            Result<OcrQueueTask> accepted = await queue.EnqueueAsync(new OcrQueueTaskRequest(context.DocumentId,
                context.Preset.PresetId,
                pages.Select(page => page.PageId).ToArray(), OcrQueueTaskKind.Document, OcrEngineIds.Mock,
                OcrAdapterKind.Mock, null, OcrQueuePriority.UserStartedDocument, MaxAttempts: 1));
            accepted.IsSuccess.Should().BeTrue(accepted.ErrorMessage);

            await queue.StartAsync();
            await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));

            OcrQueueRowService rows = new(queue, context.Services.ConnectionFactory);
            Result<IReadOnlyList<OcrQueueRow>> listed = await rows.ListRowsAsync(true);
            listed.IsSuccess.Should().BeTrue(listed.ErrorMessage);
            OcrQueueRow row = listed.Value.Should().ContainSingle().Subject;
            row.State.Should().Be(OcrQueueTaskState.Failed);
            row.Outcome.Should().NotBeNull();
            row.Outcome!.RecognitionState.Should().Be(OcrRunState.Completed);
            row.Outcome.Committed.Should().BeTrue();
            row.Outcome.CommittedPageCount.Should().Be(3);
            row.Outcome.IndexState.Should().NotBeNullOrWhiteSpace().And.NotBe("indexed");
            McpHostOcrRunsApi publicRuns = new(_ => Task.FromResult(Result<IOcrQueueRowService>.Success(rows)));
            Result<McpOcrTaskStatusProjection>
                projected = await publicRuns.TryGetTaskStatusAsync(row.TaskId.ToString());
            projected.IsSuccess.Should().BeTrue(projected.ErrorMessage);
            projected.Value.State.Should().Be("failed");
            projected.Value.RecognitionState.Should().Be(OcrRunState.Completed);
            projected.Value.Committed.Should().BeTrue();
            projected.Value.CommittedPageCount.Should().Be(3);
            projected.Value.IndexState.Should().Be(row.Outcome.IndexState);
            string publicJson = JsonSerializer.Serialize(projected.Value);
            publicJson.Should().NotContain("FAKE_SECRET_INDEPENDENT").And.NotContain("private");
            foreach (Page page in pages)
            {
                (await context.Services.DocumentTrees.GetCurrentRevisionAsync(context.DocumentId, page.PageId))
                    .IsSuccess.Should().BeTrue();
            }
        }
        finally
        {
            await queue.StopAsync();
        }
    }

    private sealed class FailedIndex : ISearchIndexRebuilder
    {
        public Task<Result> EnsureCacheAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> RebuildFtsForDocumentInstanceAsync(DocumentInstanceId documentInstanceId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Failure("independent_fts_failure",
                "Cannot index C:\\private\\cache; api_key=FAKE_SECRET_INDEPENDENT"));
        }

        public Task<Result> RebuildFtsForLibraryAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> SetIndexUnavailableAsync(string scopeType, string scopeId, string reason,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    [Fact]
    public async Task Http_cli_missing_source_error_retains_diagnostic_and_does_not_become_internal()
    {
        await using Context context = await Context.CreateAsync();
        File.Delete(context.Source);
        McpServerHost server = await StartHttpServer(context);
        try
        {
            using McpHttpClient client = new(server.Endpoint, null);
            await client.InitializeAsync();
            CliToolCall start = CliArguments.BuildToolCall("send",
            [
                "runs/ocr/control", "start", $"patchouli://texts/{context.DocumentId}/", "--args",
                """{"engine":"mock"}"""
            ], true);

            CliToolResponse response = await client.CallToolAsync(start.Tool, start.Arguments);

            response.IsError.Should().BeTrue();
            response.Text.Should().NotContain("INTERNAL").And.NotContain("source.pdf")
                .And.NotContain(Path.GetFileName(context.Root));
            response.Text.ToLowerInvariant().Should().Contain("source");
            (await context.Queue.ListTasksAsync(new OcrQueueTaskFilter())).Value.Should().BeEmpty();
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task Http_cli_model_readiness_error_retains_diagnostic_without_paths_or_credentials()
    {
        await using Context context = await Context.CreateAsync();
        context.Services.OcrAdapters.RegisterAdapter(new UnreadyAdapter(OcrEnvironmentStatus.MissingModelPath,
            OcrRequiredAction.RebindModelPath));
        McpServerHost server = await StartHttpServer(context);
        try
        {
            using McpHttpClient client = new(server.Endpoint, null);
            await client.InitializeAsync();
            CliToolCall start = CliArguments.BuildToolCall("send",
            [
                "runs/ocr/control", "start", $"patchouli://texts/{context.DocumentId}/", "--args",
                """{"engine":"mock"}"""
            ], true);

            CliToolResponse response = await client.CallToolAsync(start.Tool, start.Arguments);

            response.IsError.Should().BeTrue();
            response.Text.Should().NotContain("INTERNAL").And.NotContain("private").And
                .NotContain("FAKE_SECRET_INDEPENDENT");
            response.Text.ToLowerInvariant().Should().Contain("model");
            (await context.Queue.ListTasksAsync(new OcrQueueTaskFilter())).Value.Should().BeEmpty();
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Theory]
    [InlineData("target")]
    [InlineData("action")]
    [InlineData("args")]
    public async Task Http_send_duplicate_outer_fields_are_rejected_without_queueing(string field)
    {
        await using Context context = await Context.CreateAsync();
        McpServerHost server = await StartHttpServer(context);
        try
        {
            string target = JsonSerializer.Serialize($"patchouli://texts/{context.DocumentId}/");
            string duplicate = field switch { "target" => target, "action" => "\"start\"", _ => "{}" };
            string request =
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"patchouli.send\",\"arguments\":{\"endpoint\":\"runs/ocr/control\",\"target\":"
                + target + ",\"action\":\"start\",\"args\":{},\"format\":\"json\","
                + JsonSerializer.Serialize(field) + ":" + duplicate + "}}}";
            using HttpClient client = new();
            using HttpResponseMessage response = await client.PostAsync(server.Endpoint,
                new StringContent(request, System.Text.Encoding.UTF8, "application/json"));
            response.IsSuccessStatusCode.Should().BeTrue();
            using JsonDocument rpc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            rpc.RootElement.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
            rpc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()
                .Should().Contain("INVALID_ARGUMENT");
            (await context.Queue.ListTasksAsync(new OcrQueueTaskFilter())).Value.Should().BeEmpty();
        }
        finally
        {
            await server.StopAsync();
        }
    }

    private static async Task<McpServerHost> StartHttpServer(Context context)
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        McpServerHost server = new(context.Services, (_, _, _) => { });
        await server.StartAsync(new McpServerSettings(port, "127.0.0.1", false, [], false, null,
            [new McpToolOverride("patchouli.send", true)], DateTimeOffset.UtcNow)
        {
            DomainPermissions =
            [
                new McpDomainPermission("runs/ocr", "send", true)
            ]
        });
        server.IsRunning.Should().BeTrue();
        return server;
    }

    [Fact]
    public async Task Cli_mapping_crosses_real_http_transport_to_host_queue_status_and_committed_text()
    {
        await using Context context = await Context.CreateAsync();
        PhaseLog phases = new();
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        McpServerSettings settings = new(port, "127.0.0.1", false, [], false, null,
            [new McpToolOverride("patchouli.send", true)], DateTimeOffset.UtcNow)
        {
            DomainPermissions =
            [
                new McpDomainPermission("runs/ocr", "send", true),
                new McpDomainPermission("runs/translation", "send", true)
            ]
        };
        McpServerHost server = new(context.Services, (_, _, _) => { });
        await server.StartAsync(settings);
        try
        {
            server.IsRunning.Should().BeTrue();
            using McpHttpClient client = new(server.Endpoint, null);
            await phases.RecordAsync("initialize", () => client.InitializeAsync());
            CliToolCall start = CliArguments.BuildToolCall("send",
            [
                "runs/ocr/control", "start", $"patchouli://texts/{context.DocumentId}/", "--args",
                """{"pages":"2","engine":"mock"}""", "--request-id", "http-cli"
            ], true);

            CliToolResponse accepted = await phases.RecordAsync("ocr.start",
                () => client.CallToolAsync(start.Tool, start.Arguments));

            accepted.IsError.Should().BeFalse(accepted.Text);
            accepted.ExitCode.Should().Be(0);
            using JsonDocument receipt = JsonDocument.Parse(accepted.Text);
            JsonElement acceptedAction = receipt.RootElement.GetProperty("entries")[0];
            acceptedAction.GetProperty("state").GetString().Should().Be("started");
            string statusUri = acceptedAction.GetProperty("run_uri").GetString()!;
            acceptedAction.GetProperty("result").GetProperty("status_uri").GetString().Should().Be(statusUri);
            await phases.RecordAsync("ocr.idle", async () =>
            {
                await context.Queue.ResumeAsync(OcrPauseScope.Global);
                await context.Queue.StartAsync();
                await context.Queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));
            });
            CliToolCall fetchStatus = CliArguments.BuildToolCall("fetch", [statusUri], true);
            CliToolResponse status = await phases.RecordAsync("ocr.fetch.status",
                () => client.CallToolAsync(fetchStatus.Tool, fetchStatus.Arguments));
            status.IsError.Should().BeFalse(status.Text);
            using JsonDocument statusEnvelope = JsonDocument.Parse(status.Text);
            string statusContent =
                statusEnvelope.RootElement.GetProperty("entries")[0].GetProperty("content").GetString()!;
            using JsonDocument projection = JsonDocument.Parse(statusContent.TrimStart().StartsWith('{')
                ? statusContent
                : McpToonCodec.DecodeToJson(statusContent));
            projection.RootElement.GetProperty("state").GetString().Should().Be("succeeded");
            projection.RootElement.GetProperty("recognition_state").GetString().Should().Be(OcrRunState.Completed);
            projection.RootElement.GetProperty("committed").GetBoolean().Should().BeTrue();
            projection.RootElement.GetProperty("committed_page_count").GetInt32().Should().Be(1);
            projection.RootElement.GetProperty("index_state").GetString().Should().Be("indexed");
            CliToolCall fetchText =
                CliArguments.BuildToolCall("fetch", [$"patchouli://texts/{context.DocumentId}/page-2.xml"], true);
            CliToolResponse body = await phases.RecordAsync("ocr.fetch.body",
                () => client.CallToolAsync(fetchText.Tool, fetchText.Arguments));
            body.IsError.Should().BeFalse(body.Text);
            using JsonDocument bodyEnvelope = JsonDocument.Parse(body.Text);
            bodyEnvelope.RootElement.GetProperty("entries")[0].GetProperty("content").GetString()
                .Should().Contain("<box");

            CliToolCall translationStart = CliArguments.BuildToolCall("send",
            [
                "runs/translation/control", "start", $"texts/{context.DocumentId}/", "--pages", "2",
                "--target-language", "zh-CN", "--force", "false", "--request-id", "http-translation"
            ], true);
            CliToolResponse translationAccepted = await phases.RecordAsync("translation.start",
                () => client.CallToolAsync(translationStart.Tool, translationStart.Arguments));
            translationAccepted.IsError.Should().BeFalse(translationAccepted.Text);
            using JsonDocument translationReceipt = JsonDocument.Parse(translationAccepted.Text);
            string translationStatus = translationReceipt.RootElement.GetProperty("entries")[0].GetProperty("run_uri")
                .GetString()!;
            CliToolResponse translationReplay = await phases.RecordAsync("translation.replay",
                () => client.CallToolAsync(translationStart.Tool, translationStart.Arguments));
            translationReplay.IsError.Should().BeFalse(translationReplay.Text);
            using JsonDocument replayReceipt = JsonDocument.Parse(translationReplay.Text);
            replayReceipt.RootElement.GetProperty("entries")[0].GetProperty("result").GetProperty("reused").GetBoolean()
                .Should().BeTrue();
            replayReceipt.RootElement.GetProperty("entries")[0].GetProperty("run_uri").GetString().Should()
                .Be(translationStatus);
            CliToolCall translationFetch = CliArguments.BuildToolCall("fetch", [translationStatus], true);
            CliToolResponse translationFetched = await phases.RecordAsync("translation.fetch",
                () => client.CallToolAsync(translationFetch.Tool, translationFetch.Arguments));
            translationFetched.IsError.Should().BeFalse(translationFetched.Text);
            CliToolCall translationCancel = CliArguments.BuildToolCall("send",
                ["runs/translation/control", "cancel", translationStatus[..^"status".Length]], true);
            CliToolResponse translationCancelled = await phases.RecordAsync("translation.cancel",
                () => client.CallToolAsync(translationCancel.Tool, translationCancel.Arguments));
            translationCancelled.IsError.Should().BeFalse(translationCancelled.Text);
            using JsonDocument cancelReceipt = JsonDocument.Parse(translationCancelled.Text);
            cancelReceipt.RootElement.GetProperty("entries")[0].GetProperty("result")
                .GetProperty("state").GetString().Should().Be("cancelled");

            phases.Names.Should().Equal(
                "initialize", "ocr.start", "ocr.idle", "ocr.fetch.status", "ocr.fetch.body",
                "translation.start", "translation.replay", "translation.fetch", "translation.cancel");
        }
        finally
        {
            await server.StopAsync();
        }
    }

    /// <summary>
    ///     Records the wall-clock duration of each real HTTP/CLI phase in a run scenario so the
    ///     accepted/background split stays observable: receipt phases must not hide the engine
    ///     initialization that now runs on the background run task.
    /// </summary>
    private sealed class PhaseLog
    {
        private readonly List<(string Name, TimeSpan Elapsed)> _entries = [];

        public IReadOnlyList<string> Names => _entries.Select(entry => entry.Name).ToArray();

        public async Task RecordAsync(string name, Func<Task> operation)
        {
            await RecordAsync(name, async () =>
            {
                await operation().ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }

        public async Task<T> RecordAsync<T>(string name, Func<Task<T>> operation)
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            T result = await operation().ConfigureAwait(false);
            watch.Stop();
            _entries.Add((name, watch.Elapsed));
            Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"PHASE {name} {watch.Elapsed.TotalMilliseconds:0.##} ms"));
            return result;
        }
    }

    [Fact]
    public async Task Import_alone_does_not_enqueue_ocr_and_explicit_subset_preserves_selected_page_ids()
    {
        await using Context context = await Context.CreateAsync();
        (await context.Queue.ListTasksAsync(new OcrQueueTaskFilter())).Value.Should().BeEmpty();

        Result<McpOcrCommandResult> started = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, "3,1", "mock", "subset"));

        started.IsSuccess.Should().BeTrue(started.ErrorMessage);
        OcrQueueTask task = (await context.Queue.GetTaskAsync(OcrQueueTaskId.Parse(started.Value.TaskId))).Value;
        task.TaskKind.Should().NotBe(OcrQueueTaskKind.Document, "the document executor ignores selected PageIds");
        IReadOnlyList<Page> pages = (await context.Services.Pages.ListPagesAsync(context.DocumentId)).Value;
        task.PageIds.Should().BeEquivalentTo([pages[0].PageId, pages[2].PageId]);
        started.Value.EngineId.Should().Be(OcrEngineIds.Mock);
        started.Value.PresetId.Should().Be(context.Preset.PresetId.ToString());
        started.Value.PresetVersionId.Should().Be(
            (await context.Services.OcrPresets.GetCurrentVersionAsync(context.Preset.PresetId)).Value.PresetVersionId
            .ToString());
        started.Value.StatusUri.Should().Be($"patchouli://runs/ocr/{task.TaskId}/status");
    }

    [Fact]
    public async Task Default_engine_resolves_configured_host_engine_and_whole_document_scope()
    {
        await using Context context = await Context.CreateAsync();
        Result<McpOcrCommandResult> result =
            await context.Services.OcrCommands.StartAsync(new McpOcrStartRequest(context.DocumentId));
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.EngineId.Should().Be(OcrEngineIds.Mock);
        result.Value.PresetId.Should().Be(context.Preset.PresetId.ToString());
        OcrQueueTask task = (await context.Queue.GetTaskAsync(OcrQueueTaskId.Parse(result.Value.TaskId))).Value;
        task.PageIds.Should().BeEquivalentTo(
            (await context.Services.Pages.ListPagesAsync(context.DocumentId)).Value.Select(page => page.PageId));
        result.Value.PresetVersionId.Should().Be(
            (await context.Services.OcrPresets.GetCurrentVersionAsync(context.Preset.PresetId)).Value.PresetVersionId
            .ToString());
    }

    [Fact]
    public async Task Missing_configured_default_does_not_silently_fall_back_to_available_mock()
    {
        await using Context context = await Context.CreateAsync("");
        Result<McpOcrCommandResult> result =
            await context.Services.OcrCommands.StartAsync(new McpOcrStartRequest(context.DocumentId));
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().NotBeNullOrWhiteSpace();
        (await context.Queue.ListTasksAsync(new OcrQueueTaskFilter())).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Accepted_task_executes_resolved_preset_version_after_current_preset_changes()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion original =
            (await context.Services.OcrPresets.GetCurrentVersionAsync(context.Preset.PresetId)).Value;
        Result<McpOcrCommandResult> accepted = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, "2", "mock", "pinned-version"));
        accepted.IsSuccess.Should().BeTrue(accepted.ErrorMessage);
        accepted.Value.PresetVersionId.Should().Be(original.PresetVersionId.ToString());
        Result<OcrPresetVersion> changed = await context.Services.OcrPresets.CreatePresetVersionAsync(
            context.Preset.PresetId,
            OcrEngineIds.Mock, original.ModelId, original.ModelPath, "{\"independent_version\":2}", false);
        changed.IsSuccess.Should().BeTrue(changed.ErrorMessage);
        changed.Value.PresetVersionId.Should().NotBe(original.PresetVersionId);

        await context.Queue.ResumeAsync(OcrPauseScope.Global);
        await context.Queue.StartAsync();
        await context.Queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));

        OcrQueueTask task = (await context.Queue.GetTaskAsync(OcrQueueTaskId.Parse(accepted.Value.TaskId))).Value;
        task.State.Should().Be(OcrQueueTaskState.Succeeded, task.LastErrorMessage);
        task.RunId.Should().NotBeNull();
        OcrRun actual = (await context.Services.Ocr.GetRunAsync(task.RunId!.Value)).Value;
        actual.PresetVersionId.Should().Be(original.PresetVersionId,
            "the accepted receipt must describe the version actually executed");
    }

    [Fact]
    public async Task Restart_drops_volatile_task_identity_and_old_task_operations_are_not_found()
    {
        await using Context context = await Context.CreateAsync();
        Result<McpOcrCommandResult> accepted = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "mock", "volatile"));
        accepted.IsSuccess.Should().BeTrue(accepted.ErrorMessage);
        OcrQueueTaskId previousTask = OcrQueueTaskId.Parse(accepted.Value.TaskId);

        await context.RestartAsync();

        (await context.Queue.GetTaskAsync(previousTask)).IsFailure.Should().BeTrue();
        Result<McpOcrCommandResult> cancelled = await context.Services.OcrCommands.CancelAsync(previousTask);
        cancelled.IsFailure.Should().BeTrue();
        cancelled.ErrorCode.Should().Be(AppErrorCodes.NotFound);
        (await context.Queue.ListTasksAsync(new OcrQueueTaskFilter())).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Concurrent_identical_request_ids_enqueue_exactly_one_task()
    {
        await using Context context = await Context.CreateAsync();
        Task<Result<McpOcrCommandResult>>[] submissions = Enumerable.Range(0, 32).Select(_ =>
                context.Services.OcrCommands.StartAsync(new McpOcrStartRequest(context.DocumentId, "1,3", "mock",
                    "atomic")))
            .ToArray();

        Result<McpOcrCommandResult>[] results = await Task.WhenAll(submissions);

        results.Should().OnlyContain(result => result.IsSuccess);
        results.Select(result => result.Value.TaskId).Distinct().Should().ContainSingle();
        results.Count(result => !result.Value.Reused).Should().Be(1);
        (await context.Queue.ListTasksAsync(new OcrQueueTaskFilter())).Value.Should().ContainSingle();
    }

    [Fact]
    public async Task Concurrent_different_requests_sharing_identity_have_one_winner_and_no_duplicate_queue_entries()
    {
        await using Context context = await Context.CreateAsync();
        IMcpOcrCommandApi commands = context.Services.OcrCommands;
        DocumentInstanceId documentId = context.DocumentId;
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Result<McpOcrCommandResult>>[] submissions = Enumerable.Range(0, 32).Select(index => Task.Run(async () =>
        {
            await gate.Task;
            return await commands.StartAsync(new McpOcrStartRequest(documentId,
                index % 2 == 0 ? "1" : "3", "mock", "racing-conflict"));
        })).ToArray();
        gate.SetResult();

        Result<McpOcrCommandResult>[] results = await Task.WhenAll(submissions);

        results.Count(result => result.IsSuccess).Should().Be(16);
        results.Count(result => result.IsFailure).Should().Be(16);
        results.Where(result => result.IsFailure).Should().OnlyContain(result =>
            result.ErrorCode != null && result.ErrorCode.Contains("conflict", StringComparison.OrdinalIgnoreCase));
        results.Where(result => result.IsSuccess).Select(result => result.Value.TaskId).Distinct().Should()
            .ContainSingle();
        (await context.Queue.ListTasksAsync(new OcrQueueTaskFilter())).Value.Should().ContainSingle();
    }

    [Fact]
    public async Task Canonical_page_sets_reuse_identity_and_different_page_sets_conflict()
    {
        await using Context context = await Context.CreateAsync();
        Result<McpOcrCommandResult> first = await context.Services.OcrCommands.StartAsync(new McpOcrStartRequest(
            context.DocumentId,
            "3,1,1", "mock", "canonical"));
        first.IsSuccess.Should().BeTrue(first.ErrorMessage);

        Result<McpOcrCommandResult> replay = await context.Services.OcrCommands.StartAsync(new McpOcrStartRequest(
            context.DocumentId,
            "1,3", "mock", "canonical"));
        Result<McpOcrCommandResult> conflict = await context.Services.OcrCommands.StartAsync(new McpOcrStartRequest(
            context.DocumentId,
            "2", "mock", "canonical"));

        replay.IsSuccess.Should().BeTrue(replay.ErrorMessage);
        replay.Value.TaskId.Should().Be(first.Value.TaskId);
        replay.Value.Reused.Should().BeTrue();
        conflict.IsFailure.Should().BeTrue();
        conflict.ErrorCode.Should().NotBeNullOrWhiteSpace();
        (await context.Queue.ListTasksAsync(new OcrQueueTaskFilter())).Value.Should().ContainSingle();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("0")]
    [InlineData("4")]
    [InlineData("1,4")]
    [InlineData("3-1")]
    [InlineData("1-")]
    [InlineData("one")]
    [InlineData("1,,2")]
    [InlineData("2147483647")]
    [InlineData("1-2147483647")]
    [InlineData("2147483648")]
    public async Task Invalid_empty_or_out_of_bounds_ranges_never_enqueue(string pages)
    {
        await using Context context = await Context.CreateAsync();

        Result<McpOcrCommandResult> started = await context.Services.OcrCommands.StartAsync(new McpOcrStartRequest(
            context.DocumentId,
            pages, "mock", "invalid"));

        started.IsFailure.Should().BeTrue();
        started.ErrorCode.Should().NotBeNullOrWhiteSpace();
        (await context.Queue.ListTasksAsync(new OcrQueueTaskFilter())).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Accepted_work_survives_request_token_cancellation_and_finishes_on_host_queue()
    {
        await using Context context = await Context.CreateAsync();
        await context.Queue.StopAsync();
        await context.Queue.PauseAsync(OcrPauseScope.Global);
        using CancellationTokenSource request = new();
        Result<McpOcrCommandResult> started = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, "2", "mock", "disconnect"), request.Token);
        started.IsSuccess.Should().BeTrue(started.ErrorMessage);

        await request.CancelAsync();
        await context.Queue.ResumeAsync(OcrPauseScope.Global);
        await context.Queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));

        OcrQueueTask task = (await context.Queue.GetTaskAsync(OcrQueueTaskId.Parse(started.Value.TaskId))).Value;
        task.State.Should().Be(OcrQueueTaskState.Succeeded, task.LastErrorMessage);
        IReadOnlyList<Page> pages = (await context.Services.Pages.ListPagesAsync(context.DocumentId)).Value;
        (await context.Services.DocumentTrees.GetCurrentRevisionAsync(context.DocumentId, pages[1].PageId))
            .IsSuccess.Should().BeTrue();
        (await context.Services.DocumentTrees.GetCurrentRevisionAsync(context.DocumentId, pages[0].PageId))
            .IsFailure.Should().BeTrue();
        (await context.Services.DocumentTrees.GetCurrentRevisionAsync(context.DocumentId, pages[2].PageId))
            .IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Accepted_request_replay_returns_original_after_source_disappears_and_preset_changes()
    {
        await using Context context = await Context.CreateAsync();
        McpOcrStartRequest request = new(context.DocumentId, "1,3", "mock", "durable-receipt");
        Result<McpOcrCommandResult> first = await context.Services.OcrCommands.StartAsync(request);
        first.IsSuccess.Should().BeTrue(first.ErrorMessage);
        OcrPresetVersion version =
            (await context.Services.OcrPresets.GetCurrentVersionAsync(context.Preset.PresetId)).Value;
        (await context.Services.OcrPresets.CreatePresetVersionAsync(context.Preset.PresetId,
                OcrEngineIds.Mock, version.ModelId, version.ModelPath, "{\"changed\":true}", false)).IsSuccess.Should()
            .BeTrue();
        File.Delete(context.Source);

        Result<McpOcrCommandResult> replay = await context.Services.OcrCommands.StartAsync(request);

        replay.IsSuccess.Should().BeTrue(replay.ErrorMessage);
        replay.Value.TaskId.Should().Be(first.Value.TaskId);
        replay.Value.PresetVersionId.Should().Be(first.Value.PresetVersionId);
        replay.Value.Reused.Should().BeTrue();
        (await context.Queue.ListTasksAsync(new OcrQueueTaskFilter())).Value.Should().ContainSingle();
    }

    [Fact]
    public async Task Start_and_retry_cannot_reuse_the_same_request_identity_for_different_operations()
    {
        await using Context context = await Context.CreateAsync();
        Result<McpOcrCommandResult> accepted = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "mock", "global-request"));
        accepted.IsSuccess.Should().BeTrue(accepted.ErrorMessage);
        OcrQueueTaskId original = OcrQueueTaskId.Parse(accepted.Value.TaskId);
        (await context.Services.OcrCommands.CancelAsync(original)).IsSuccess.Should().BeTrue();

        Result<McpOcrCommandResult> retry = await context.Services.OcrCommands.RetryAsync(original, "global-request");

        retry.IsFailure.Should().BeTrue();
        retry.ErrorCode.Should().Contain("conflict");
        (await context.Queue.ListTasksAsync(new OcrQueueTaskFilter())).Value.Should().ContainSingle();
    }

    [Fact]
    public async Task Concurrent_retry_replays_create_one_queryable_successor_task()
    {
        await using Context context = await Context.CreateAsync();
        IMcpOcrCommandApi commands = context.Services.OcrCommands;
        Result<McpOcrCommandResult> accepted = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "mock", "start-for-retry"));
        accepted.IsSuccess.Should().BeTrue(accepted.ErrorMessage);
        OcrQueueTaskId original = OcrQueueTaskId.Parse(accepted.Value.TaskId);
        (await context.Services.OcrCommands.CancelAsync(original)).IsSuccess.Should().BeTrue();

        Result<McpOcrCommandResult>[] retries = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ =>
            commands.RetryAsync(original, "atomic-retry")));

        retries.Should().OnlyContain(result => result.IsSuccess);
        retries.Select(result => result.Value.TaskId).Distinct().Should().ContainSingle();
        retries[0].Value.TaskId.Should().NotBe(original.ToString());
        (await context.Queue.ListTasksAsync(new OcrQueueTaskFilter())).Value.Should().HaveCount(2);
    }

    [Fact]
    public async Task Explicit_cancel_and_retry_return_the_actual_queryable_task()
    {
        await using Context context = await Context.CreateAsync();
        Result<McpOcrCommandResult> started = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "mock", "cancel"));
        started.IsSuccess.Should().BeTrue(started.ErrorMessage);
        OcrQueueTaskId original = OcrQueueTaskId.Parse(started.Value.TaskId);

        Result<McpOcrCommandResult> cancelled = await context.Services.OcrCommands.CancelAsync(original);
        Result<McpOcrCommandResult> retried = await context.Services.OcrCommands.RetryAsync(original, "retry-request");

        cancelled.IsSuccess.Should().BeTrue(cancelled.ErrorMessage);
        cancelled.Value.TaskId.Should().Be(original.ToString());
        (await context.Queue.GetTaskAsync(original)).Value.State.Should().Be(OcrQueueTaskState.Cancelled);
        retried.IsSuccess.Should().BeTrue(retried.ErrorMessage);
        OcrQueueTask actual = (await context.Queue.GetTaskAsync(OcrQueueTaskId.Parse(retried.Value.TaskId))).Value;
        actual.TaskId.Should().NotBe(original);
        actual.State.Should().Be(OcrQueueTaskState.Queued);
        actual.RetryOfTaskId.Should().Be(original);
        retried.Value.StatusUri.Should().Be($"patchouli://runs/ocr/{actual.TaskId}/status");
    }

    [Fact]
    public async Task Missing_source_is_diagnosable_without_exposing_local_path()
    {
        await using Context context = await Context.CreateAsync();
        File.Delete(context.Source);

        Result<McpOcrCommandResult> result = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "mock", "missing-source"));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().NotBeNullOrWhiteSpace();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace().And.NotContain(context.Root).And
            .NotContain(context.Source);
        (await context.Queue.ListTasksAsync(new OcrQueueTaskFilter())).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Unknown_engine_fails_before_enqueue_without_falling_back_to_mock()
    {
        await using Context context = await Context.CreateAsync();

        Result<McpOcrCommandResult> result = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "unregistered-engine", "engine-missing"));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().NotBeNullOrWhiteSpace();
        result.ErrorMessage.Should().NotContain(context.Root);
        (await context.Queue.ListTasksAsync(new OcrQueueTaskFilter())).Value.Should().BeEmpty();
    }

    [Theory]
    [InlineData(OcrEnvironmentStatus.MissingModelPath, OcrRequiredAction.RebindModelPath)]
    [InlineData(OcrEnvironmentStatus.MissingCredential, OcrRequiredAction.ConfigureCredential)]
    public async Task Adapter_readiness_failures_preserve_diagnostic_code_and_sanitize_paths_and_secrets(string status,
        string action)
    {
        await using Context context = await Context.CreateAsync();
        context.Services.OcrAdapters.RegisterAdapter(new UnreadyAdapter(status, action));

        Result<McpOcrCommandResult> result = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "mock", "readiness"));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().NotBeNullOrWhiteSpace();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace().And.NotContain("FAKE_SECRET_INDEPENDENT")
            .And.NotContain("C:\\private\\models").And.NotContain("/private/cache");
        (await context.Queue.ListTasksAsync(new OcrQueueTaskFilter())).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Changed_source_is_rejected_before_enqueue()
    {
        await using Context context = await Context.CreateAsync();
        await File.AppendAllTextAsync(context.Source, "\n% changed after import\n");

        Result<McpOcrCommandResult> result = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "mock", "source-changed"));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().NotBeNullOrWhiteSpace();
        result.ErrorMessage.Should().NotContain(context.Root);
        (await context.Queue.ListTasksAsync(new OcrQueueTaskFilter())).Value.Should().BeEmpty();
    }

    private sealed class UnreadyAdapter(string status, string action) : IRealOcrAdapter
    {
        private readonly MockOcrAdapter _inner = new();
        public string EngineId => OcrEngineIds.Mock;
        public string DisplayName => "Independent unready adapter";
        public string Kind => OcrAdapterKind.Mock;

        public OcrEngineCapability GetCapability()
        {
            return _inner.GetCapability();
        }

        public Task<OcrEnvironmentCheckResult> CheckEnvironmentAsync(OcrPresetVersion presetVersion,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new OcrEnvironmentCheckResult(
                EngineId, presetVersion.ModelId, "C:\\private\\models", status, false,
                "Model unavailable: cannot load C:\\private\\models; cache=/private/cache; api_key=FAKE_SECRET_INDEPENDENT",
                action, []));
        }

        public Task<Result> ValidatePresetAsync(OcrPresetVersion presetVersion,
            CancellationToken cancellationToken = default)
        {
            return _inner.ValidatePresetAsync(presetVersion, cancellationToken);
        }

        public Task<Result> ValidateInputAsync(OcrInputDescriptor input, CancellationToken cancellationToken = default)
        {
            return _inner.ValidateInputAsync(input, cancellationToken);
        }

        public Task<Result<OcrEnginePageResult>> RunPageAsync(OcrInputDescriptor input, OcrPresetVersion presetVersion,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("An unready adapter must never run.");
        }
    }

    private sealed class Context(
        string root,
        string source,
        HostServices services,
        IOcrQueueScheduler queue,
        DocumentInstanceId documentId,
        OcrPreset preset,
        HostOcrCompositionOptions ocrComposition) : IAsyncDisposable
    {
        public string Root { get; } = root;
        public string Source { get; } = source;
        public HostServices Services { get; private set; } = services;
        public IOcrQueueScheduler Queue { get; private set; } = queue;
        public DocumentInstanceId DocumentId { get; } = documentId;
        public OcrPreset Preset { get; } = preset;
        public HostOcrCompositionOptions OcrComposition { get; } = ocrComposition;

        public static async Task<Context> CreateAsync(string configuredEngine = OcrEngineIds.Mock)
        {
            string root = Directory
                .CreateDirectory(Path.Combine(Path.GetTempPath(), $"ocr-command-independent-{Guid.NewGuid():N}"))
                .FullName;
            string source = Path.Combine(root, "source.pdf");
            File.Copy(
                TestPaths.FromRepositoryRoot("tests", "Patchouli.Tests", "Fixtures", "Pdf",
                    "real-three-page-sample.pdf"), source);
            string db = Path.Combine(root, "runtime.sqlite");
            PatchouliAppSettings settings = PatchouliAppSettings.Default();
            // These independent host-command tests exercise the deterministic mock engine and
            // adapter through explicit dependencies; product settings carry no mock switch.
            HostOcrCompositionOptions ocrComposition = new(new MockOcrEngine(), [new MockOcrAdapter()]);
            HostServices services = await HostServices.CreateAsync(db, settings with
            {
                OcrEngines = new OcrEnginesAppSettings(configuredEngine, configuredEngine, configuredEngine),
                Runtime = settings.Runtime with
                {
                    RuntimeDatabasePath = db, DefaultSyncRoot = Path.Combine(root, "sync"),
                    DefaultStagingRoot = Path.Combine(root, "staging"), LogDirectory = Path.Combine(root, "logs"),
                    FileSearchRoot = Path.Combine(root, "search")
                }
            }, Path.Combine(root, "settings.json"), ocrComposition: ocrComposition);
            (await services.Library.CreateLibraryAsync("Independent OCR public tests")).IsSuccess.Should().BeTrue();
            IOcrQueueScheduler queue = (await services.GetOcrQueueAsync()).Value;
            await queue.PauseAsync(OcrPauseScope.Global);
            PdfImportResult imported =
                await services.PdfImport.ImportPdfAsync(new PdfImportRequest(source, "Three physical pages", null, 3));
            imported.Success.Should().BeTrue(imported.ErrorMessage);
            OcrPreset preset = (await services.OcrPresets.CreatePresetAsync("Independent mock", null, OcrEngineIds.Mock,
                OcrModelIds.MockBasic, null, "{}", false)).Value;
            return new Context(root, source, services, queue,
                DocumentInstanceId.Parse(imported.CreatedDocumentInstanceId!), preset, ocrComposition);
        }

        public async Task RestartAsync()
        {
            string database = Services.RuntimeDatabasePath;
            PatchouliAppSettings settings = Services.StartupSettings;
            await Services.ShutdownAsync();
            Services = await HostServices.CreateAsync(database, settings, Path.Combine(Root, "settings.json"),
                ocrComposition: OcrComposition);
            Queue = (await Services.GetOcrQueueAsync()).Value;
            await Queue.PauseAsync(OcrPauseScope.Global);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.ShutdownAsync();
            TestTempFileCleanup.DeleteDirectoryWithRetry(Root);
        }
    }
}
