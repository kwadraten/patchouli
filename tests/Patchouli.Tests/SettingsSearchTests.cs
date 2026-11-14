using FluentAssertions;
using Patchouli.Core.Search;
using Patchouli.Host.Composition;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.Tests;

/// <summary>Static settings index and toolbar Settings search (issue #53): real section/anchor
/// targets, candidate navigation without a results tab, no query rewrite, and the Settings mode
/// availability rules.</summary>
[Collection("Avalonia")]
public sealed class SettingsSearchTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    [Fact]
    public void Every_index_entry_resolves_to_a_real_section_and_a_declared_card_anchor()
    {
        using MainWindowViewModel main = new(settingsPath: _settings.Path);
        string xaml = string.Join('\n',
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "SettingsPage.axaml")),
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "Settings",
                "OcrComponentCatalogView.axaml")),
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "Settings",
                "NetworkProxySettingsView.axaml")));

        foreach (SettingsSearchEntry entry in SettingsSearchIndex.Entries)
        {
            main.Settings.SectionEntries.Should().Contain(item => item.Id == entry.SectionId,
                $"{entry.DisplayPath} must route through a real section id");
            entry.CardAnchorId.Should().NotBeNullOrWhiteSpace();
            if (entry.CardAnchorId.StartsWith("ocr_components_", StringComparison.Ordinal))
            {
                main.Settings.OcrComponents.Catalogs.Select(catalog => catalog.CardAnchorId)
                    .Should().Contain(entry.CardAnchorId);
                continue;
            }

            xaml.Should().Contain($"AnchorId=\"{entry.CardAnchorId}\"",
                $"{entry.DisplayPath} must have a card anchor the page can bring into view");
        }
    }

    [Fact]
    public async Task Settings_search_navigates_to_the_card_without_opening_a_results_tab()
    {
        using MainWindowViewModel main = new(settingsPath: _settings.Path);
        await main.OpenSettingsCommand.ExecuteAsync();
        main.ToolbarSearchMode.Mode.Should().Be(SearchMode.Settings);

        string? requestedAnchor = null;
        main.Settings.CardNavigationRequested += (_, args) => requestedAnchor = args.AnchorId;
        main.ToolbarSearchQuery = "端口";
        main.IsSettingsSearchOpen.Should().BeTrue();
        SettingsSearchResultViewModel target = main.SettingsSearchResults
            .Single(result => result.Entry.SectionId == "mcp");

        await main.AcceptSettingsSearchResultAsync(target);

        main.Settings.ActiveCategory.Title.Should().Be("AI 集成");
        main.Settings.ActiveTabbedGroup!.ActiveEntry.Id.Should().Be("mcp");
        requestedAnchor.Should().Be("mcp_service");
        main.Layout.Tabs.Should().NotContain(tab => tab.Kind == WorkspaceTabKind.SearchResults);
    }

    [Fact]
    public async Task Settings_search_ignores_query_rewrite_and_reports_no_matches()
    {
        string database = _settings.CreateDatabasePath("settings-search-rewrite");
        await using (MainWindowViewModel main = new(settingsPath: _settings.Path)
                     {
                         RuntimeDatabasePath = database
                     })
        {
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            (await services.SearchProfiles.AddRewriteRuleAsync(null, SearchRuleType.Literal, "端口", "PORT",
                SearchRewriteDirection.Expand, 0, null)).IsSuccess.Should().BeTrue();
            await main.SetQueryRewriteEnabledAsync(true);
            await main.OpenSettingsCommand.ExecuteAsync();
            main.ToolbarSearchQuery = "端口";

            main.SettingsSearchResults.Should().Contain(result => result.Entry.SectionId == "mcp");
            await main.AcceptSettingsSearchResultAsync(main.SettingsSearchResults
                .Single(result => result.Entry.SectionId == "mcp"));
            main.Layout.Tabs.Should().NotContain(tab => tab.Kind == WorkspaceTabKind.SearchResults);

            WorkspaceTabViewModel settingsTab = main.ActiveTab!;
            main.ToolbarSearchQuery = "没有这个设置项";
            main.SettingsSearchResults.Should().BeEmpty();
            main.SettingsSearchStatus.Should().Be("没有找到匹配的设置项");
            await main.RunToolbarSearchCommand.ExecuteAsync();
            main.ActiveTab.Should().BeSameAs(settingsTab);
            main.Status.Should().Contain("没有找到匹配的设置项");
        }
    }

    [Fact]
    public async Task Settings_mode_is_selectable_only_from_the_settings_tab_and_is_its_default()
    {
        await using MainWindowViewModel main = new(settingsPath: _settings.Path);
        SearchModeOption settingsOption =
            SearchPageAwareness.ModeOptions.Single(option => option.Mode == SearchMode.Settings);

        main.ToolbarSearchMode.Mode.Should().Be(SearchMode.Bibliographic);
        main.SelectToolbarSearchModeCommand.Execute(settingsOption);
        main.ToolbarSearchMode.Mode.Should().Be(SearchMode.Bibliographic,
            "settings search is unavailable outside the settings tab");

        await main.OpenSettingsCommand.ExecuteAsync();
        main.IsToolbarSettingsSearchAvailable.Should().BeTrue();
        main.ToolbarSearchMode.Mode.Should().Be(SearchMode.Settings);
        main.IsToolbarSettingsSearch.Should().BeTrue();
        main.SelectToolbarSearchModeCommand.Execute(settingsOption);
        main.ToolbarSearchMode.Mode.Should().Be(SearchMode.Settings);
    }

    public void Dispose()
    {
        _settings.Dispose();
    }
}
