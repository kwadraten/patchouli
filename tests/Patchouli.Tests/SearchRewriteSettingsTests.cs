using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Search;
using Patchouli.Host.Composition;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.Tests;

/// <summary>A partially failed save of the 搜索重写 table must not duplicate rows that already
/// committed; failed rows stay dirty and retry against their persisted identity.</summary>
public sealed class SearchRewriteSettingsTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    [Fact]
    public async Task Partially_failed_save_does_not_duplicate_committed_rows_and_failed_rows_retry()
    {
        string path = _settings.CreateDatabasePath("search-rewrite-partial");
        try
        {
            MainWindowViewModel vm = new(settingsPath: _settings.Path) { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            SearchRewriteSettingsViewModel section = vm.Settings.SearchRewriteSettings;
            await section.LoadAsync();

            section.AddRuleCommand.Execute(null);
            section.AddRuleCommand.Execute(null);
            SearchRewriteRuleRowViewModel first = section.Rules[section.Rules.Count - 2];
            SearchRewriteRuleRowViewModel second = section.Rules[section.Rules.Count - 1];
            first.Pattern = "臺灣";
            first.Replacement = "台湾";
            // The second row keeps an empty pattern, so its commit fails deterministically.
            section.IsDirty.Should().BeTrue();

            await section.SaveAsync();

            section.SaveState.Should().Be(SettingsSaveState.Saved);
            section.ValidationState.Should().Be(SettingsValidationState.Invalid);
            section.IsDirty.Should().BeFalse();
            section.Status.Should().Contain("草稿");
            first.RuleId.Should().NotBeNull("the committed row must adopt its persisted identity");
            second.RuleId.Should().BeNull();

            HostServices services = await vm.ServicesAsync();
            SearchRewriteRuleId adoptedId = first.RuleId!.Value;
            Result<IReadOnlyList<SearchRewriteRule>> rules =
                await services.SearchProfiles.ListRulesAsync(null, true);
            rules.Value.Should().ContainSingle(rule => rule.Pattern == "臺灣");

            second.Pattern = "计算机";
            second.Replacement = "电脑";
            await section.SaveAsync();

            section.SaveState.Should().Be(SettingsSaveState.Saved);
            section.IsDirty.Should().BeFalse();
            rules = await services.SearchProfiles.ListRulesAsync(null, true);
            rules.Value.Should().HaveCount(2);
            rules.Value.Where(rule => rule.Pattern == "臺灣").Should().ContainSingle()
                .Which.RuleId.Should().Be(adoptedId);
            rules.Value.Where(rule => rule.Pattern == "计算机").Should().ContainSingle();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    public void Dispose()
    {
        _settings.Dispose();
    }
}
