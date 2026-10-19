using FluentAssertions;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Settings;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.UI;

namespace Patchouli.Tests;

public sealed class AppSettingsWriteCoordinationTests
{
    [Fact]
    public async Task Binding_updates_keep_each_other_and_preserve_other_settings()
    {
        using TemporaryAppSettingsFile file = new();
        string deviceId = Guid.NewGuid().ToString("D");
        LibraryId firstLibrary = LibraryId.New();
        LibraryId secondLibrary = LibraryId.New();
        PatchouliAppSettings initial = PatchouliAppSettings.Default() with
        {
            Sync = PatchouliAppSettings.Default().Sync with { DeviceId = deviceId }
        };
        initial.Save(file.Path).IsSuccess.Should().BeTrue();
        JsonObject settingsJson = JsonNode.Parse(await File.ReadAllTextAsync(file.Path))!.AsObject();
        settingsJson["ExtensionRoot"] = new JsonObject { ["Kept"] = true };
        settingsJson["Sync"]!["ExtensionSync"] = "kept";
        await File.WriteAllTextAsync(file.Path, settingsJson.ToJsonString());

        string firstDatabasePath = file.CreateDatabasePath("first");
        string secondDatabasePath = file.CreateDatabasePath("second");
        AppSettingsDeviceRootBindingStore store = new(file.Path);
        await Task.WhenAll(
            Task.Run(() =>
                store.SaveBindingAsync(NewBinding(firstLibrary, deviceId, firstDatabasePath))),
            Task.Run(() =>
                store.SaveBindingAsync(NewBinding(secondLibrary, deviceId, secondDatabasePath))));

        PatchouliAppSettings persisted = PatchouliAppSettings.Load(file.Path);
        persisted.Sync.Bindings.Select(binding => binding.LibraryId).Should()
            .BeEquivalentTo([firstLibrary.ToString(), secondLibrary.ToString()]);
        persisted.Ui.ReadingFontFamily.Should().Be(initial.Ui.ReadingFontFamily);
        persisted.Llm.ChatProviderId.Should().Be(initial.Llm.ChatProviderId);
        JsonObject persistedJson = JsonNode.Parse(await File.ReadAllTextAsync(file.Path))!.AsObject();
        persistedJson["ExtensionRoot"]!["Kept"]!.GetValue<bool>().Should().BeTrue();
        persistedJson["Sync"]!["ExtensionSync"]!.GetValue<string>().Should().Be("kept");
    }

    [Fact]
    public async Task Sync_settings_can_create_a_binding_and_switch_to_a_new_root()
    {
        using TemporaryAppSettingsFile file = new();
        LibraryId libraryId = LibraryId.New();
        string deviceId = Guid.NewGuid().ToString("D");
        SyncAppSettings sync = PatchouliAppSettings.Default().Sync with { DeviceId = deviceId };
        PatchouliAppSettings.Default().Save(file.Path).IsSuccess.Should().BeTrue();

        DeviceRootBindingAppSettings firstBinding = NewBindingRow(libraryId, deviceId, "root-a",
            file.CreateDatabasePath("sync-a"));
        SyncAppSettings firstDraft = sync with
        {
            SyncRoot = firstBinding.LocalPath,
            SyncRootId = firstBinding.LogicalRootId
        };
        (await PatchouliAppSettings.SaveSyncBindingAsync(file.Path, firstDraft, firstBinding)).IsSuccess
            .Should().BeTrue();

        DeviceRootBindingAppSettings secondBinding = NewBindingRow(libraryId, deviceId, "root-b",
            file.CreateDatabasePath("sync-b"));
        SyncAppSettings secondDraft = firstDraft with
        {
            SyncRoot = secondBinding.LocalPath,
            SyncRootId = secondBinding.LogicalRootId
        };
        (await PatchouliAppSettings.SaveSyncBindingAsync(file.Path, secondDraft, secondBinding)).IsSuccess
            .Should().BeTrue();

        PatchouliAppSettings persisted = PatchouliAppSettings.Load(file.Path);
        persisted.Sync.Bindings.Should().Contain(binding => binding.LogicalRootId == "root-a");
        persisted.Sync.Bindings.Should().Contain(binding => binding.LogicalRootId == "root-b");
        persisted.Sync.SyncRootId.Should().Be("root-b");
        persisted.Sync.SyncRoot.Should().Be(Path.GetFullPath(secondBinding.LocalPath));
    }

