using FluentAssertions;
using Patchouli.Host.Settings;
using Patchouli.UI;

namespace Patchouli.Tests;

public sealed class AppSettingsStoreTests
{
    [Fact]
    public async Task Update_is_visible_immediately_and_debounced_value_is_persisted()
    {
        using TemporaryAppSettingsFile file = new();
        PatchouliAppSettings initial = PatchouliAppSettings.Load(file.Path);
        await using AppSettingsStore store = new(file.Path, initial, TimeSpan.FromMilliseconds(30));

        for (int i = 0; i < 10; i++)
        {
            string palette = $"palette-{i}";
            store.Update(settings => settings with { Ui = settings.Ui with { PaletteId = palette } }, "Ui");
            store.Current.Ui.PaletteId.Should().Be(palette);
        }

        await Task.Delay(100);
        (await store.FlushAsync()).IsSuccess.Should().BeTrue();
        PatchouliAppSettings.Load(file.Path).Ui.PaletteId.Should().Be("palette-9");
        store.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task Successful_save_notifies_minimal_runtime_category_with_committed_snapshot()
    {
        using TemporaryAppSettingsFile file = new();
        PatchouliAppSettings initial = PatchouliAppSettings.Load(file.Path);
        await using AppSettingsStore store = new(file.Path, initial, TimeSpan.FromHours(1));
        AppSettingsCommittedEventArgs? committed = null;
        store.SettingsCommitted += (_, change) => committed = change;
        PatchouliAppSettings next = initial with
        {
            Llm = initial.Llm with { ChatModel = "committed-model" }
        };
        store.Update(settings => settings with { Llm = next.Llm }, "Llm");

        SettingsSaveResult result = await store.SaveImmediatelyAsync();

        result.IsSuccess.Should().BeTrue();
        committed.Should().NotBeNull();
        committed!.Snapshot.Llm.ChatModel.Should().Be("committed-model");
        committed.Categories.Should().Equal("Llm");
    }

    [Fact]
    public async Task Failed_write_preserves_dirty_state()
    {
        string root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"patchouli-settings-store-{Guid.NewGuid():N}")).FullName;
        try
        {
            string blockingFile = Path.Combine(root, "blocking-file");
            await File.WriteAllTextAsync(blockingFile, "not a directory");
            string invalidPath = Path.Combine(blockingFile, "settings.json");
            await using AppSettingsStore store = new(
                invalidPath,
                PatchouliAppSettings.Default(),
                TimeSpan.FromHours(1));
            bool notified = false;
            store.SettingsCommitted += (_, _) => notified = true;
            store.Update(settings => settings with
            {
                Ui = settings.Ui with { PaletteId = "will-fail" }
            }, "Ui");

            SettingsSaveResult result = await store.SaveImmediatelyAsync();

            result.IsSuccess.Should().BeFalse();
            store.IsDirty.Should().BeTrue();
            notified.Should().BeFalse();
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryWithRetry(root);
        }
    }

    [Fact]
    public async Task DisposeAsync_flushes_a_pending_long_debounce()
    {
        using TemporaryAppSettingsFile file = new();
        PatchouliAppSettings initial = PatchouliAppSettings.Load(file.Path);
        AppSettingsStore store = new(file.Path, initial, TimeSpan.FromHours(1));
        store.Update(settings => settings with
        {
            Ui = settings.Ui with { PaletteId = "flushed-on-dispose" }
        }, "Ui");

        await store.DisposeAsync();

        PatchouliAppSettings.Load(file.Path).Ui.PaletteId.Should().Be("flushed-on-dispose");
    }

    [Fact]
    public async Task Cancelled_field_level_save_does_not_over_release_the_file_gate()
    {
        using TemporaryAppSettingsFile file = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        string settingsPath = file.Path;
        CancellationToken cancellationToken = cancellation.Token;

        Func<Task> act = () => PatchouliAppSettings.Default().SaveFieldLevelAsync(
            settingsPath,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Ui" },
            cancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
