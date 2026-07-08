using FluentAssertions;
using Patchouli.Core.Operations;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Operations;

namespace Patchouli.Tests;

public sealed class BlockingOperationServiceTests
{
    [Fact]
    public async Task Start_update_complete_and_log_entries_round_trip()
    {
        await using var database = TemporarySqliteDatabase.Create();
        var clock = new FixedClock(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        var service = new BlockingOperationService(database.ConnectionFactory, clock);

        var started = await service.StartAsync(
            BlockingOperationTypes.InitialRootScan,
            "file_search_root",
            "root-a",
            canCancel: true,
            progressLabel: "Scanning library root.",
            progressCurrent: 1,
            progressTotal: 3,
            nextActions: ["Wait for scan completion"]);
        var updated = await service.UpdateProgressAsync(
            started.Value.OperationId,
            progressCurrent: 2,
            progressLabel: "Scanning PDF manifests.",
            nextActions: ["Keep window open"]);
        var logEntry = await service.AddLogEntryAsync(
            started.Value.OperationId,
            BlockingOperationLogLevel.Warning,
            "Scan slowed down.",
            "Waiting on filesystem.");
        var completed = await service.CompleteAsync(
            started.Value.OperationId,
            "Scan completed.",
            []);
        var loaded = await service.GetAsync(started.Value.OperationId);
        var logs = await service.GetLogEntriesAsync(started.Value.OperationId);
        var updateAfterComplete = await service.UpdateProgressAsync(started.Value.OperationId, progressCurrent: 3);

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
        await using var database = TemporarySqliteDatabase.Create();
        var clock = new FixedClock(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        var service = new BlockingOperationService(database.ConnectionFactory, clock);

        var failedCandidate = await service.StartAsync(
            BlockingOperationTypes.CslStyleInstall,
            BlockingOperationScopeTypes.CslStyle,
            "apa");
        var failed = await service.FailAsync(
            failedCandidate.Value.OperationId,
            "validation_failed",
            "CSL style content is required.",
            "CSL install blocked.",
            ["Retry style installation"]);

        var cancelDeniedCandidate = await service.StartAsync(
            BlockingOperationTypes.FileSearchRootScan,
            "file_search_root",
            "root-b");
        var cancelDenied = await service.CancelAsync(cancelDeniedCandidate.Value.OperationId);

        var cancellable = await service.StartAsync(
            BlockingOperationTypes.FileSearchRootScan,
            "file_search_root",
            "root-c",
            canCancel: true,
            nextActions: ["Wait for scan completion"]);
        var cancelled = await service.CancelAsync(
            cancellable.Value.OperationId,
            "Scan cancelled by user.",
            ["Retry scan later"]);
        var failedOperations = await service.ListAsync(
            status: BlockingOperationStatus.Failed,
            operationType: BlockingOperationTypes.CslStyleInstall,
            scopeType: BlockingOperationScopeTypes.CslStyle,
            scopeId: "apa");
        var cancelledLogs = await service.GetLogEntriesAsync(cancellable.Value.OperationId);
        var failAfterCancel = await service.FailAsync(
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
