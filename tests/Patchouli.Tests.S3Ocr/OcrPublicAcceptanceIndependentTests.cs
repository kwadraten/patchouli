using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Search;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Ocr;

namespace Patchouli.Tests.S3Ocr;

public sealed class OcrPublicAcceptanceIndependentTests
{
    [Fact]
    public async Task Page_execution_recognizes_and_commits_only_requested_physical_page()
    {
        await using OcrPerfContext context = await OcrPerfContext.CreateAsync(3);
        OcrPreset preset = await Preset(context);

        Result<OcrRun> run = await context.Engine.RunPresetOnPagesAsync(context.Document.DocumentInstanceId,
            preset.PresetId, [context.Pages[1].PageId]);

        run.IsSuccess.Should().BeTrue(run.ErrorMessage);
        (await context.Engine.ListPageResultsAsync(run.Value.OcrRunId)).Value
            .Should().ContainSingle(page => page.PageId == context.Pages[1].PageId);
        (await context.Engine.CommitCandidateRunAsync(run.Value.OcrRunId)).IsSuccess.Should().BeTrue();
        (await context.Trees.GetCurrentRevisionAsync(context.Document.DocumentInstanceId, context.Pages[1].PageId))
            .IsSuccess.Should().BeTrue();
        foreach (Page page in new[] { context.Pages[0], context.Pages[2] })
        {
            (await context.Trees.GetCurrentRevisionAsync(context.Document.DocumentInstanceId, page.PageId))
                .IsFailure.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Manual_draft_created_after_recognition_blocks_adoption_and_remains_editable()
    {
        await using OcrPerfContext context = await OcrPerfContext.CreateAsync(1);
        OcrPreset preset = await Preset(context);
        OcrRun run = (await context.Engine.RunPresetOnDocumentAsync(context.Document.DocumentInstanceId,
            preset.PresetId)).Value;
        PageEditSession draft = (await context.Trees.BeginPageEditAsync(context.Document.DocumentInstanceId,
            context.Pages[0].PageId)).Value;
        (await context.Trees.DrawAndInsertLeafAsync(draft.SessionId, new InsertLeafCommand(null, null,
            DocumentBoxType.Text, null, new NormalizedBBox(0, 0, 1, 1),
            new TextBoxPayload("Independent handwritten correction")))).IsSuccess.Should().BeTrue();

        Result<OcrCandidateCommit> commit = await context.Engine.CommitCandidateRunAsync(run.OcrRunId);

        commit.IsFailure.Should().BeTrue("OCR must not silently replace an active manual draft");
        commit.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        (await context.Trees.ListBoxesAsync(draft.DraftRevisionId)).Value.Should().ContainSingle()
            .Which.Payload.Should().Be(new TextBoxPayload("Independent handwritten correction"));
        (await context.Trees.GetCurrentRevisionAsync(context.Document.DocumentInstanceId, context.Pages[0].PageId))
            .IsFailure.Should().BeTrue();
        (await context.Trees.CommitPageEditAsync(draft.SessionId)).IsSuccess.Should().BeTrue();
        (await context.Trees.GetCurrentRevisionAsync(context.Document.DocumentInstanceId, context.Pages[0].PageId))
            .Value.TreeRevisionId.Should().Be(draft.DraftRevisionId);
    }

    [Fact]
    public async Task Manual_edit_committed_after_recognition_rejects_stale_ocr_candidate()
    {
        await using OcrPerfContext context = await OcrPerfContext.CreateAsync(1);
        OcrPreset preset = await Preset(context);
        OcrRun run = (await context.Engine.RunPresetOnDocumentAsync(context.Document.DocumentInstanceId,
            preset.PresetId)).Value;
        PageEditSession draft = (await context.Trees.BeginPageEditAsync(context.Document.DocumentInstanceId,
            context.Pages[0].PageId)).Value;
        (await context.Trees.DrawAndInsertLeafAsync(draft.SessionId, new InsertLeafCommand(null, null,
            DocumentBoxType.Text, null, new NormalizedBBox(0, 0, 1, 1),
            new TextBoxPayload("Saved after OCR recognition")))).IsSuccess.Should().BeTrue();
        (await context.Trees.CommitPageEditAsync(draft.SessionId)).IsSuccess.Should().BeTrue();

        Result<OcrCandidateCommit> commit = await context.Engine.CommitCandidateRunAsync(run.OcrRunId);

        commit.IsFailure.Should().BeTrue();
        commit.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        (await context.Trees.GetCurrentRevisionAsync(context.Document.DocumentInstanceId, context.Pages[0].PageId))
            .Value.TreeRevisionId.Should().Be(draft.DraftRevisionId);
        (await context.Trees.ListBoxesAsync(draft.DraftRevisionId)).Value.Should().ContainSingle()
            .Which.Payload.Should().Be(new TextBoxPayload("Saved after OCR recognition"));
    }

    [Fact]
    public async Task Fts_failure_leaves_adopted_body_and_recognition_records_in_database()
    {
        await using OcrPerfContext context = await OcrPerfContext.CreateAsync(2);
        OcrPreset preset = await Preset(context);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        OcrQueueTask task = new(OcrQueueTaskId.New(), LibraryId.New(), context.Document.DocumentInstanceId,
            preset.PresetId, context.Pages.Select(page => page.PageId).ToArray(), OcrQueueTaskKind.Document,
            OcrEngineIds.Mock, OcrAdapterKind.Mock, null, OcrQueuePriority.UserStartedDocument, now, now,
            OcrQueueTaskState.Running, 1, 1, null, null, null, null, null, null);
        OcrQueueTaskExecutor executor = new(context.Engine, context.Units, new FailingIndex());

        OcrQueueExecutionResult result = await executor.ExecuteAsync(task, default);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("test_fts_failure");
        result.RunId.Should().NotBeNull();
        (await context.Engine.ListPageResultsAsync(result.RunId!.Value)).Value.Should().HaveCount(2)
            .And.OnlyContain(page => page.State == OcrPageResultState.Succeeded);
        foreach (Page page in context.Pages)
        {
            Result<DocumentTreeRevision> current =
                await context.Trees.GetCurrentRevisionAsync(context.Document.DocumentInstanceId, page.PageId);
            current.IsSuccess.Should().BeTrue(current.ErrorMessage);
            (await context.Trees.ListBoxesAsync(current.Value.TreeRevisionId)).Value.Should().NotBeEmpty();
        }

        (await context.CountAsync("select count(*) from ocr_candidate_adoptions;")).Should().Be(1);
        (await context.CountAsync("select count(*) from document_commits;")).Should().Be(1);
    }

    private static async Task<OcrPreset> Preset(OcrPerfContext context)
    {
        return (await context.Presets.CreatePresetAsync("Independent mock fixture", null,
            OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
    }

    private sealed class FailingIndex : ISearchIndexRebuilder
    {
        public Task<Result> RebuildFtsForDocumentInstanceAsync(DocumentInstanceId documentInstanceId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Failure("test_fts_failure", "Injected index outage."));
        }

        public Task<Result> EnsureCacheAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
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
}
