using FluentAssertions;
using Patchouli.Host.Settings;
using Patchouli.UI;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Infrastructure.Snapshots;

namespace Patchouli.Tests;

public sealed class AppSettingsCommitNotificationTests
{
    [Fact]
    public async Task Failed_and_cancelled_saves_do_not_publish_commit_notifications()
    {
        using TemporaryAppSettingsFile file = new();
        string invalidPath = Path.Combine(file.Path, "settings.json");
        await using AppSettingsStore failingStore = new(
            invalidPath, PatchouliAppSettings.Default(), TimeSpan.FromHours(1));
        bool failedSaveNotified = false;
        failingStore.SettingsCommitted += (_, _) => failedSaveNotified = true;
        failingStore.Update(settings => settings with
        {
            Sync = settings.Sync with { DeviceName = "must-fail" }
        }, "Sync");

        SettingsSaveResult failed = await failingStore.SaveImmediatelyAsync();

        failed.IsSuccess.Should().BeFalse();
        failedSaveNotified.Should().BeFalse();

        await using AppSettingsStore cancelledStore = new(
            file.Path, PatchouliAppSettings.Load(file.Path), TimeSpan.FromHours(1));
        bool cancelledSaveNotified = false;
        cancelledStore.SettingsCommitted += (_, _) => cancelledSaveNotified = true;
        cancelledStore.Update(settings => settings with
        {
            Sync = settings.Sync with { DeviceName = "must-cancel" }
        }, "Sync");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        CancellationToken cancellationToken = cancellation.Token;

        Func<Task> save = CreateSaveAction(cancelledStore.SaveImmediatelyAsync, cancellationToken);

        await save.Should()
            .ThrowAsync<OperationCanceledException>();
        cancelledSaveNotified.Should().BeFalse();
        cancelledStore.IsDirty.Should().BeTrue();
    }

    [Fact]
    public async Task Throwing_subscriber_does_not_fail_commit_or_skip_later_subscribers()
    {
        using TemporaryAppSettingsFile file = new();
        await using AppSettingsStore store = new(
            file.Path, PatchouliAppSettings.Load(file.Path), TimeSpan.FromHours(1));
        bool laterSubscriberNotified = false;
        store.SettingsCommitted += (_, _) => throw new InvalidOperationException("subscriber failed");
        store.SettingsCommitted += (_, change) =>
        {
            change.Categories.Should().Contain("Sync");
            change.Snapshot.Sync.DeviceName.Should().Be("committed");
            laterSubscriberNotified = true;
        };
        store.Update(settings => settings with
        {
            Sync = settings.Sync with { DeviceName = "committed" }
        }, "Sync");

        SettingsSaveResult result = await store.SaveImmediatelyAsync();

        result.IsSuccess.Should().BeTrue();
        laterSubscriberNotified.Should().BeTrue();
        PatchouliAppSettings.Load(file.Path).Sync.DeviceName.Should().Be("committed");
    }

    [Fact]
    public async Task Explicit_save_notification_can_synchronously_reenter_the_store()
    {
        using TemporaryAppSettingsFile file = new();
        await using AppSettingsStore store = new(
            file.Path, PatchouliAppSettings.Load(file.Path), TimeSpan.FromHours(1));
        using CancellationTokenSource reentryTimeout = new(TimeSpan.FromSeconds(5));
        CancellationToken reentryToken = reentryTimeout.Token;
        SettingsSaveResult? nestedResult = null;
        Exception? nestedError = null;
        int notifications = 0;
        store.SettingsCommitted += (sender, _) =>
        {
            if (Interlocked.Increment(ref notifications) != 1)
            {
                return;
            }

            if (sender is not AppSettingsStore reentrantStore)
            {
                throw new InvalidOperationException("Expected the settings store as the event sender.");
            }

            reentrantStore.Update(settings => settings with
            {
                Sync = settings.Sync with { DeviceName = "reentered" }
            }, "Sync");
            try
            {
                nestedResult = reentrantStore.SaveImmediatelyAsync(reentryToken).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                nestedError = exception;
            }
        };
        store.Update(settings => settings with
        {
            Sync = settings.Sync with { DeviceName = "first-commit" }
        }, "Sync");

        SettingsSaveResult result = await store.SaveImmediatelyAsync();

        result.IsSuccess.Should().BeTrue();
        nestedError.Should().BeNull();
        nestedResult.Should().NotBeNull();
        nestedResult!.IsSuccess.Should().BeTrue();
        notifications.Should().Be(2);
        PatchouliAppSettings.Load(file.Path).Sync.DeviceName.Should().Be("reentered");
    }

