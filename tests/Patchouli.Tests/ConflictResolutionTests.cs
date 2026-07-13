using FluentAssertions;
using Patchouli.Core.Conflicts;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Conflicts;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.Tests;

public sealed class ConflictResolutionTests
{
    [Fact]
    public void Executor_transition_rejects_unknown_actions_and_missing_required_options()
    {
        ConflictDescriptor descriptor = ConflictDescriptorMapper.FileRelocationMultipleCandidates(
            FileAssetId.New(),
            "C:\\library\\missing.pdf",
            [
                new FileResolutionCandidate("C:\\library\\candidate.pdf", 12, null, "quick", "full",
                    FileResolutionConfidence.Exact, "search_root")
            ]);

        Result<ConflictDescriptor> unknown = ConflictResolutionTransitions.ValidateSelection(descriptor,
            new ConflictActionSelection("delete_everything"));
        Result<ConflictDescriptor> missingOption = ConflictResolutionTransitions.ValidateSelection(descriptor,
            new ConflictActionSelection("choose_candidate"));
        Result<ConflictDescriptor> selected = ConflictResolutionTransitions.ValidateSelection(descriptor,
            new ConflictActionSelection("choose_candidate", "C:\\library\\candidate.pdf"));

        unknown.ErrorCode.Should().Be("conflict_action_unknown");
        missingOption.ErrorCode.Should().Be("conflict_option_required");
        selected.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Warning_can_be_ignored_but_blocking_conflict_cannot()
    {
        ConflictDescriptor warning = ConflictDescriptorMapper.SourceFileChanged(
            FileAssetId.New(), "C:\\library\\old.pdf", [], "Source changed.");
        ConflictDescriptor blocking = ConflictDescriptorMapper.LayoutBBoxOrdinaryOverlap(
            PageId.New().ToString(), LayoutNodeId.New().ToString(), "paragraph",
            new Core.Layout.NormalizedBBox(0.1, 0.1, 0.2, 0.2), "block",
            new Core.Layout.NormalizedBBox(0.15, 0.15, 0.2, 0.2));

        ConflictResolutionTransitions.Ignore(warning, "keep_old_evidence").ResolutionStatus
            .Should().Be(ConflictResolutionStatus.Ignored);
        Action ignoreBlocking = () => ConflictResolutionTransitions.Ignore(blocking, "skip_candidate");
        ignoreBlocking.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Dialog_exposes_severity_and_disables_option_actions_until_a_candidate_is_selected()
    {
        ConflictDescriptor descriptor = ConflictDescriptorMapper.FileRelocationMultipleCandidates(
            FileAssetId.New(),
            "C:\\library\\missing.pdf",
            [
                new FileResolutionCandidate("C:\\library\\candidate.pdf", 12, null, "quick", "full",
                    FileResolutionConfidence.Exact, "search_root")
            ]);

        ConflictResolutionDialogViewModel dialog = new(descriptor);
        ConflictDialogActionViewModel choose = dialog.Actions.Single(action => action.ActionId == "choose_candidate");

        dialog.Severity.Should().Be(ConflictSeverity.Blocking);
        choose.IsEnabled.Should().BeFalse();
        dialog.SelectedOption = dialog.Options.Single();
        choose.IsEnabled.Should().BeTrue();
    }
}
