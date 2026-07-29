using FluentAssertions;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.Tests;

public sealed class MetadataLookupSettingsTests
{
    [Fact]
    public void Defaults_prioritize_specialized_sources()
    {
        MetadataLookupAppSettings.Default().Sources.Take(7).Select(source => source.SourceId).Should().Equal(
            "calis", "nlc", "ndl", "cinii", "library-of-congress", "dnb", "bnf");
    }

    [Fact]
    public void Load_merges_new_defaults_after_saved_order_and_ignores_bad_entries()
    {
        string path = TemporarySettings("""
                                        {
                                          "MetadataLookup": {
                                            "Sources": [
                                              { "SourceId": "crossref", "Enabled": false },
                                              { "SourceId": "crossref", "Enabled": true },
                                              { "Enabled": true },
                                              "invalid"
                                            ]
                                          }
                                        }
                                        """);
        try
        {
            PatchouliAppSettings settings = PatchouliAppSettings.Load(path);

            settings.MetadataLookup.Sources.First().Should().Be(new MetadataSourcePreference("crossref", false));
            settings.MetadataLookup.Sources.Select(source => source.SourceId).Should().OnlyHaveUniqueItems();
            settings.MetadataLookup.Sources.Should().Contain(source => source.SourceId == "ndl");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_round_trips_preferences_and_preserves_forward_sections()
    {
        string path = TemporarySettings("""{ "FutureFeature": { "Version": 3 } }""");
        try
        {
            PatchouliAppSettings settings = PatchouliAppSettings.Load(path) with
            {
                MetadataLookup = new MetadataLookupAppSettings(
                [
                    new MetadataSourcePreference("pubmed", false),
                    new MetadataSourcePreference("crossref", true)
                ])
            };

            settings.Save(path);
            PatchouliAppSettings reloaded = PatchouliAppSettings.Load(path);

            reloaded.MetadataLookup.Sources.Take(2).Should().Equal(
                new MetadataSourcePreference("pubmed", false),
                new MetadataSourcePreference("crossref", true));
            File.ReadAllText(path).Should().Contain("FutureFeature").And.Contain("Version");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Metadata_settings_save_and_discard_are_real()
    {
        string path = TemporarySettings("{}");
        try
        {
            MainWindowViewModel main = new(settingsPath: path);
            MetadataLookupSettingsViewModel viewModel = main.Settings.MetadataLookupSettings;
            main.Settings.ActiveCategory =
                main.Settings.Categories.Single(category => ReferenceEquals(category.Content, viewModel));
            MetadataSourceSettingsRowViewModel first = viewModel.Sources[0];
            first.Enabled = false;
            viewModel.Sources[1].MoveUpCommand.Execute(null);

            viewModel.IsDirty.Should().BeTrue();
            await main.Settings.SaveCommand.ExecuteAsync();
            PatchouliAppSettings.Load(path).MetadataLookup.Sources.First().SourceId.Should()
                .Be(viewModel.Sources[0].SourceId);
            viewModel.Status.Should().Be("已保存");

            viewModel.Sources[0].Enabled = !viewModel.Sources[0].Enabled;
            await main.Settings.DiscardCommand.ExecuteAsync();
            viewModel.IsDirty.Should().BeFalse();
            viewModel.Sources[0].Enabled.Should().Be(PatchouliAppSettings.Load(path).MetadataLookup.Sources[0].Enabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Changing_category_does_not_discard_unsaved_settings()
    {
        string path = TemporarySettings("{}");
        try
        {
            MainWindowViewModel main = new(settingsPath: path);
            MetadataLookupSettingsViewModel metadata = main.Settings.MetadataLookupSettings;
            SettingsCategoryViewModel metadataCategory = main.Settings.Categories.Single(category =>
                ReferenceEquals(category.Content, metadata));
            SettingsCategoryViewModel syncCategory = main.Settings.Categories.Single(category =>
                ReferenceEquals(category.Content, main.Settings.SyncSettings));
            main.Settings.ActiveCategory = metadataCategory;
            bool original = metadata.Sources[0].Enabled;

            metadata.Sources[0].Enabled = !original;
            main.Settings.ActiveCategory = syncCategory;

            main.Settings.ActiveCategory.Should().BeSameAs(metadataCategory);
            metadata.Sources[0].Enabled.Should().Be(!original);
            metadata.IsDirty.Should().BeTrue();
            main.Settings.GlobalStatus.Should().Contain("未保存");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Closing_settings_does_not_discard_unsaved_drafts()
    {
        string path = TemporarySettings("{}");
        try
        {
            MainWindowViewModel main = new(settingsPath: path);
            await main.OpenSettingsAsync("mineru");
            MetadataLookupSettingsViewModel metadata = main.Settings.MetadataLookupSettings;
            metadata.Sources[0].Enabled = !metadata.Sources[0].Enabled;

            await main.CloseSettingsTabCommand.ExecuteAsync();

            main.ShowSettingsTab.Should().BeTrue();
            metadata.IsDirty.Should().BeTrue();
            main.Settings.GlobalStatus.Should().Contain("未保存");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Opting_in_moves_metadata_preferences_to_the_library_and_opting_out_materializes_them_locally()
    {
        string root = Path.Combine(Path.GetTempPath(), $"patchouli-synced-metadata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string settingsPath = Path.Combine(root, "appsettings.json");
        string databasePath = Path.Combine(root, "runtime.sqlite");
        try
        {
            PatchouliAppSettings initial = PatchouliAppSettings.Default() with
            {
                Runtime = PatchouliAppSettings.Default().Runtime with { RuntimeDatabasePath = databasePath }
            };
            initial.Save(settingsPath).IsSuccess.Should().BeTrue();
            MainWindowViewModel main = new(settingsPath: settingsPath);
            AppServices services = await main.ServicesAsync();
            (await services.Library.CreateLibraryAsync("Synced metadata")).IsSuccess.Should().BeTrue();
            SyncSettingsViewModel sync = main.Settings.SyncSettings;
            await sync.LoadAsync();
            sync.SyncRoot = Path.Combine(root, "sync");
            sync.SyncMetadataLookup = true;

            await sync.SaveAsync();
            MetadataLookupSettingsViewModel metadata = main.Settings.MetadataLookupSettings;
            metadata.Sources[0].Enabled = false;
            await metadata.SaveAsync();

            (await (await main.ServicesAsync()).LibrarySettings.GetAsync("metadata_lookup")).Value.Should().NotBeNull();
            File.ReadAllText(settingsPath).Should().NotContain("\"MetadataLookup\"");

            MainWindowViewModel restarted = new(settingsPath: settingsPath);
            await restarted.ServicesAsync();
            restarted.Settings.MetadataLookupSettings.Sources[0].Enabled.Should().BeFalse();

            sync.SyncMetadataLookup = false;
            await sync.SaveAsync();

            (await (await main.ServicesAsync()).LibrarySettings.GetAsync("metadata_lookup")).Value.Should().BeNull();
            PatchouliAppSettings.Load(settingsPath).MetadataLookup.Sources[0].Enabled.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Malformed_json_loads_defaults()
    {
        string path = TemporarySettings("{ not-json");
        try
        {
            PatchouliAppSettings.Load(path).MetadataLookup.Sources.Should().NotBeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Save_failure_keeps_changes_dirty_and_reports_failure()
    {
        string settingsPath = Directory
            .CreateDirectory(Path.Combine(Path.GetTempPath(), $"patchouli-metadata-settings-{Guid.NewGuid():N}"))
            .FullName;
        try
        {
            MainWindowViewModel main = new(settingsPath: settingsPath);
            MetadataLookupSettingsViewModel viewModel = main.Settings.MetadataLookupSettings;
            viewModel.Sources[0].Enabled = !viewModel.Sources[0].Enabled;

            await viewModel.SaveAsync();

            viewModel.IsDirty.Should().BeTrue();
            viewModel.Status.Should().StartWith("保存失败");
        }
        finally
        {
            Directory.Delete(settingsPath, true);
        }
    }

    private static string TemporarySettings(string contents)
    {
        string path = Path.Combine(Path.GetTempPath(), $"patchouli-metadata-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, contents);
        return path;
    }
}