    [Fact]
    public async Task Successful_special_binding_save_keeps_store_current_equal_to_disk()
    {
        using TemporaryAppSettingsFile file = new();
        PatchouliAppSettings initial = PatchouliAppSettings.Load(file.Path);
        await using AppSettingsStore store = new(file.Path, initial, TimeSpan.FromHours(1));
        LibraryId libraryId = LibraryId.New();
        string deviceId = Guid.NewGuid().ToString("D");
        DeviceRootBindingAppSettings binding = new(
            libraryId.ToString(), LogicalRootKinds.SyncRoot, "sync-root", deviceId,
            file.CreateDatabasePath("sync-root"), "settings_ui", true,
            SnapshotState: SnapshotSyncLocalState.NotConfigured,
            SyncedSettingKeys: ["metadata_lookup"]);
        SyncAppSettings sync = initial.Sync with
        {
            DeviceId = deviceId,
            DeviceName = "Committed device",
            SyncRoot = binding.LocalPath,
            SyncRootId = binding.LogicalRootId,
            DeviceBindings = [binding],
            SyncedSettingKeys = ["metadata_lookup"]
        };

        SettingsSaveResult result = await store.UpdateSyncBindingAsync(sync, binding);

        result.IsSuccess.Should().BeTrue();
        store.IsDirty.Should().BeFalse();
        store.Current.Sync.DeviceName.Should().Be("Committed device");
        store.Current.Sync.Bindings.Should().ContainSingle().Which.Should().BeEquivalentTo(
            PatchouliAppSettings.Load(file.Path).Sync.Bindings.Single());
        store.Current.Sync.Should().BeEquivalentTo(PatchouliAppSettings.Load(file.Path).Sync);
    }

    [Fact]
    public async Task Cancelled_special_binding_save_does_not_leave_sync_dirty_or_claim_binding_was_saved()
    {
        using TemporaryAppSettingsFile file = new();
        PatchouliAppSettings initial = PatchouliAppSettings.Load(file.Path);
        await using AppSettingsStore store = new(file.Path, initial, TimeSpan.FromHours(1));
        LibraryId libraryId = LibraryId.New();
        string deviceId = Guid.NewGuid().ToString("D");
        DeviceRootBindingAppSettings binding = new(
            libraryId.ToString(), LogicalRootKinds.SyncRoot, "cancelled-sync-root", deviceId,
            file.CreateDatabasePath("cancelled-sync-root"), "settings_ui", true,
            SnapshotState: SnapshotSyncLocalState.NotConfigured,
            SyncedSettingKeys: []);
        SyncAppSettings sync = initial.Sync with
        {
            DeviceId = deviceId,
            SyncRoot = binding.LocalPath,
            SyncRootId = binding.LogicalRootId,
            DeviceBindings = [binding]
        };
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        CancellationToken cancellationToken = cancellation.Token;

        Func<Task> save = CreateSyncBindingSaveAction(
            store.UpdateSyncBindingAsync, sync, binding, cancellationToken);

        await save.Should()
            .ThrowAsync<OperationCanceledException>();
        store.IsDirty.Should().BeFalse();
        store.Current.Sync.Bindings.Should().BeEmpty();
        PatchouliAppSettings.Load(file.Path).Sync.Bindings.Should().BeEmpty();
    }

    private static Func<Task> CreateSaveAction(
        Func<CancellationToken, Task<SettingsSaveResult>> saveAsync,
        CancellationToken cancellationToken)
    {
        return () => saveAsync(cancellationToken);
    }

    private static Func<Task> CreateSyncBindingSaveAction(
        Func<SyncAppSettings, DeviceRootBindingAppSettings, CancellationToken, Task<SettingsSaveResult>> saveAsync,
        SyncAppSettings settings,
        DeviceRootBindingAppSettings binding,
        CancellationToken cancellationToken)
    {
        return () => saveAsync(settings, binding, cancellationToken);
    }
}
