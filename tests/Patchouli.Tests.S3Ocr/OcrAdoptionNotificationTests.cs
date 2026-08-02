using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Ocr;

namespace Patchouli.Tests.S3Ocr;

public sealed class OcrAdoptionNotificationTests
{
    [Fact]
    public async Task Successful_adoption_raises_exactly_one_post_commit_notification()
    {
        await using OcrPerfContext context = await OcrPerfContext.CreateAsync();
        OcrPreset preset = (await context.Presets.CreatePresetAsync(
            "Mock", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        List<OcrAdoptionCommittedEventArgs> events = [];
        context.Engine.AdoptionCommitted += (_, args) => events.Add(args);

        OcrRun run = (await context.Engine.RunPresetOnDocumentAsync(
            context.Document.DocumentInstanceId, preset.PresetId)).Value;
        run.State.Should().Be(OcrRunState.Completed);

        Result<OcrCandidateAdoption> adopted = await context.Engine.AdoptCandidateRunAsync(run.OcrRunId);

        adopted.IsSuccess.Should().BeTrue(adopted.ErrorMessage);
        events.Should().HaveCount(1);
        events[0].DocumentInstanceId.Should().Be(context.Document.DocumentInstanceId);
        events[0].AdoptedRevisionIds.Should().HaveCount(context.Pages.Count);

        // Re-adopting the same completed run is deduplicated and must not notify again.
        Result<OcrCandidateAdoption> again = await context.Engine.AdoptCandidateRunAsync(run.OcrRunId);
        again.IsSuccess.Should().BeTrue(again.ErrorMessage);
        events.Should().HaveCount(1);
    }

    [Fact]
    public async Task Cancelled_adoption_never_raises_notification_and_leaves_no_current_tree()
    {
        await using OcrPerfContext context = await OcrPerfContext.CreateAsync();
        OcrPreset preset = (await context.Presets.CreatePresetAsync(
            "Mock", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        OcrRun run = (await context.Engine.RunPresetOnDocumentAsync(
            context.Document.DocumentInstanceId, preset.PresetId)).Value;
        run.State.Should().Be(OcrRunState.Completed);
        List<OcrAdoptionCommittedEventArgs> events = [];
        context.Engine.AdoptionCommitted += (_, args) => events.Add(args);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> adopt = () => context.Engine.AdoptCandidateRunAsync(run.OcrRunId, cancellationToken: cts.Token);

        await adopt.Should().ThrowAsync<OperationCanceledException>();
        events.Should().BeEmpty();
        foreach (Page page in context.Pages)
        {
            (await context.Trees.GetCurrentRevisionAsync(context.Document.DocumentInstanceId, page.PageId)).IsFailure
                .Should().BeTrue();
        }
    }
}
