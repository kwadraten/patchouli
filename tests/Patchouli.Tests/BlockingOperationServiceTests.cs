using FluentAssertions;
using Patchouli.Core.Operations;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Operations;

namespace Patchouli.Tests;

public sealed class BlockingOperationServiceTests
{
    [Fact]
    public async Task Start_update_complete_and_log_entries_round_trip()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        BlockingOperationService service = new(database.ConnectionFactory, clock);

        Result<BlockingOperation> started = await service.StartAsync(
            BlockingOperationTypes.InitialRootScan,
            "file_search_root",
            "root-a",
            true,
            "Scanning library root.",
            1,
            3,
            ["Wait for scan completion"]);
        Result<BlockingOperation> updated = await service.UpdateProgressAsync(
            started.Value.OperationId,
            2,
            progressLabel: "Scanning PDF manifests.",
            nextActions: ["Keep window open"]);
        Result<BlockingOperationLogEntry> logEntry = await service.AddLogEntryAsync(
            started.Value.OperationId,
            BlockingOperationLogLevel.Warning,
            "Scan slowed down.",
            "Waiting on filesystem.");
        Result<BlockingOperation> completed = await service.CompleteAsync(
            started.Value.OperationId,
            "Scan completed.",
            []);
        Result<BlockingOperation> loaded = await service.GetAsync(started.Value.OperationId);
        Result<IReadOnlyList<BlockingOperationLogEntry>> logs =
            await service.GetLogEntriesAsync(started.Value.OperationId);
        Result<BlockingOperation> updateAfterComplete = await service.UpdateProgressAsync(started.Value.OperationId, 3);

        started.IsSuccess.Should().BeTrue();
        updated.IsSuccess.Should().BeTrue();
        logEntry.IsSuccess.Should().BeTrue();
        completed.IsSuccess.Should().BeTrue();
        loaded.IsSuccess.Should().BeTrue();
        logs.IsSuccess.Should().BeTrue();
        loaded.Value.Status.Should().Be(BlockingOperationStatus.Completed);
        loaded.Value.ProgressCurrent.Should().Be(2);
        loaded.Value.ProgressTotal.Should().Be(3);
        loaded.Value.ProgressLabel.Should().Be("Scan completed.");
        loaded.Value.NextActions.Should().BeEmpty();
        logs.Value.Select(value => value.Message).Should().ContainInOrder(
            "Blocking operation started.",
            "Scan slowed down.",
            "Blocking operation completed.");
        updateAfterComplete.IsFailure.Should().BeTrue();
        updateAfterComplete.ErrorCode.Should().Be("invalid_state");
    }

    [Fact]
    public async Task Fail_and_cancel_transitions_are_persisted_and_filterable()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        BlockingOperationService service = new(database.ConnectionFactory, clock);

        Result<BlockingOperation> failedCandidate = await service.StartAsync(
            BlockingOperationTypes.CslStyleInstall,
            BlockingOperationScopeTypes.CslStyle,
            "apa");
        Result<BlockingOperation> failed = await service.FailAsync(
            failedCandidate.Value.OperationId,
            "validation_failed",
            "CSL style content is required.",
            "CSL install blocked.",
            ["Retry style installation"]);

        Result<BlockingOperation> cancelDeniedCandidate = await service.StartAsync(
            BlockingOperationTypes.FileSearchRootScan,
            "file_search_root",
            "root-b");
        Result<BlockingOperation> cancelDenied = await service.CancelAsync(cancelDeniedCandidate.Value.OperationId);

        Result<BlockingOperation> cancellable = await service.StartAsync(
            BlockingOperationTypes.FileSearchRootScan,
            "file_search_root",
            "root-c",
            true,
            nextActions: ["Wait for scan completion"]);
        Result<BlockingOperation> cancelled = await service.CancelAsync(
            cancellable.Value.OperationId,
            "Scan cancelled by user.",
            ["Retry scan later"]);
        Result<IReadOnlyList<BlockingOperation>> failedOperations = await service.ListAsync(
            BlockingOperationStatus.Failed,
            BlockingOperationTypes.CslStyleInstall,
            BlockingOperationScopeTypes.CslStyle,
            "apa");
        Result<IReadOnlyList<BlockingOperationLogEntry>> cancelledLogs =
            await service.GetLogEntriesAsync(cancellable.Value.OperationId);
        Result<BlockingOperation> failAfterCancel = await service.FailAsync(
            cancellable.Value.OperationId,
            "should_not_transition",
            "Cancelled operations are terminal.");

        failed.IsSuccess.Should().BeTrue();
        failed.Value.Status.Should().Be(BlockingOperationStatus.Failed);
        failed.Value.FailureCode.Should().Be("validation_failed");
        failed.Value.FailureMessage.Should().Be("CSL style content is required.");
        failed.Value.NextActions.Should().ContainSingle().Which.Should().Be("Retry style installation");
        cancelDenied.IsFailure.Should().BeTrue();
        cancelDenied.ErrorCode.Should().Be("invalid_state");
        cancelled.IsSuccess.Should().BeTrue();
        cancelled.Value.Status.Should().Be(BlockingOperationStatus.Cancelled);
        cancelled.Value.NextActions.Should().ContainSingle().Which.Should().Be("Retry scan later");
        failedOperations.IsSuccess.Should().BeTrue();
        failedOperations.Value.Should().ContainSingle();
        failedOperations.Value.Single().OperationId.Should().Be(failedCandidate.Value.OperationId);
        cancelledLogs.IsSuccess.Should().BeTrue();
        cancelledLogs.Value.Select(value => value.Message).Should().Contain("Blocking operation cancelled.");
        failAfterCancel.IsFailure.Should().BeTrue();
        failAfterCancel.ErrorCode.Should().Be("invalid_state");
    }
}
