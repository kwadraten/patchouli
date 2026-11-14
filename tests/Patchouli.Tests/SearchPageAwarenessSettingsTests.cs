using FluentAssertions;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.Tests;

/// <summary>The 搜索框页面感知 panel under Settings &gt; 搜索: per-workspace-kind defaults, the
/// persisted <see cref="UiPreferences.SearchPageModes"/> map (missing, stale and invalid values),
/// and the auto-save write-through that must not clobber unrelated UI preferences.</summary>
[Collection("Avalonia")]
public sealed class SearchPageAwarenessSettingsTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    [Fact]
    public void Document_search_is_offered_only_for_document_workspace_kinds()
    {
        using MainWindowViewModel main = new(settingsPath: _settings.Path);
        foreach (SearchPageModeRowViewModel row in main.Settings.SearchRewriteSettings.Awareness.Rows)
        {
            row.ModeOptions.Any(option => option.Mode == SearchMode.DocumentFullText).Should()
                .Be(row.Kind is WorkspaceTabKind.PdfWorkspace or WorkspaceTabKind.ItemEditor);
            row.ModeOptions.Any(option => option.Mode == SearchMode.Settings).Should()
                .Be(row.Kind == WorkspaceTabKind.Settings);
        }

        UiPreferences preferences = UiPreferences.Default() with
        {
            SearchPageModes = new Dictionary<string, string> { ["Settings"] = "document_full_text" }
        };
        SearchPageAwareness.GetMode(preferences, WorkspaceTabKind.Settings).Should().Be(SearchMode.Settings);
    }

    [Fact]
    public void Mode_options_are_ordered_and_carry_the_documented_labels()
    {
        SearchPageAwareness.ModeOptions.Select(option => option.Mode).Should()
            .Equal(SearchMode.Bibliographic, SearchMode.FullText, SearchMode.DocumentFullText,
                SearchMode.Settings);
        SearchPageAwareness.ModeOptions.Select(option => option.Label).Should()
            .Equal("元数据检索", "全库全文检索", "全书全文检索", "设置");
    }

    [Fact]
    public void Every_workspace_tab_kind_has_a_row_and_a_default_mode()
    {
        SearchPageAwareness.Kinds.Should().Equal(Enum.GetValues<WorkspaceTabKind>());

        UiPreferences preferences = UiPreferences.Default();
        SearchPageAwareness.GetMode(preferences, WorkspaceTabKind.Library).Should()
            .Be(SearchMode.Bibliographic);
        SearchPageAwareness.GetMode(preferences, WorkspaceTabKind.PdfWorkspace).Should()
            .Be(SearchMode.DocumentFullText);
        SearchPageAwareness.GetMode(preferences, WorkspaceTabKind.ItemEditor).Should()
            .Be(SearchMode.DocumentFullText);
        SearchPageAwareness.GetMode(preferences, WorkspaceTabKind.Settings).Should()
            .Be(SearchMode.Settings, "the settings workspace searches settings");
        foreach (WorkspaceTabKind kind in Enum.GetValues<WorkspaceTabKind>())
        {
            if (kind is WorkspaceTabKind.Library or WorkspaceTabKind.PdfWorkspace
                or WorkspaceTabKind.ItemEditor or WorkspaceTabKind.Settings)
            {
                continue;
            }

            SearchPageAwareness.GetMode(preferences, kind).Should()
                .Be(SearchMode.FullText, $"{kind} defaults to library full text");
        }
    }

    [Fact]
    public void Missing_or_invalid_persisted_values_fall_back_to_defaults()
    {
        UiPreferences preferences = UiPreferences.Default() with
        {
            SearchPageModes = new Dictionary<string, string>
            {
                ["PdfWorkspace"] = "garbage",
                ["SearchResults"] = "bibliographic",
                ["NotARealKind"] = "full_text"
            }
        };

        // Invalid value reads back as the kind default; a valid override wins; kinds without an
        // entry keep their default.
        SearchPageAwareness.GetMode(preferences, WorkspaceTabKind.PdfWorkspace).Should()
            .Be(SearchMode.DocumentFullText);
        SearchPageAwareness.GetMode(preferences, WorkspaceTabKind.SearchResults).Should()
            .Be(SearchMode.Bibliographic);
        SearchPageAwareness.GetMode(preferences, WorkspaceTabKind.Library).Should()
            .Be(SearchMode.Bibliographic);

        SearchPageAwareness.GetModeOption(preferences, WorkspaceTabKind.SearchResults).Should()
            .BeSameAs(SearchPageAwareness.ModeOptions[0]);
    }

    [Fact]
    public async Task Row_selection_persists_through_the_settings_store_and_roundtrips()
    {
        MainWindowViewModel vm = new(settingsPath: _settings.Path);
        SearchPageAwarenessSettingsViewModel awareness = vm.Settings.SearchRewriteSettings.Awareness;

        awareness.Rows.Select(row => row.Kind).Should().Equal(SearchPageAwareness.Kinds);
        SearchPageModeRowViewModel pdfRow = awareness.Rows.Single(row => row.Kind == WorkspaceTabKind.PdfWorkspace);
        pdfRow.SelectedMode.Should().BeSameAs(SearchPageAwareness.ModeOptions[2],
            "PDF workspaces default to document full text");

        PatchouliAppSettings? committedSnapshot = null;
        vm.SettingsStore.SettingsCommitted += (_, args) =>
        {
            if (args.Categories.Contains("Ui"))
            {
                committedSnapshot = args.Snapshot;
            }
        };

        pdfRow.SelectedMode = SearchPageAwareness.ModeOptions[0];

        Dictionary<string, string> liveModes =
            vm.AppOptions.Ui.SearchPageModes ?? new Dictionary<string, string>();
        liveModes.Should().ContainKey("PdfWorkspace").WhoseValue.Should().Be("bibliographic");

        await vm.SettingsStore.FlushAsync();

        committedSnapshot.Should().NotBeNull(
            "committing the deferred write must raise SettingsCommitted with the Ui category");
        (committedSnapshot!.Ui.SearchPageModes ?? new Dictionary<string, string>()).Should()
            .ContainKey("PdfWorkspace");

        PatchouliAppSettings reloaded = PatchouliAppSettings.Load(_settings.Path);
        Dictionary<string, string> reloadedModes =
            reloaded.Ui.SearchPageModes ?? new Dictionary<string, string>();
        reloadedModes.Should().ContainKey("PdfWorkspace").WhoseValue.Should().Be("bibliographic");
        SearchPageAwareness.GetModeOption(reloaded.Ui, WorkspaceTabKind.PdfWorkspace).Mode.Should()
            .Be(SearchMode.Bibliographic);

        // A new main window picks the persisted override up when its rows are built.
        MainWindowViewModel next = new(settingsPath: _settings.Path);
        next.Settings.SearchRewriteSettings.Awareness.Rows
            .Single(row => row.Kind == WorkspaceTabKind.PdfWorkspace)
            .SelectedMode.Should().BeSameAs(SearchPageAwareness.ModeOptions[0]);
    }

    [Fact]
    public async Task Selecting_the_default_mode_removes_the_persisted_override()
    {
        MainWindowViewModel vm = new(settingsPath: _settings.Path);
        SearchPageModeRowViewModel pdfRow = vm.Settings.SearchRewriteSettings.Awareness.Rows
            .Single(row => row.Kind == WorkspaceTabKind.PdfWorkspace);

        pdfRow.SelectedMode = SearchPageAwareness.ModeOptions[1];
        (vm.AppOptions.Ui.SearchPageModes ?? new Dictionary<string, string>()).Should()
            .ContainKey("PdfWorkspace").WhoseValue.Should().Be("full_text");

        pdfRow.SelectedMode = SearchPageAwareness.ModeOptions[2];
        vm.AppOptions.Ui.SearchPageModes.Should().BeNull(
            "returning to the default removes the only override, leaving no map at all");

        await vm.SettingsStore.FlushAsync();
        PatchouliAppSettings reloaded = PatchouliAppSettings.Load(_settings.Path);
        (reloaded.Ui.SearchPageModes ?? new Dictionary<string, string>()).Should()
            .NotContainKey("PdfWorkspace");
    }

    [Fact]
    public async Task Null_selection_and_explicit_defaults_write_nothing()
    {
        MainWindowViewModel vm = new(settingsPath: _settings.Path);
        SearchPageModeRowViewModel libraryRow = vm.Settings.SearchRewriteSettings.Awareness.Rows
            .Single(row => row.Kind == WorkspaceTabKind.Library);

        // ComboBox-style transient null selections must not persist anything.
        libraryRow.SelectedMode = null;
        // Re-selecting the persisted (default) mode is a no-op write too.
        libraryRow.SelectedMode = SearchPageAwareness.ModeOptions[0];

        vm.AppOptions.Ui.SearchPageModes.Should().BeNull();
        await vm.SettingsStore.FlushAsync();
        PatchouliAppSettings.Load(_settings.Path).Ui.SearchPageModes.Should().BeNull();
    }

    [Fact]
    public async Task Awareness_writes_preserve_unrelated_ui_preferences()
    {
        MainWindowViewModel vm = new(settingsPath: _settings.Path);
        (await vm.SaveReadingFontImmediatelyAsync("Awareness Test Font", 20)).Should().BeTrue();

        SearchPageModeRowViewModel libraryRow = vm.Settings.SearchRewriteSettings.Awareness.Rows
            .Single(row => row.Kind == WorkspaceTabKind.Library);
        libraryRow.SelectedMode = SearchPageAwareness.ModeOptions[1];
        await vm.SettingsStore.FlushAsync();

        PatchouliAppSettings reloaded = PatchouliAppSettings.Load(_settings.Path);
        reloaded.Ui.ReadingFontFamily.Should().Be("Awareness Test Font");
        reloaded.Ui.ReadingFontSize.Should().Be(20);
        (reloaded.Ui.SearchPageModes ?? new Dictionary<string, string>()).Should()
            .ContainKey("Library").WhoseValue.Should().Be("full_text");
    }

    [Fact]
    public async Task Expand_full_text_results_toggle_persists_and_roundtrips()
    {
        MainWindowViewModel vm = new(settingsPath: _settings.Path);
        SearchPageAwarenessSettingsViewModel awareness = vm.Settings.SearchRewriteSettings.Awareness;
        UiPreferences.Default().ExpandFullTextSearchResults.Should().BeTrue();
        awareness.ExpandFullTextSearchResults.Should().BeTrue();
        (await vm.SaveReadingFontImmediatelyAsync("Toggle Test Font", 18)).Should().BeTrue();

        awareness.ExpandFullTextSearchResults = false;
        vm.AppOptions.Ui.ExpandFullTextSearchResults.Should().BeFalse();
        await vm.SettingsStore.FlushAsync();

        PatchouliAppSettings reloaded = PatchouliAppSettings.Load(_settings.Path);
        reloaded.Ui.ExpandFullTextSearchResults.Should().BeFalse();
        reloaded.Ui.ReadingFontFamily.Should().Be("Toggle Test Font");

        MainWindowViewModel next = new(settingsPath: _settings.Path);
        next.Settings.SearchRewriteSettings.Awareness.ExpandFullTextSearchResults.Should().BeFalse();
        next.AppOptions.Ui.ExpandFullTextSearchResults.Should().BeFalse();

        string xaml = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.UI", "Views", "SettingsPage.axaml"));
        xaml.Should().Contain("AutomationProperties.Name=\"全库全文检索\"")
            .And.Contain("Content=\"默认展开命中结果\"");
    }

    public void Dispose()
    {
        _settings.Dispose();
    }
}
