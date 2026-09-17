using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Ocr;

namespace Patchouli.Tests.S3Ocr;

public sealed class OcrAdoptionRegressionTests
{
    [Fact]
    public async Task Batch_adoption_atomically_commits_tree_revisions_page_results_and_candidate_adoption()
    {
        await using OcrPerfContext context = await OcrPerfContext.CreateAsync(2);
        OcrPreset preset = (await context.Presets.CreatePresetAsync(
            "Mock", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        OcrRun run = (await context.Engine.RunPresetOnDocumentAsync(
            context.Document.DocumentInstanceId, preset.PresetId)).Value;
        run.State.Should().Be(OcrRunState.Completed);

        // Pre-commit state verification
        int workingCount = await context.CountAsync(
            "select count(1) from document_tree_revisions where document_instance_id = @Doc and status = 'working';",
            new { Doc = context.Document.DocumentInstanceId.ToString() });
        workingCount.Should().Be(2);

        int committedCount = await context.CountAsync(
            "select count(1) from document_tree_revisions where document_instance_id = @Doc and status = 'committed';",
            new { Doc = context.Document.DocumentInstanceId.ToString() });
        committedCount.Should().Be(0);

        int docCommits = await context.CountAsync(
            "select count(1) from document_commits where document_instance_id = @Doc;",
            new { Doc = context.Document.DocumentInstanceId.ToString() });
        docCommits.Should().Be(0);

        int adoptions = await context.CountAsync(
            "select count(1) from ocr_candidate_adoptions where ocr_run_id = @Run;",
            new { Run = run.OcrRunId.ToString() });
        adoptions.Should().Be(0);

        List<OcrTaskStageProgress> reportedStages = [];
        Progress<OcrTaskStageProgress> progress = new(reportedStages.Add);

        Result<OcrCandidateCommit> commitResult = await context.Engine.CommitCandidateRunAsync(
            run.OcrRunId, progress: progress);
        commitResult.IsSuccess.Should().BeTrue(commitResult.ErrorMessage);

        // Post-commit state verification: atomic commit of all parts
        (await context.CountAsync(
            "select count(1) from document_tree_revisions where document_instance_id = @Doc and status = 'working';",
            new { Doc = context.Document.DocumentInstanceId.ToString() })).Should().Be(0);

        (await context.CountAsync(
            "select count(1) from document_tree_revisions where document_instance_id = @Doc and status = 'committed';",
            new { Doc = context.Document.DocumentInstanceId.ToString() })).Should().Be(2);

        (await context.CountAsync(
            "select count(1) from document_commits where document_instance_id = @Doc;",
            new { Doc = context.Document.DocumentInstanceId.ToString() })).Should().Be(1);

        (await context.CountAsync(
            "select count(1) from ocr_candidate_adoptions where ocr_run_id = @Run;",
            new { Run = run.OcrRunId.ToString() })).Should().Be(1);

        reportedStages.Should().Contain(p => p.Stage == OcrTaskStage.Adopting);
    }

    [Fact]
    public async Task Commit_is_idempotent_and_returns_same_adoption_record()
    {
        await using OcrPerfContext context = await OcrPerfContext.CreateAsync(2);
        OcrPreset preset = (await context.Presets.CreatePresetAsync(
            "Mock", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        OcrRun run = (await context.Engine.RunPresetOnDocumentAsync(
            context.Document.DocumentInstanceId, preset.PresetId)).Value;
        run.State.Should().Be(OcrRunState.Completed);

        Result<OcrCandidateCommit> firstCommit = await context.Engine.CommitCandidateRunAsync(run.OcrRunId);
        firstCommit.IsSuccess.Should().BeTrue(firstCommit.ErrorMessage);

        Result<OcrCandidateCommit> secondCommit = await context.Engine.CommitCandidateRunAsync(run.OcrRunId);
        secondCommit.IsSuccess.Should().BeTrue(secondCommit.ErrorMessage);

        secondCommit.Value.CommitId.Should().Be(firstCommit.Value.CommitId);
        secondCommit.Value.OcrRunId.Should().Be(firstCommit.Value.OcrRunId);

        // Verify only 1 document commit exists
        (await context.CountAsync(
            "select count(1) from document_commits where document_instance_id = @Doc;",
            new { Doc = context.Document.DocumentInstanceId.ToString() })).Should().Be(1);
    }

    [Fact]
    public async Task Conflicting_page_selection_on_recommit_fails_and_does_not_alter_commit()
    {
        await using OcrPerfContext context = await OcrPerfContext.CreateAsync(2);
        OcrPreset preset = (await context.Presets.CreatePresetAsync(
            "Mock", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        OcrRun run = (await context.Engine.RunPresetOnDocumentAsync(
            context.Document.DocumentInstanceId, preset.PresetId)).Value;
        run.State.Should().Be(OcrRunState.Completed);

        // First commit adopts all pages
        Result<OcrCandidateCommit> firstCommit = await context.Engine.CommitCandidateRunAsync(run.OcrRunId);
        firstCommit.IsSuccess.Should().BeTrue();

        // Re-committing with a conflicting page selection fails
        Result<OcrCandidateCommit> conflictCommit = await context.Engine.CommitCandidateRunAsync(
            run.OcrRunId, selectedPages: [context.Pages[0].PageId]);
        conflictCommit.IsFailure.Should().BeTrue();
        conflictCommit.ErrorCode.Should().Be(AppErrorCodes.Conflict);
    }

    [Fact]
    public async Task Atomic_rollback_on_failure_leaves_no_partial_commits_or_revisions()
    {
        await using OcrPerfContext context = await OcrPerfContext.CreateAsync(2);
        OcrPreset preset = (await context.Presets.CreatePresetAsync(
            "Mock", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        OcrRun run = (await context.Engine.RunPresetOnDocumentAsync(
            context.Document.DocumentInstanceId, preset.PresetId)).Value;
        run.State.Should().Be(OcrRunState.Completed);

        // Corrupt the second page result's working revision by marking it cancelled
        // so CommitWorkingRevisionsInTransactionAsync fails for it
        await using (SqliteConnection conn = context.OpenConnection())
        {
            await conn.OpenAsync();
            string? secondRev = await conn.ExecuteScalarAsync<string>(
                "select working_tree_revision_id from ocr_page_results where ocr_run_id = @Run and page_id = @Page;",
                new { Run = run.OcrRunId.ToString(), Page = context.Pages[1].PageId.ToString() });
            secondRev.Should().NotBeNull();
            await conn.ExecuteAsync(
                "update document_tree_revisions set status = 'discarded' where tree_revision_id = @Rev;",
                new { Rev = secondRev });
        }

        // Attempt to commit
        Result<OcrCandidateCommit> failedCommit = await context.Engine.CommitCandidateRunAsync(run.OcrRunId);
        failedCommit.IsFailure.Should().BeTrue();

        // Assert atomic rollback:
        // No document commit created
        (await context.CountAsync(
            "select count(1) from document_commits where document_instance_id = @Doc;",
            new { Doc = context.Document.DocumentInstanceId.ToString() })).Should().Be(0);

        // No candidate adoptions created
        (await context.CountAsync(
            "select count(1) from ocr_candidate_adoptions where ocr_run_id = @Run;",
            new { Run = run.OcrRunId.ToString() })).Should().Be(0);

        // Page 0's working revision was NOT left as committed
        (await context.CountAsync(
            "select count(1) from document_tree_revisions where document_instance_id = @Doc and status = 'committed';",
            new { Doc = context.Document.DocumentInstanceId.ToString() })).Should().Be(0);
    }

    [Fact]
    public async Task CancelRunAsync_clears_inbound_references_and_deletes_only_working_revisions_without_foreign_key_violation()
    {
        await using OcrPerfContext context = await OcrPerfContext.CreateAsync(2);
        OcrPreset preset = (await context.Presets.CreatePresetAsync(
            "Mock", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        OcrRun run = (await context.Engine.RunPresetOnDocumentAsync(
            context.Document.DocumentInstanceId, preset.PresetId)).Value;
        run.State.Should().Be(OcrRunState.Completed);

        // Transition run to running so it is cancellable
        await using (SqliteConnection conn = context.OpenConnection())
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "update ocr_runs set state = 'running' where ocr_run_id = @Run;",
                new { Run = run.OcrRunId.ToString() });
        }

        // Cancel the run
        Result cancelResult = await context.Engine.CancelRunAsync(run.OcrRunId);
        cancelResult.IsSuccess.Should().BeTrue(cancelResult.ErrorMessage);

        // Verify inbound references cleared
        (await context.CountAsync(
            "select count(1) from ocr_page_results where ocr_run_id = @Run and working_tree_revision_id is not null;",
            new { Run = run.OcrRunId.ToString() })).Should().Be(0);

        (await context.CountAsync(
            "select count(1) from ocr_runs where ocr_run_id = @Run and output_tree_revision_id is not null;",
            new { Run = run.OcrRunId.ToString() })).Should().Be(0);

        // Verify working revisions deleted
        (await context.CountAsync(
            "select count(1) from document_tree_revisions where document_instance_id = @Doc and status = 'working';",
            new { Doc = context.Document.DocumentInstanceId.ToString() })).Should().Be(0);
    }

    [Fact]
    public async Task ReconcileInterruptedRuns_cleans_up_working_revisions_without_deleting_committed_revisions()
    {
        await using OcrPerfContext context = await OcrPerfContext.CreateAsync(2);
        OcrPreset preset = (await context.Presets.CreatePresetAsync(
            "Mock", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;

        // Run 1 and commit it, establishing committed revisions on both pages
        OcrRun run1 = (await context.Engine.RunPresetOnDocumentAsync(
            context.Document.DocumentInstanceId, preset.PresetId)).Value;
        (await context.Engine.CommitCandidateRunAsync(run1.OcrRunId)).IsSuccess.Should().BeTrue();

        int committedBoxesBefore = await context.CountAsync(
            """
            select count(1)
            from document_boxes b
            join document_tree_revisions r on b.tree_revision_id = r.tree_revision_id
            where r.document_instance_id = @Doc and r.status = 'committed';
            """,
            new { Doc = context.Document.DocumentInstanceId.ToString() });
        committedBoxesBefore.Should().BeGreaterThan(0);

        // Run 2 starts
        OcrRun run2 = (await context.Engine.RunPresetOnDocumentAsync(
            context.Document.DocumentInstanceId, preset.PresetId)).Value;

        // Simulate crash/interruption: change run2 state to 'running'
        await using (SqliteConnection conn = context.OpenConnection())
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "update ocr_runs set state = 'running' where ocr_run_id = @Run;",
                new { Run = run2.OcrRunId.ToString() });
        }

        // Reconcile
        Result reconcileResult = await context.Engine.ReconcileInterruptedRunsAsync();
        reconcileResult.IsSuccess.Should().BeTrue(reconcileResult.ErrorMessage);

        // Committed revisions and boxes from Run 1 MUST be intact
        (await context.CountAsync(
            "select count(1) from document_tree_revisions where document_instance_id = @Doc and status = 'committed';",
            new { Doc = context.Document.DocumentInstanceId.ToString() })).Should().Be(2);

        int committedBoxesAfter = await context.CountAsync(
            """
            select count(1)
            from document_boxes b
            join document_tree_revisions r on b.tree_revision_id = r.tree_revision_id
            where r.document_instance_id = @Doc and r.status = 'committed';
            """,
            new { Doc = context.Document.DocumentInstanceId.ToString() });
        committedBoxesAfter.Should().Be(committedBoxesBefore);

        // Working revisions from Run 2 must be deleted
        (await context.CountAsync(
            "select count(1) from document_tree_revisions where document_instance_id = @Doc and status = 'working';",
            new { Doc = context.Document.DocumentInstanceId.ToString() })).Should().Be(0);
    }
}