    [Fact]
    public async Task Sync_binding_can_materialize_metadata_lookup_in_the_same_narrow_write()
    {
        using TemporaryAppSettingsFile file = new();
        LibraryId libraryId = LibraryId.New();
        string deviceId = Guid.NewGuid().ToString("D");
        PatchouliAppSettings initial = PatchouliAppSettings.Default() with
        {
            Sync = PatchouliAppSettings.Default().Sync with { DeviceId = deviceId }
        };
        initial.Save(file.Path).IsSuccess.Should().BeTrue();

        MetadataLookupAppSettings materialized = new(
        [
            new MetadataSourcePreference("crossref", false),
            new MetadataSourcePreference("open-library", true)
        ]);
        DeviceRootBindingAppSettings binding = NewBindingRow(libraryId, deviceId, "root-metadata",
                file.CreateDatabasePath("metadata-sync")) with
            {
                SyncedSettingKeys = []
            };
        SyncAppSettings desiredSync = initial.Sync with
        {
            SyncRoot = binding.LocalPath,
            SyncRootId = binding.LogicalRootId,
            SyncMetadataLookup = false,
            SyncedSettingKeys = []
        };

        SettingsSaveResult result = await PatchouliAppSettings.SaveSyncBindingAsync(
            file.Path, desiredSync, binding, materializedMetadataLookup: materialized);

        result.IsSuccess.Should().BeTrue();
        PatchouliAppSettings persisted = PatchouliAppSettings.Load(file.Path);
        persisted.Sync.Bindings.Should().Contain(bindingRow => bindingRow.LogicalRootId == "root-metadata");
        persisted.Sync.IsSettingEnabled(LibrarySettingKeys.MetadataLookup, libraryId).Should().BeFalse();
        persisted.MetadataLookup.Sources.Should()
            .BeEquivalentTo(MetadataLookupAppSettings.MergeWithDefaults(materialized.Sources).Sources);
    }

    [Fact]
    public async Task Narrow_sync_update_leaves_a_malformed_settings_file_untouched()
    {
        using TemporaryAppSettingsFile file = new();
        const string malformed = "{\"Patchouli\": {";
        await File.WriteAllTextAsync(file.Path, malformed);

        SettingsSaveResult result = await PatchouliAppSettings.UpdateSyncJsonAsync(file.Path, sync =>
        {
            sync["DeviceName"] = "must not replace the file";
            return true;
        });

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("settings_json_invalid");
        (await File.ReadAllTextAsync(file.Path)).Should().Be(malformed);
    }

