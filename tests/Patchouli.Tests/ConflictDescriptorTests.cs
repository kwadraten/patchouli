using FluentAssertions;
using Patchouli.Core.Conflicts;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Infrastructure.Conflicts;
using Patchouli.Core.Results;

namespace Patchouli.Tests;

public sealed class ConflictDescriptorTests
{
    [Fact]
    public void Conflict_codes_are_stable_and_known()
    {
        ConflictCode.IsKnown(ConflictCode.SameIdDifferentContent).Should().BeTrue();
        ConflictCode.IsKnown(ConflictCode.PrimaryDocumentConflict).Should().BeTrue();
        ConflictCode.IsKnown(ConflictCode.CredentialNotImported).Should().BeTrue();
        ConflictCode.IsKnown(ConflictCode.FileRelocationMultipleCandidates).Should().BeTrue();
        ConflictCode.IsKnown(ConflictCode.SourceFileChangedOrBBoxBasisStale).Should().BeTrue();
        ConflictCode.IsKnown(ConflictCode.LayoutBBoxOrdinaryOverlap).Should().BeTrue();
        ConflictCode.IsKnown(ConflictCode.BiblatexItemFieldConflict).Should().BeTrue();
        ConflictCode.IsKnown(ConflictCode.BiblatexBatchLinkCandidates).Should().BeTrue();
    }

    [Fact]
    public void Same_item_content_conflict_uses_blocking_snapshot_shape()
    {
        ConflictDescriptor descriptor = ConflictDescriptorMapper.SameItemDifferentContent(
            ItemId.New(),
            "Existing",
            "book",
            "Incoming",
            "report");

        descriptor.Domain.Should().Be(ConflictDomain.SnapshotSync);
        descriptor.Severity.Should().Be(ConflictSeverity.Blocking);
        descriptor.ConflictCode.Should().Be(ConflictCode.SameIdDifferentContent);
        descriptor.RecommendedActions.Select(action => action.ActionId).Should().BeEquivalentTo(
            "keep_local", "import_as_new_item", "skip");
        descriptor.ResolutionStatus.Should().Be(ConflictResolutionStatus.Unresolved);
    }

    [Fact]
    public void File_candidate_conflict_uses_choose_candidate_action()
    {
        ConflictDescriptor descriptor = ConflictDescriptorMapper.FileRelocationMultipleCandidates(
            FileAssetId.New(),
            "C:\\library\\missing.pdf",
            [
                new FileResolutionCandidate("C:\\roots\\a\\missing.pdf", 12, null, "hash-a", null,
                    FileResolutionConfidence.Exact, "search_root")
            ]);

        descriptor.Domain.Should().Be(ConflictDomain.FileResolution);
        descriptor.ConflictCode.Should().Be(ConflictCode.FileRelocationMultipleCandidates);
        descriptor.RecommendedActions.Should().ContainSingle(action => action.ActionId == "choose_candidate");
    }

    [Fact]
    public void Document_box_overlap_conflict_uses_cf06_and_adjust_bbox_action()
    {
        ConflictDescriptor descriptor = ConflictDescriptorMapper.DocumentBoxBBoxOrdinaryOverlap(
            PageId.New().ToString(),
            DocumentBoxId.New().ToString(),
            DocumentBoxType.Text,
            new NormalizedBBox(0.1, 0.1, 0.2, 0.2),
            DocumentBoxType.Title,
            new NormalizedBBox(0.15, 0.15, 0.2, 0.2));

        descriptor.Domain.Should().Be(ConflictDomain.LayoutEdit);
        descriptor.ConflictCode.Should().Be(ConflictCode.LayoutBBoxOrdinaryOverlap);
        descriptor.RecommendedActions.Should().ContainSingle(action => action.ActionId == "adjust_bbox");
        descriptor.RecommendedActions.Select(action => action.ActionId).Should().BeEquivalentTo(
            "adjust_bbox", "change_to_allowed_type", "skip_candidate");
    }

    [Fact]
    public void Primary_document_conflict_offers_only_non_destructive_import_choices()
    {
        ConflictDescriptor descriptor = ConflictDescriptorMapper.PrimaryDocumentConflict(
            ItemId.New(),
            DocumentInstanceId.New(),
            DocumentInstanceId.New());

        descriptor.RecommendedActions.Select(action => action.ActionId).Should().BeEquivalentTo(
            "keep_local_with_incoming_secondary",
            "keep_local_without_incoming");
    }

    [Fact]
    public void Changed_source_conflict_exposes_all_explicit_evidence_preserving_actions()
    {
        ConflictDescriptor descriptor = ConflictDescriptorMapper.SourceFileChanged(
            FileAssetId.New(),
            "C:\\library\\old.pdf",
            [],
            "The source file changed.");

        descriptor.RecommendedActions.Select(action => action.ActionId).Should().BeEquivalentTo(
            "rebind_source",
            "confirm_changed_file",
            "reuse_revision_for_new_fingerprint",
            "keep_old_evidence");
    }

    [Fact]
    public async Task Document_box_executor_runs_only_non_destructive_cf06_actions()
    {
        bool skipped = false;
        DocumentBoxConflictActionExecutor executor = new(
            _ => Task.FromResult(Result.Success()),
            (_, _) => Task.FromResult(Result.Success()),
            _ =>
            {
                skipped = true;
                return Task.FromResult(Result.Success());
            });
        ConflictDescriptor descriptor = ConflictDescriptorMapper.DocumentBoxBBoxOrdinaryOverlap(
            PageId.New().ToString(), DocumentBoxId.New().ToString(), DocumentBoxType.Text,
            new NormalizedBBox(0.1, 0.1, 0.2, 0.2), DocumentBoxType.Title,
            new NormalizedBBox(0.15, 0.15, 0.2, 0.2));

        Result<ConflictExecutionResult> result = await executor.ExecuteAsync(descriptor,
            new ConflictActionSelection("skip_candidate"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Descriptor.ResolutionStatus.Should().Be(ConflictResolutionStatus.Resolved);
        skipped.Should().BeTrue();
    }

    [Fact]
    public void Executor_registry_rejects_duplicate_conflict_codes()
    {
        DocumentBoxConflictActionExecutor first = NoOpBoxExecutor();
        DocumentBoxConflictActionExecutor second = NoOpBoxExecutor();

        Action create = () => new ConflictActionExecutorRegistry([first, second]);

        create.Should().Throw<ArgumentException>();
    }

    private static DocumentBoxConflictActionExecutor NoOpBoxExecutor()
    {
        return new DocumentBoxConflictActionExecutor(
            _ => Task.FromResult(Result.Success()),
            (_, _) => Task.FromResult(Result.Success()),
            _ => Task.FromResult(Result.Success()));
    }
}
