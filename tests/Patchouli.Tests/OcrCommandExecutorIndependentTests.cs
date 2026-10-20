using System.Reflection;
using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Search;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class OcrCommandExecutorIndependentTests
{
    [Fact]
    public async Task Partial_recognition_failure_never_adopts_successful_pages_or_builds_index()
    {
        Harness harness = new();
        harness.Pages = [harness.Page(OcrPageResultState.Succeeded), harness.Page(OcrPageResultState.Failed)];

        OcrQueueExecutionResult result = await harness.Executor.ExecuteAsync(harness.Task, default);

        result.Succeeded.Should().BeFalse();
        result.RunId.Should().Be(harness.Run.OcrRunId);
        result.CompletedPageCount.Should().Be(1);
        result.FailedPageCount.Should().Be(1);
        harness.Calls.Should().Equal("recognize", "pages");
    }

    [Fact]
    public async Task Commit_conflict_preserves_recognition_but_does_not_start_indexing()
    {
        Harness harness = new();
        harness.CommitResult = Result<OcrCandidateCommit>.Failure("revision_conflict", "A manual draft exists.");

        OcrQueueExecutionResult result = await harness.Executor.ExecuteAsync(harness.Task, default);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("revision_conflict");
        result.RunId.Should().Be(harness.Run.OcrRunId);
        result.CompletedPageCount.Should().Be(1);
        harness.Calls.Should().Equal("recognize", "pages", "commit");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Index_failure_is_reported_after_successful_commit_without_repeating_recognition(bool failUnits)
    {
        Harness harness = new();
        harness.UnitResult = failUnits ? Result.Failure("units_failed", "units unavailable") : Result.Success();
        harness.IndexResult = Result.Failure("fts_failed", "index unavailable");

        OcrQueueExecutionResult result = await harness.Executor.ExecuteAsync(harness.Task, default);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(failUnits ? "units_failed" : "fts_failed");
        result.RunId.Should().Be(harness.Run.OcrRunId);
        result.CompletedPageCount.Should().Be(1);
        harness.Calls.Should().Equal(failUnits
            ? ["recognize", "pages", "commit", "units"]
            : ["recognize", "pages", "commit", "units", "fts"]);
    }

    [Fact]
    public async Task Successful_queue_execution_orders_recognition_commit_and_index()
    {
        Harness harness = new();

        OcrQueueExecutionResult result = await harness.Executor.ExecuteAsync(harness.Task, default);

        result.Succeeded.Should().BeTrue();
        result.Cancelled.Should().BeFalse();
        harness.Calls.Should().Equal("recognize", "pages", "commit", "units", "fts");
    }

    [Fact]
    public async Task Recognition_error_stops_before_page_results_commit_and_index()
    {
        Harness harness = new();
        harness.Engine.Handler = (method, _) => method == nameof(IOcrRunEngine.RunPresetOnDocumentAsync)
            ? Task.FromResult(Result<OcrRun>.Failure("ocr_engine_failure", "Engine rejected input."))
            : throw new NotSupportedException(method);

        OcrQueueExecutionResult result = await harness.Executor.ExecuteAsync(harness.Task, default);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("ocr_engine_failure");
        result.RunId.Should().BeNull();
        harness.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Retry_with_completed_recognition_reuses_run_before_commit_and_index()
    {
        Harness harness = new();

        OcrQueueExecutionResult result = await harness.Executor.ExecuteAsync(
            harness.Task with { RunId = harness.Run.OcrRunId }, default);

        result.Succeeded.Should().BeTrue();
        result.RunId.Should().Be(harness.Run.OcrRunId);
        harness.Calls.Should().Equal("run", "pages", "pages", "commit", "units", "fts");
    }

    [Fact]
    public async Task Cancelled_recognition_does_not_commit_or_index()
    {
        Harness harness = new();
        harness.Engine.Handler = (_, _) => throw new OperationCanceledException();

        OcrQueueExecutionResult result = await harness.Executor.ExecuteAsync(harness.Task, default);

        result.Succeeded.Should().BeFalse();
        result.Cancelled.Should().BeTrue();
        harness.Calls.Should().BeEmpty();
    }

    private sealed class Harness
    {
        public readonly List<string> Calls = [];
        public readonly Probe<IOcrRunEngine> Engine = Probe<IOcrRunEngine>.Create();
        public readonly OcrRun Run;
        public readonly OcrQueueTask Task;
        public readonly OcrQueueTaskExecutor Executor;
        public IReadOnlyList<OcrPageResult> Pages;
        public Result<OcrCandidateCommit> CommitResult;
        public Result UnitResult = Result.Success();
        public Result IndexResult = Result.Success();

        public Harness()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Run = new OcrRun(OcrRunId.New(), DocumentInstanceId.New(), OcrPresetId.New(),
                OcrPresetVersionId.New(), "test-real-adapter", "test-model", "{}", null, null, null,
                OcrRunState.Completed, now, now);
            Pages = [Page(OcrPageResultState.Succeeded)];
            CommitResult = Result<OcrCandidateCommit>.Success(new OcrCandidateCommit(OcrCandidateAdoptionId.New(),
                Run.OcrRunId, Run.DocumentInstanceId, [DocumentTreeRevisionId.New()], "[1]", now));
            Task = new OcrQueueTask(OcrQueueTaskId.New(), LibraryId.New(), Run.DocumentInstanceId, Run.PresetId,
                Pages.Select(page => page.PageId).ToArray(), OcrQueueTaskKind.Document, Run.EngineId,
                OcrAdapterKind.LocalLibrary, null, OcrQueuePriority.UserStartedDocument, now, now,
                OcrQueueTaskState.Running, 1, 1, null, null, null, null, null, null);
            Engine.Handler = (method, _) => method switch
            {
                nameof(IOcrRunEngine.GetRunAsync) => Record("run", Result<OcrRun>.Success(Run)),
                nameof(IOcrRunEngine.RunPresetOnDocumentAsync) => Record("recognize", Result<OcrRun>.Success(Run)),
                nameof(IOcrRunEngine.ListPageResultsAsync) => Record("pages",
                    Result<IReadOnlyList<OcrPageResult>>.Success(Pages)),
                nameof(IOcrRunEngine.CommitCandidateRunAsync) => Record("commit", CommitResult),
                _ => throw new NotSupportedException(method)
            };
            Probe<ISearchUnitBuilder> units = Probe<ISearchUnitBuilder>.Create();
            units.Handler = (method, _) => method == nameof(ISearchUnitBuilder.RebuildForDocumentInstanceAsync)
                ? Record("units", UnitResult)
                : throw new NotSupportedException(method);
            Probe<ISearchIndexRebuilder> index = Probe<ISearchIndexRebuilder>.Create();
            index.Handler = (method, _) => method == nameof(ISearchIndexRebuilder.RebuildFtsForDocumentInstanceAsync)
                ? Record("fts", IndexResult)
                : throw new NotSupportedException(method);
            Executor = new OcrQueueTaskExecutor(Engine.Value, units.Value, index.Value);
        }

        public OcrPageResult Page(string state)
        {
            return new OcrPageResult(OcrPageResultId.New(), Run.OcrRunId, PageId.New(), state,
                DocumentTreeRevisionId.New(), state == OcrPageResultState.Failed ? "page_failure" : null,
                state == OcrPageResultState.Failed ? "Recognition failed." : null, DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
        }

        private Task<T> Record<T>(string call, T value)
        {
            Calls.Add(call);
            return System.Threading.Tasks.Task.FromResult(value);
        }
    }

    private class Probe<T> : DispatchProxy where T : class
    {
        public Func<string, object?[], object?> Handler { get; set; } = (_, _) => throw new NotSupportedException();
        public T Value => (T)(object)this;

        public static Probe<T> Create()
        {
            return (Probe<T>)(object)Create<T, Probe<T>>();
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return Handler(targetMethod!.Name, args ?? []);
        }
    }
}
