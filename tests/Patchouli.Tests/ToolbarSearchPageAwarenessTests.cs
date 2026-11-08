using Avalonia.Headless;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Results;
using Patchouli.Core.Search;
using Patchouli.Host.Composition;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Editor;

namespace Patchouli.Tests;

/// <summary>
/// Covers the context-aware global toolbar search: <see cref="SearchPageAwareness"/> maps each
/// workspace tab kind onto its default <see cref="SearchMode"/> (configurable through
/// <see cref="UiPreferences.SearchPageModes"/>), the toolbar re-derives the mode on every
/// activation change, and a document-scoped execution routes into the PDF workspace's 全书搜索
/// sidebar instead of the global 搜索结果 tab.
/// </summary>
[Collection("RetainedTabUI")]
[Trait("Category", "ui")]
public sealed class ToolbarSearchPageAwarenessTests(AvaloniaSharedSessionFixture headless) : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    public void Dispose()
    {
        _settings.Dispose();
    }

    [Fact]
    public void Mode_options_cover_the_three_search_scopes_in_order()
    {
        SearchPageAwareness.ModeOptions.Select(option => option.Mode).Should().Equal(
            SearchMode.Bibliographic, SearchMode.FullText, SearchMode.DocumentFullText);
        SearchPageAwareness.ModeOptions.Select(option => option.Label).Should().Equal(
            "元数据检索", "全库全文检索", "全书全文检索");
    }

    [Fact]
    public void GetMode_returns_the_builtin_default_for_every_tab_kind()
    {
        UiPreferences preferences = UiPreferences.Default();

        SearchPageAwareness.GetMode(preferences, WorkspaceTabKind.Library)
            .Should().Be(SearchMode.Bibliographic);
        SearchPageAwareness.GetMode(preferences, WorkspaceTabKind.PdfWorkspace)
            .Should().Be(SearchMode.DocumentFullText);
        SearchPageAwareness.GetMode(preferences, WorkspaceTabKind.ItemEditor)
            .Should().Be(SearchMode.DocumentFullText);

        foreach (WorkspaceTabKind kind in Enum.GetValues<WorkspaceTabKind>()
                     .Where(kind => kind is not (WorkspaceTabKind.Library or WorkspaceTabKind.PdfWorkspace
                         or WorkspaceTabKind.ItemEditor)))
        {
            SearchPageAwareness.GetMode(preferences, kind).Should().Be(SearchMode.FullText,
                $"{kind} falls back to the library-wide full-text default");
        }
    }

    [Fact]
    public void GetMode_honors_configured_overrides_and_ignores_unknown_values()
    {
        UiPreferences preferences = UiPreferences.Default() with
        {
            SearchPageModes = new Dictionary<string, string>
            {
                [WorkspaceTabKind.Library.ToString()] = "full_text",
                [WorkspaceTabKind.Settings.ToString()] = "bibliographic",
                [WorkspaceTabKind.SyncCenter.ToString()] = "document_full_text",
                [WorkspaceTabKind.PdfWorkspace.ToString()] = "document_full_text",
                [WorkspaceTabKind.ItemEditor.ToString()] = "not-a-mode"
            }
        };

        SearchPageAwareness.GetMode(preferences, WorkspaceTabKind.Library)
            .Should().Be(SearchMode.FullText);
        SearchPageAwareness.GetMode(preferences, WorkspaceTabKind.Settings)
            .Should().Be(SearchMode.Bibliographic);
        SearchPageAwareness.GetMode(preferences, WorkspaceTabKind.SyncCenter)
            .Should().Be(SearchMode.FullText,
                "document scope only applies to document-centric kinds");
        SearchPageAwareness.GetMode(preferences, WorkspaceTabKind.PdfWorkspace)
            .Should().Be(SearchMode.DocumentFullText);
        SearchPageAwareness.GetMode(preferences, WorkspaceTabKind.ItemEditor)
            .Should().Be(SearchMode.DocumentFullText, "unrecognized values fall back to the kind default");
    }

    [Fact]
    public void GetModeOption_returns_the_dropdown_entry_for_the_resolved_mode()
    {
        UiPreferences preferences = UiPreferences.Default() with
        {
            SearchPageModes = new Dictionary<string, string>
            {
                [WorkspaceTabKind.About.ToString()] = "bibliographic"
            }
        };

        SearchModeOption option = SearchPageAwareness.GetModeOption(preferences, WorkspaceTabKind.About);
        option.Mode.Should().Be(SearchMode.Bibliographic);
        SearchPageAwareness.ModeOptions.Should().Contain(option);
    }

    [Fact]
    public async Task Page_awareness_applies_defaults_and_an_explicit_scope_can_suspend_it()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<bool>(async () =>
        {
            MainWindowViewModel main = new(new FakeClipboard(), settingsPath: _settings.Path);
            try
            {
                main.ActiveTab!.Kind.Should().Be(WorkspaceTabKind.Library);
                main.ToolbarSearchMode.Mode.Should().Be(SearchMode.Bibliographic);
                main.IsToolbarBibliographicSearch.Should().BeTrue();
                main.IsToolbarSearchPageAware.Should().BeTrue();
                main.IsToolbarDocumentSearchAvailable.Should().BeFalse();

                await main.OpenSettingsCommand.ExecuteAsync();
                main.ActiveTab!.Kind.Should().Be(WorkspaceTabKind.Settings);
                main.ToolbarSearchMode.Mode.Should().Be(SearchMode.FullText);

                main.SelectToolbarSearchModeCommand.Execute(SearchPageAwareness.ModeOptions[0]);
                main.IsToolbarSearchPageAware.Should().BeFalse();
                main.ToolbarSearchMode.Mode.Should().Be(SearchMode.Bibliographic);

                LibraryItemViewModel item = new(
                    ItemId.New().ToString(), "PDF item", "book", "", "", "", null,
                    DocumentInstanceId.New().ToString(), null, "paper.pdf", "", 1, 0, "",
                    _ => Task.CompletedTask, _ => Task.CompletedTask);
                main.Workspace.OpenOrActivate(WorkspaceTabKind.PdfWorkspace, "awareness-pdf",
                    "PDF", "FolderOpen", true, () => new PdfWorkspaceViewModel(main, item));
                await main.Layout.WaitForNavigationAsync();
                main.ToolbarSearchMode.Mode.Should().Be(SearchMode.Bibliographic);
                main.IsToolbarDocumentSearchAvailable.Should().BeTrue();
                main.IsToolbarSearchPageAware = true;
                main.ToolbarSearchMode.Mode.Should().Be(SearchMode.DocumentFullText);

                await main.ShowLibraryCommand.ExecuteAsync();
                main.ToolbarSearchMode.Mode.Should().Be(SearchMode.Bibliographic);
                main.IsToolbarDocumentSearchAvailable.Should().BeFalse();
                return true;
            }
            finally
            {
                await main.ShutdownAsync();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_configured_default_change_updates_the_active_toolbar_immediately()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<bool>(async () =>
        {
            MainWindowViewModel main = new(new FakeClipboard(), settingsPath: _settings.Path);
            try
            {
                main.ToolbarSearchMode.Mode.Should().Be(SearchMode.Bibliographic);

                main.UpdateAppOptions(main.AppOptions with
                {
                    Ui = main.AppOptions.Ui with
                    {
                        SearchPageModes = new Dictionary<string, string>
                        {
                            [WorkspaceTabKind.Library.ToString()] = "full_text"
                        }
                    }
                });
                main.ToolbarSearchMode.Mode.Should().Be(SearchMode.FullText,
                    "a changed per-kind default applies to the active toolbar at once");

                main.UpdateAppOptions(main.AppOptions with
                {
                    Ui = main.AppOptions.Ui with
                    {
                        SearchPageModes = new Dictionary<string, string>
                        {
                            [WorkspaceTabKind.Library.ToString()] = "bibliographic"
                        }
                    }
                });
                main.ToolbarSearchMode.Mode.Should().Be(SearchMode.Bibliographic);
                main.IsToolbarBibliographicSearch.Should().BeTrue();

                // An unrelated Ui write keeps the toolbar mode untouched.
                main.UpdateAppOptions(main.AppOptions with
                {
                    Ui = main.AppOptions.Ui with { ReadingFontSize = 16 }
                });
                main.ToolbarSearchMode.Mode.Should().Be(SearchMode.Bibliographic);
                return true;
            }
            finally
            {
                await main.ShutdownAsync();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Executing_a_global_search_keeps_the_chosen_mode_through_tab_activation()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<bool>(async () =>
        {
            MainWindowViewModel main = new(new FakeClipboard(), settingsPath: _settings.Path)
            {
                RuntimeDatabasePath = _settings.CreateDatabasePath("ui-awareness-run")
            };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            try
            {
                main.ActiveTab!.Kind.Should().Be(WorkspaceTabKind.Library);
                main.ToolbarSearchQuery = "needle";
                await main.RunToolbarSearchCommand.ExecuteAsync();

                main.ActiveTab!.Kind.Should().Be(WorkspaceTabKind.SearchResults);
                // The activation created by the search itself must not restamp the user's mode.
                main.ToolbarSearchMode.Mode.Should().Be(SearchMode.Bibliographic);
                main.SearchEvidence.Mode.Should().Be(SearchMode.Bibliographic);
                return true;
            }
            finally
            {
                await main.ShutdownAsync();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Document_search_opens_the_workspace_search_sidebar_without_a_results_tab()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<bool>(async () =>
        {
            MainWindowViewModel main = new(new FakeClipboard(), settingsPath: _settings.Path)
            {
                RuntimeDatabasePath = _settings.CreateDatabasePath("ui-awareness-doc")
            };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            DocumentInstance? document = null;
            try
            {
                ItemMetadata item = (await services.Items.CreateItemAsync("book", "Awareness target")).Value;
                document = (await services.Documents.AttachDocumentInstanceAsync(
                    item.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
                await main.Shell.RefreshItemsAsync();
                LibraryItemViewModel row = main.Shell.Items.Single(candidate =>
                    candidate.ItemId == item.ItemId.ToString());
                main.Shell.SelectedItem = row;

                // Pre-open the workspace so the BookSearchHandler test seam is in place; the
                // toolbar search reuses the same tab (tab id is derived from the item id).
                await main.ShowReadingAsync(row);
                PdfWorkspaceViewModel workspace =
                    main.ActiveTab!.Content.Should().BeOfType<PdfWorkspaceViewModel>().Which;
                bool sawRequest = false;
                workspace.BookSearchHandler = (request, _) =>
                {
                    sawRequest = true;
                    request.Query.Should().Be("needle");
                    request.DocumentInstanceId.Should().Be(document!.DocumentInstanceId);
                    return Task.FromResult(Result<SearchResultPage>.Success(
                        new SearchResultPage([], null, 0, "current", null)));
                };

                await main.ShowLibraryCommand.ExecuteAsync();
                main.ToolbarSearchMode.Mode.Should().Be(SearchMode.Bibliographic);

                main.ToolbarSearchMode = SearchPageAwareness.ModeOptions[2];
                main.ToolbarSearchQuery = "needle";
                await main.RunToolbarSearchCommand.ExecuteAsync();

                main.ActiveTab!.Content.Should().BeSameAs(workspace);
                workspace.IsSearchTabActive.Should().BeTrue();
                workspace.BookSearchQuery.Should().Be("needle");
                sawRequest.Should().BeTrue("the sidebar search must execute after the tab switch");
                main.Layout.Tabs.Should().NotContain(tab => tab.Kind == WorkspaceTabKind.SearchResults,
                    "document searches never open the global 搜索结果 tab");
                return true;
            }
            finally
            {
                if (document is { } released)
                {
                    await services.PageRenders.ReleaseDocumentSessionAsync(released.DocumentInstanceId);
                }

                await main.ShutdownAsync();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Document_search_defaults_from_an_item_editor_target_its_document()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<bool>(async () =>
        {
            MainWindowViewModel main = new(new FakeClipboard(), settingsPath: _settings.Path)
            {
                RuntimeDatabasePath = _settings.CreateDatabasePath("ui-awareness-editor")
            };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            DocumentInstance? document = null;
            try
            {
                ItemMetadata item = (await services.Items.CreateItemAsync("book", "Editor target")).Value;
                document = (await services.Documents.AttachDocumentInstanceAsync(
                    item.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
                await main.Shell.RefreshItemsAsync();

                await main.EditItemByIdAsync(item.ItemId.ToString());
                main.ActiveTab!.Content.Should().BeOfType<ItemEditorViewModel>();
                main.ToolbarSearchMode.Mode.Should().Be(SearchMode.DocumentFullText,
                    "the item editor's kind default is document-scoped search");
                main.Shell.Items.Clear();

                main.ToolbarSearchQuery = "needle";
                await main.RunToolbarSearchCommand.ExecuteAsync();

                PdfWorkspaceViewModel workspace =
                    main.ActiveTab!.Content.Should().BeOfType<PdfWorkspaceViewModel>().Which;
                workspace.Item.ItemId.Should().Be(item.ItemId.ToString());
                main.Shell.Items.Should().BeEmpty("document lookup must work outside the loaded library rows");
                workspace.IsSearchTabActive.Should().BeTrue();
                workspace.BookSearchQuery.Should().Be("needle");
                return true;
            }
            finally
            {
                if (document is { } released)
                {
                    await services.PageRenders.ReleaseDocumentSessionAsync(released.DocumentInstanceId);
                }

                await main.ShutdownAsync();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Document_search_on_an_item_without_a_document_reports_and_stays_put()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<bool>(async () =>
        {
            MainWindowViewModel main = new(new FakeClipboard(), settingsPath: _settings.Path)
            {
                RuntimeDatabasePath = _settings.CreateDatabasePath("ui-awareness-nodoc")
            };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            try
            {
                ItemMetadata item = (await services.Items.CreateItemAsync("book", "No document")).Value;
                await main.Shell.RefreshItemsAsync();
                LibraryItemViewModel row = main.Shell.Items.Single(candidate =>
                    candidate.ItemId == item.ItemId.ToString());
                row.DocumentInstanceId.Should().BeNull();

                main.Shell.SelectedItem = row;
                main.ToolbarSearchMode = SearchPageAwareness.ModeOptions[2];
                main.ToolbarSearchQuery = "needle";
                await main.RunToolbarSearchCommand.ExecuteAsync();

                main.Status.Should().Contain("暂无文档");
                main.ActiveTab!.Kind.Should().Be(WorkspaceTabKind.Library,
                    "a missing document never opens a workspace");
                main.Layout.Tabs.Should().NotContain(tab => tab.Kind == WorkspaceTabKind.SearchResults);
                return true;
            }
            finally
            {
                await main.ShutdownAsync();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Document_mode_never_leaks_into_the_shared_search_evidence_view_model()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<bool>(async () =>
        {
            MainWindowViewModel main = new(new FakeClipboard(), settingsPath: _settings.Path);
            try
            {
                main.ToolbarSearchMode = SearchPageAwareness.ModeOptions[2];
                main.SearchEvidence.SelectedMode.Mode.Should().NotBe(SearchMode.DocumentFullText,
                    "the library-wide evidence VM only carries bibliographic/full-text modes");
                main.SearchEvidence.ModeOptions.Select(option => option.Mode)
                    .Should().NotContain(SearchMode.DocumentFullText);
                return true;
            }
            finally
            {
                await main.ShutdownAsync();
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Virtual_link_search_setting_controls_search_but_keeps_protocol_navigation(bool parseLinks)
    {
        await headless.Session.Dispatch<bool>(async () =>
        {
            await using MainWindowViewModel main = new(new FakeClipboard(), settingsPath: _settings.Path);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            PdfImportResult imported = await services.PdfImport.ImportPdfAsync(
                new PdfImportRequest(TestFixtures.RealThreePagePdf, "Virtual link test", null, 3));
            imported.Success.Should().BeTrue(imported.ErrorMessage);
            await main.Shell.RefreshItemsAsync();
            main.Settings.VirtualLinkSettings.ParseLinksInSearchBar = parseLinks;
            main.ToolbarSearchMode = SearchPageAwareness.ModeOptions[0];
            string uri = $"patchouli://items/{imported.CreatedItemId}.yml";
            main.ToolbarSearchQuery = uri;
            await main.RunToolbarSearchCommand.ExecuteAsync();
            main.ActiveTab!.Kind.Should().Be(parseLinks ? WorkspaceTabKind.ItemEditor : WorkspaceTabKind.SearchResults);
            if (!parseLinks)
            {
                main.SearchEvidence.Query.Should().Be(uri);
            }

            await main.OpenProtocolUriAsync(uri);
            main.ActiveTab!.Kind.Should().Be(WorkspaceTabKind.ItemEditor);
            await main.OpenProtocolUriAsync($"patchouli://texts/{imported.CreatedDocumentInstanceId}/page-2.xml");
            main.ActiveTab!.Content.Should().BeOfType<PdfWorkspaceViewModel>()
                .Which.PageNumberText.Should().Be("2");
            return true;
        }, CancellationToken.None);
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public string? Text { get; private set; }

        public Task SetTextAsync(string text)
        {
            Text = text;
            return Task.CompletedTask;
        }

        public Task<string?> GetTextAsync()
        {
            return Task.FromResult(Text);
        }

        public Task ClearAsync()
        {
            Text = null;
            return Task.CompletedTask;
        }
    }
}
