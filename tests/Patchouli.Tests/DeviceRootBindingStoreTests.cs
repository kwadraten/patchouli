using FluentAssertions;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.UI;

namespace Patchouli.Tests;

public sealed class DeviceRootBindingStoreTests
{
    [Fact]
    public async Task Appsettings_device_root_bindings_are_keyed_by_library_root_kind_logical_root_and_device()
    {
        string root = Path.Combine(Path.GetTempPath(), $"patchouli-binding-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string settingsPath = Path.Combine(root, "settings.json");
        try
        {
            string deviceId = Guid.NewGuid().ToString("D");
            PatchouliAppSettings settings = PatchouliAppSettings.Default() with
            {
                Sync = PatchouliAppSettings.Default().Sync with { DeviceId = deviceId }
            };
            settings.Save(settingsPath).IsSuccess.Should().BeTrue();

            AppSettingsDeviceRootBindingStore store = new(settingsPath);
            LibraryId libraryA = LibraryId.New();
            LibraryId libraryB = LibraryId.New();
            string pathA = Path.Combine(root, "library-a-root");
            string pathB = Path.Combine(root, "library-b-root");
            Directory.CreateDirectory(pathA);
            Directory.CreateDirectory(pathB);

            await store.SaveBindingAsync(new DeviceRootBinding(libraryA, LogicalRootKinds.SyncRoot,
                "logical-sync", deviceId, pathA, "test", true, FileSearchRootAuthorizationKinds.None, null, null,
                null, DateTimeOffset.Parse("2026-07-13T00:00:00Z")));
            await store.SaveBindingAsync(new DeviceRootBinding(libraryB, LogicalRootKinds.SyncRoot,
                "logical-sync", deviceId, pathB, "test", true, FileSearchRootAuthorizationKinds.None, null, null,
                null, DateTimeOffset.Parse("2026-07-13T00:00:00Z")));

            Result<DeviceRootBinding?> a = await store.GetBindingAsync(libraryA, LogicalRootKinds.SyncRoot,
                "logical-sync", deviceId);
            Result<DeviceRootBinding?> b = await store.GetBindingAsync(libraryB, LogicalRootKinds.SyncRoot,
                "logical-sync", deviceId);
            Result<IReadOnlyList<DeviceRootBinding>> listed = await store.ListBindingsAsync(rootKind:
                LogicalRootKinds.SyncRoot, deviceId: deviceId);

            a.Value!.LocalPath.Should().Be(Path.GetFullPath(pathA));
            b.Value!.LocalPath.Should().Be(Path.GetFullPath(pathB));
            listed.Value.Should().HaveCount(2);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
