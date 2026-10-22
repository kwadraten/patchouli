using FluentAssertions;
using Patchouli.Core.Import;
using Patchouli.Infrastructure.Import;

namespace Patchouli.Tests;

/// <summary>
/// Issue #30, plan test item 7 (registry level): the shared path-claim registry
/// admits exactly one holder per normalized path. Explicit imports wait for the
/// holder; background scanners use the non-blocking try-claim and get nothing
/// while a path is owned; multi-path claims are all-or-nothing, so a failed or
/// cancelled attempt never leaves a partial registration behind.
/// </summary>
public sealed class FileOperationCoordinatorIssue30Tests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static string PathFor(string name) =>
        Path.Combine(Path.GetTempPath(), $"issue30-{Guid.NewGuid():N}", name);

    [Fact]
    public async Task TryClaim_fails_while_another_lease_holds_the_path_and_succeeds_after_release()
    {
        FileOperationCoordinator coordinator = new();
        string path = PathFor("held.pdf");

        await using IFileOperationLease holder = await coordinator.ClaimAsync([path]);
        coordinator.IsClaimed(path).Should().BeTrue();

        IFileOperationLease? attempt = coordinator.TryClaim([path]);
        attempt.Should().BeNull("a second importer must never enter the same path concurrently");

        await holder.DisposeAsync();
        coordinator.IsClaimed(path).Should().BeFalse();
        await using IFileOperationLease? second = coordinator.TryClaim([path]);
        second.Should().NotBeNull();
    }

    [Fact]
    public async Task ClaimAsync_waits_for_the_current_holder_instead_of_entering_concurrently()
    {
        FileOperationCoordinator coordinator = new();
        string path = PathFor("waited.pdf");

        await using IFileOperationLease holder = await coordinator.ClaimAsync([path]);
        Task<IFileOperationLease> waiting = coordinator.ClaimAsync([path]);
        waiting.IsCompleted.Should().BeFalse("the second claim must wait while the first holds");

        await holder.DisposeAsync();
        await using IFileOperationLease lease = await waiting.WaitAsync(Timeout);
        lease.Paths.Should().Contain(Path.GetFullPath(path));
        coordinator.TryClaim([path]).Should().BeNull("the woken waiter is now the sole holder");
    }

    [Fact]
    public async Task Multi_path_try_claim_is_all_or_nothing()
    {
        FileOperationCoordinator coordinator = new();
        string heldPath = PathFor("a.pdf");
        string freePath = PathFor("b.pdf");

        await using IFileOperationLease holder = await coordinator.ClaimAsync([heldPath]);

        IFileOperationLease? attempt = coordinator.TryClaim([heldPath, freePath]);

        attempt.Should().BeNull();
        coordinator.IsClaimed(freePath).Should().BeFalse(
            "a failed all-or-nothing attempt registers none of its paths");
    }

    [Fact]
    public async Task Cancelled_claim_wait_registers_nothing()
    {
        FileOperationCoordinator coordinator = new();
        string path = PathFor("cancelled.pdf");

        await using IFileOperationLease holder = await coordinator.ClaimAsync([path]);
        using CancellationTokenSource cancelling = new();
        Task<IFileOperationLease> waiting = coordinator.ClaimAsync([path], cancelling.Token);
        cancelling.Cancel();

        Func<Task> act = () => waiting;
        await act.Should().ThrowAsync<OperationCanceledException>();

        await holder.DisposeAsync();
        coordinator.IsClaimed(path).Should().BeFalse(
            "the cancelled waiter did not sneak a registration in while waiting");
        await using IFileOperationLease? lease = coordinator.TryClaim([path]);
        lease.Should().NotBeNull();
    }

    [Fact]
    public async Task Paths_are_normalized_before_matching()
    {
        FileOperationCoordinator coordinator = new();
        string directory = Path.Combine(Path.GetTempPath(), $"issue30-{Guid.NewGuid():N}");
        string canonical = Path.Combine(directory, "file.pdf");
        string withDetour = Path.Combine(directory, "sub", "..", "file.pdf");

        await using IFileOperationLease holder = await coordinator.ClaimAsync([withDetour]);

        coordinator.IsClaimed(canonical).Should().BeTrue(
            "claims key on the normalized absolute path, so spelling variants collide");
        coordinator.TryClaim([canonical]).Should().BeNull();
    }

    [Fact]
    public async Task Lease_extension_waits_for_the_added_path_only()
    {
        FileOperationCoordinator coordinator = new();
        string firstPath = PathFor("first.pdf");
        string secondPath = PathFor("second.pdf");

        await using IFileOperationLease lease = await coordinator.ClaimAsync([firstPath]);
        await using IFileOperationLease blocker = await coordinator.ClaimAsync([secondPath]);

        Task extending = lease.AddAsync([secondPath]);
        extending.IsCompleted.Should().BeFalse("the added path is owned by someone else");
        coordinator.IsClaimed(firstPath).Should().BeTrue("previously held paths stay claimed during the wait");

        await blocker.DisposeAsync();
        await extending.WaitAsync(Timeout);
        lease.Paths.Should().Contain(Path.GetFullPath(firstPath))
            .And.Contain(Path.GetFullPath(secondPath));
    }
}
