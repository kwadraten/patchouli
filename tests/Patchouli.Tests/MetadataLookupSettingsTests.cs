using FluentAssertions;
using Patchouli.UI;
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
        var path = TemporarySettings("""
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
            var settings = PatchouliAppSettings.Load(path);

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
        var path = TemporarySettings("""{ "FutureFeature": { "Version": 3 } }""");
        try
        {
            var settings = PatchouliAppSettings.Load(path) with
            {
                MetadataLookup = new MetadataLookupAppSettings(
                [
                    new("pubmed", false),
                    new("crossref", true)
                ])
            };

            settings.Save(path);
            var reloaded = PatchouliAppSettings.Load(path);

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
        var path = TemporarySettings("{}");
        try
        {
            var main = new Patchouli.UI.ViewModels.MainWindowViewModel(settingsPath: path);
            var viewModel = main.Settings.MetadataLookupSettings;
            main.Settings.ActiveCategory = main.Settings.Categories.Single(category => ReferenceEquals(category.Content, viewModel));
            var first = viewModel.Sources[0];
            first.Enabled = false;
            viewModel.Sources[1].MoveUpCommand.Execute(null);

            viewModel.IsDirty.Should().BeTrue();
            await main.Settings.SaveCommand.ExecuteAsync();
            PatchouliAppSettings.Load(path).MetadataLookup.Sources.First().SourceId.Should().Be(viewModel.Sources[0].SourceId);
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
    public void Malformed_json_loads_defaults()
    {
        var path = TemporarySettings("{ not-json");
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
        var settingsPath = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"patchouli-metadata-settings-{Guid.NewGuid():N}")).FullName;
        try
        {
            var main = new Patchouli.UI.ViewModels.MainWindowViewModel(settingsPath: settingsPath);
            var viewModel = main.Settings.MetadataLookupSettings;
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
        var path = Path.Combine(Path.GetTempPath(), $"patchouli-metadata-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, contents);
        return path;
    }
}
