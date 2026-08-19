using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Ocr;

namespace Patchouli.Tests.S3Ocr;

public sealed class OcrAdoptionNotificationTests
{
    [Fact]
    public async Task Successful_commit_raises_exactly_one_post_commit_notification()
    {
        await using OcrPerfContext context = await OcrPerfContext.CreateAsync();
        OcrPreset preset = (await context.Presets.CreatePresetAsync(
            "Mock", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        List<OcrCommitCompletedEventArgs> events = [];
        context.Engine.CommitCompleted += (_, args) => events.Add(args);

        OcrRun run = (await context.Engine.RunPresetOnDocumentAsync(
            context.Document.DocumentInstanceId, preset.PresetId)).Value;
        run.State.Should().Be(OcrRunState.Completed);

        Result<OcrCandidateCommit> committed = await context.Engine.CommitCandidateRunAsync(run.OcrRunId);

        committed.IsSuccess.Should().BeTrue(committed.ErrorMessage);
        events.Should().HaveCount(1);
        events[0].DocumentInstanceId.Should().Be(context.Document.DocumentInstanceId);
        events[0].OcrRunId.Should().Be(run.OcrRunId);
        events[0].CommittedRevisionIds.Should().HaveCount(context.Pages.Count);

        // Re-committing the same completed run is deduplicated and must not notify again.
        Result<OcrCandidateCommit> again = await context.Engine.CommitCandidateRunAsync(run.OcrRunId);
        again.IsSuccess.Should().BeTrue(again.ErrorMessage);
        events.Should().HaveCount(1);
    }

    [Fact]
    public async Task Cancelled_commit_never_raises_notification_and_leaves_no_current_tree()
    {
        await using OcrPerfContext context = await OcrPerfContext.CreateAsync();
        OcrPreset preset = (await context.Presets.CreatePresetAsync(
            "Mock", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        OcrRun run = (await context.Engine.RunPresetOnDocumentAsync(
            context.Document.DocumentInstanceId, preset.PresetId)).Value;
        run.State.Should().Be(OcrRunState.Completed);
        List<OcrCommitCompletedEventArgs> events = [];
        context.Engine.CommitCompleted += (_, args) => events.Add(args);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> commit = () => context.Engine.CommitCandidateRunAsync(run.OcrRunId, cancellationToken: cts.Token);

        await commit.Should().ThrowAsync<OperationCanceledException>();
        events.Should().BeEmpty();
        foreach (Page page in context.Pages)
        {
            (await context.Trees.GetCurrentRevisionAsync(context.Document.DocumentInstanceId, page.PageId)).IsFailure
                .Should().BeTrue();
        }
    }
}