    [Fact]
    public async Task Snapshot_state_save_without_a_binding_leaves_settings_untouched()
    {
        using TemporaryAppSettingsFile file = new();
        string databasePath = file.CreateDatabasePath("unmapped-library");
        await CreateRuntimeDatabaseAsync(databasePath, LibraryId.New());
        string before = await File.ReadAllTextAsync(file.Path);
        SnapshotSyncSettingsStore store = new(databasePath, file.Path, file.CreateDatabasePath("staging"));

        Result result = await store.SaveLocalStateAsync(new SnapshotSyncLocalState(
            null, null, null, null, SnapshotSyncOperationState.Ready, null, DateTimeOffset.UtcNow));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.MappingRequired);
        (await File.ReadAllTextAsync(file.Path)).Should().Be(before);
    }

    [Fact]
    public async Task Stale_ui_saves_keep_checkpoint_and_do_not_resurrect_a_deleted_binding()
    {
        using TemporaryAppSettingsFile file = new();
        string databasePath = file.CreateDatabasePath("library");
        LibraryId libraryId = LibraryId.New();
        await CreateRuntimeDatabaseAsync(databasePath, libraryId);
        string deviceId = Guid.NewGuid().ToString("D");
        DeviceRootBindingAppSettings binding = new(
            libraryId.ToString(), LogicalRootKinds.SyncRoot, "sync-root-a", deviceId,
            file.CreateDatabasePath("sync-root"), "settings_json", true);
        PatchouliAppSettings initial = PatchouliAppSettings.Default() with
        {
            Sync = PatchouliAppSettings.Default().Sync with
            {
                DeviceId = deviceId,
                DeviceBindings = [binding]
            }
        };
        initial.Save(file.Path).IsSuccess.Should().BeTrue();
        PatchouliAppSettings staleUiSnapshot = PatchouliAppSettings.Load(file.Path);

        SnapshotSyncSettingsStore snapshotStore = new(databasePath, file.Path, file.CreateDatabasePath("staging"));
        SnapshotSyncLocalState checkpoint = new(
            "published-1", "applied-1", "remote-1", "lineage-1",
            SnapshotSyncOperationState.Published, null, DateTimeOffset.Parse("2026-10-18T00:00:00Z"));
        (await snapshotStore.SaveLocalStateAsync(checkpoint)).IsSuccess.Should().BeTrue();

        PatchouliAppSettings withNewFont = staleUiSnapshot with
        {
            Ui = staleUiSnapshot.Ui with { ReadingFontFamily = "Checkpoint-safe font" }
        };
        (await withNewFont.SaveFieldLevelAsync(file.Path, new HashSet<string> { "Ui" })).IsSuccess
            .Should().BeTrue();

        PatchouliAppSettings withNewLlm = staleUiSnapshot with
        {
            Llm = staleUiSnapshot.Llm with { ChatModel = "checkpoint-safe-model" }
        };
        (await withNewLlm.SaveFieldLevelAsync(file.Path, new HashSet<string> { "Llm" })).IsSuccess
            .Should().BeTrue();

        PatchouliAppSettings withNewOcr = staleUiSnapshot with
        {
            OcrEngines = staleUiSnapshot.OcrEngines with { DocumentOcrEngine = "checkpoint-safe-ocr" }
        };
        (await withNewOcr.SaveFieldLevelAsync(file.Path, new HashSet<string> { "OcrEngines" })).IsSuccess
            .Should().BeTrue();

        PatchouliAppSettings afterUnrelatedUiSaves = PatchouliAppSettings.Load(file.Path);
        afterUnrelatedUiSaves.Sync.Bindings.Single().SnapshotState.Should().BeEquivalentTo(checkpoint);

        AppSettingsDeviceRootBindingStore bindingStore = new(file.Path);
        (await bindingStore.DeleteBindingAsync(libraryId, LogicalRootKinds.SyncRoot,
            binding.LogicalRootId, deviceId)).IsSuccess.Should().BeTrue();

        PatchouliAppSettings staleSyncSave = staleUiSnapshot with
        {
            Sync = staleUiSnapshot.Sync with { DeviceName = "Updated device name" }
        };
        (await staleSyncSave.SaveFieldLevelAsync(file.Path, new HashSet<string> { "Sync" })).IsSuccess
            .Should().BeTrue();

        PatchouliAppSettings persisted = PatchouliAppSettings.Load(file.Path);
        persisted.Ui.ReadingFontFamily.Should().Be("Checkpoint-safe font");
        persisted.Llm.ChatModel.Should().Be("checkpoint-safe-model");
        persisted.OcrEngines.DocumentOcrEngine.Should().Be("checkpoint-safe-ocr");
        persisted.Sync.Bindings.Should().BeEmpty("a stale Sync form save must not restore a deleted binding");
        persisted.Sync.DeviceName.Should().Be("Updated device name");
    }

    [Fact]
    public async Task Concurrent_checkpoint_and_ui_saves_preserve_both_and_stale_binding_save_keeps_new_ui_fields()
    {
        using TemporaryAppSettingsFile file = new();
        LibraryId libraryId = LibraryId.New();
        string deviceId = Guid.NewGuid().ToString("D");
        DeviceRootBindingAppSettings binding = NewBindingRow(libraryId, deviceId, "sync-root-a",
            file.CreateDatabasePath("sync-root"));
        PatchouliAppSettings initial = PatchouliAppSettings.Default() with
        {
            Sync = PatchouliAppSettings.Default().Sync with
            {
                DeviceId = deviceId,
                DeviceBindings = [binding]
            }
        };
        initial.Save(file.Path).IsSuccess.Should().BeTrue();
        PatchouliAppSettings staleUiSnapshot = PatchouliAppSettings.Load(file.Path);
        SnapshotSyncLocalState checkpoint = new(
            "published-concurrent", "applied-concurrent", null, "lineage-concurrent",
            SnapshotSyncOperationState.Published, null, DateTimeOffset.Parse("2026-10-18T01:00:00Z"));

        string settingsPath = file.Path;
        TaskCompletionSource<bool> checkpointEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseCheckpoint = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SettingsSaveResult> checkpointWrite = Task.Run(() => PatchouliAppSettings.UpdateSyncJsonAsync(
            settingsPath,
            sync =>
            {
                checkpointEntered.TrySetResult(true);
                releaseCheckpoint.Task.GetAwaiter().GetResult();
                JsonArray bindings = sync["DeviceBindings"]!.AsArray();
                JsonObject target = bindings.Single()!.AsObject();
                target[nameof(DeviceRootBindingAppSettings.SnapshotState)] =
                    JsonSerializer.SerializeToNode(checkpoint);
                return true;
            }).GetAwaiter().GetResult());
        bool checkpointHasEntered = await Task.WhenAny(checkpointEntered.Task, Task.Delay(TimeSpan.FromSeconds(10))) ==
                                    checkpointEntered.Task;
        if (!checkpointHasEntered)
        {
            releaseCheckpoint.TrySetResult(true);
        }

        checkpointHasEntered.Should().BeTrue();

        PatchouliAppSettings uiEdit = staleUiSnapshot with
        {
            Ui = staleUiSnapshot.Ui with { ReadingFontFamily = "Concurrent-safe font" },
            Llm = staleUiSnapshot.Llm with { ChatModel = "concurrent-safe-model" },
            OcrEngines = staleUiSnapshot.OcrEngines with { DocumentOcrEngine = "concurrent-safe-ocr" }
        };
        Task<SettingsSaveResult> uiWrite = uiEdit.SaveFieldLevelAsync(settingsPath,
            new HashSet<string> { "Ui", "Llm", "OcrEngines" });
        try
        {
            uiWrite.IsCompleted.Should().BeFalse("the snapshot write owns the shared path gate");
        }
        finally
        {
            releaseCheckpoint.TrySetResult(true);
        }

        (await checkpointWrite).IsSuccess.Should().BeTrue();
        (await uiWrite).IsSuccess.Should().BeTrue();

        DeviceRootBindingAppSettings staleBindingDraft = staleUiSnapshot.Sync.Bindings.Single();
        (await PatchouliAppSettings.SaveSyncBindingAsync(file.Path, staleUiSnapshot.Sync, staleBindingDraft))
            .IsSuccess.Should().BeTrue();

        PatchouliAppSettings persisted = PatchouliAppSettings.Load(file.Path);
        persisted.Ui.ReadingFontFamily.Should().Be("Concurrent-safe font");
        persisted.Llm.ChatModel.Should().Be("concurrent-safe-model");
        persisted.OcrEngines.DocumentOcrEngine.Should().Be("concurrent-safe-ocr");
        persisted.Sync.Bindings.Single().SnapshotState.Should().BeEquivalentTo(checkpoint);
    }

    private static DeviceRootBinding NewBinding(LibraryId libraryId, string deviceId, string localPath)
    {
        return new DeviceRootBinding(libraryId, LogicalRootKinds.SyncRoot, "sync-root-a", deviceId,
            localPath, "test", true, FileSearchRootAuthorizationKinds.None, null, null, null,
            DateTimeOffset.Parse("2026-10-18T00:00:00Z"));
    }

    private static DeviceRootBindingAppSettings NewBindingRow(
        LibraryId libraryId,
        string deviceId,
        string logicalRootId,
        string localPath)
    {
        return new DeviceRootBindingAppSettings(
            libraryId.ToString(), LogicalRootKinds.SyncRoot, logicalRootId, deviceId, localPath,
            "settings_ui", true, SnapshotState: SnapshotSyncLocalState.NotConfigured,
            SyncedSettingKeys: []);
    }

    private static async Task CreateRuntimeDatabaseAsync(string path, LibraryId libraryId)
    {
        await using SqliteConnection connection = new($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE library_metadata (library_id TEXT NOT NULL); " +
                              "INSERT INTO library_metadata (library_id) VALUES ($libraryId);";
        command.Parameters.AddWithValue("$libraryId", libraryId.ToString());
        await command.ExecuteNonQueryAsync();
    }
}
