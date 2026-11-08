using System.Reactive.Concurrency;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Mcp;
using Patchouli.UI;
using Patchouli.UI.Themes;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

/// <remarks>
/// R14 shared-state audit: every case constructs <see cref="MainWindowViewModel"/> or reads
/// <see cref="ThemePaletteApplier"/> statics (the ctor calls Apply unconditionally) — both touch
/// the process-global roots catalogued in AvaloniaTestCollection, so the class stays in the
/// DisableParallelization collection. Plugin host services are substituted through the
/// <c>PluginApiProvider</c>/<c>PluginViewWindowFactory</c> seams; no NativeWebView is created.
/// </remarks>
[Collection("Avalonia")]
public sealed class PluginViewTests
{
    private static readonly McpPluginViewInfo TabView = new(
        "notes", "插件笔记", "tab",
        "patchouli://plugin/demo/views/notes",
        "patchouli://plugin/demo/ui/notes.html", "ui/notes.html");

    private static readonly McpPluginViewInfo WindowView = new(
        "palette", "配色面板", "window",
        "patchouli://plugin/demo/views/palette",
        "patchouli://plugin/demo/ui/palette.html", "ui/palette.html");

    private static readonly McpPluginViewInfo SidebarView = new(
        "inspect", "页面检查器", "pdf-sidebar",
        "patchouli://plugin/demo/views/inspect",
        "patchouli://plugin/demo/ui/inspect.html", "ui/inspect.html");

    private const string ViewHtml = "<html><head><title>v</title></head><body>hi</body></html>";

    // ---------- context model & script generation ----------

    [Theory]
    [InlineData("SurfaceColor", "--patchouli-color-surface")]
    [InlineData("OnSurfaceVariantColor", "--patchouli-color-on-surface-variant")]
    [InlineData("OnURLColor", "--patchouli-color-on-url")]
    [InlineData("PrimaryColor", "--patchouli-color-primary")]
    public void Palette_keys_map_to_css_custom_properties(string key, string expected)
    {
        PluginViewContextBuilder.CssVariableName(key).Should().Be(expected);
    }

    [Fact]
    public void Context_serializes_theme_and_every_palette_color()
    {
        PluginViewContext context = PluginViewContextBuilder.Build("patchouli://texts/x/page-1.xml");

        using JsonDocument document = JsonDocument.Parse(PluginViewContextScript.Serialize(context));
        JsonElement root = document.RootElement;
        root.GetProperty("version").GetInt32().Should().Be(1);
        JsonElement theme = root.GetProperty("theme");
        theme.GetProperty("mode").GetString().Should().Be(context.ThemeMode);
        theme.GetProperty("scheme").GetString().Should().Be(context.Scheme);
        theme.GetProperty("dark").GetBoolean().Should().Be(context.IsDark);
        root.GetProperty("page").GetProperty("uri").GetString()
            .Should().Be("patchouli://texts/x/page-1.xml");

        IReadOnlyDictionary<string, string> palette = UiColorPalettes
            .Resolve(ThemePaletteApplier.CurrentPaletteId)
            .GetColors(ThemePaletteApplier.CurrentEffectiveIsDark);
        JsonElement colors = theme.GetProperty("colors");
        colors.EnumerateObject().Should().HaveCount(palette.Count);
        foreach (string key in palette.Keys)
        {
            colors.GetProperty(key).GetString().Should().Be(palette[key]);
        }
    }

    [Fact]
    public void Context_serializes_null_page_outside_the_pdf_sidebar()
    {
        using JsonDocument document = JsonDocument.Parse(
            PluginViewContextScript.Serialize(PluginViewContextBuilder.Build(null)));

        document.RootElement.GetProperty("page").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void Theme_apply_notifies_plugin_context_consumers()
    {
        int notifications = 0;
        Action listener = () => notifications++;
        ThemePaletteApplier.Applied += listener;
        try
        {
            ThemePaletteApplier.Apply(null, UiPreferences.ThemeModeLight);
        }
        finally
        {
            ThemePaletteApplier.Applied -= listener;
        }

        notifications.Should().Be(1);
        PluginViewContextBuilder.Build(null).IsDark.Should().BeFalse();
    }

    [Fact]
    public void PrepareHtml_injects_csp_and_context_before_view_markup()
    {
        PluginViewContext context = new("dark", "dark", true,
            new Dictionary<string, string> { ["PrimaryColor"] = "#112233" }, null);
        string html = "<!doctype html><html><head><meta charset=\"utf-8\"></head><body>x</body></html>";

        string prepared = PluginViewContextScript.PrepareHtml(html, context);

        prepared.Should().Contain("Content-Security-Policy");
        prepared.Should().Contain("default-src 'none'");
        prepared.Should().Contain("window.patchouli=");
        prepared.Should().Contain("\"PrimaryColor\":\"#112233\"");
        prepared.Should().Contain("--patchouli-color-primary");
        prepared.Should().Contain("colorScheme");
        prepared.IndexOf("window.patchouli", StringComparison.Ordinal).Should()
            .BeLessThan(prepared.IndexOf("<meta charset", StringComparison.Ordinal));
    }

    [Fact]
    public void PushScript_dispatches_theme_and_page_events()
    {
        PluginViewContext context = new("light", "light", false,
            new Dictionary<string, string>(), "patchouli://texts/d/page-3.xml");

        string script = PluginViewContextScript.BuildPushScript(context);

        script.Should().Contain("window.patchouli=p");
        script.Should().Contain("'patchouli:theme'");
        script.Should().Contain("'patchouli:page'");
        script.Should().Contain("patchouli://texts/d/page-3.xml");
    }

    [Fact]
    public void PrepareHtml_synthesizes_a_head_when_the_document_has_none()
    {
        PluginViewContext context = new("light", "light", false,
            new Dictionary<string, string>(), "patchouli://texts/d/page-2.xml");

        string prepared = PluginViewContextScript.PrepareHtml("<div>loose</div>", context);

        prepared.Should().StartWith("<meta http-equiv=\"Content-Security-Policy\"");
        prepared.Should().Contain("patchouli://texts/d/page-2.xml");
    }

    [Theory]
    [InlineData("{\"type\":\"patchouli:request-context\"}", true)]
    [InlineData("{\"type\":\"patchouli:request-context\",\"extra\":1}", true)]
    [InlineData("{\"type\":\"other\"}", false)]
    [InlineData("patchouli:request-context", false)]
    [InlineData("not json", false)]
    [InlineData("", false)]
    public void Only_the_request_context_message_is_supported(string body, bool expected)
    {
        PluginViewContextScript.IsContextRequestMessage(body).Should().Be(expected);
    }

    // ---------- session: permission gate + fetch ----------

    [Fact]
    public async Task Session_load_fetches_declared_html_through_the_view_verb()
    {
        FakePluginApi api = new();
        api.Allow("demo", "view");
        api.FetchResult = Result<McpPluginFetchContent>.Success(
            new McpPluginFetchContent("text/html", ViewHtml));
        PluginViewSession session = new("demo", TabView);

        (await session.LoadAsync(api)).Should().BeTrue();

        session.Html.Should().Be(ViewHtml);
        session.ErrorText.Should().BeNull();
        session.IsLoading.Should().BeFalse();
        api.FetchedPaths.Should().ContainSingle().Which.Should().Be("ui/notes.html");
    }

    [Fact]
    public async Task Session_load_reports_when_the_view_verb_is_denied()
    {
        FakePluginApi api = new();
        PluginViewSession session = new("demo", TabView);

        (await session.LoadAsync(api)).Should().BeFalse();

        session.Html.Should().BeNull();
        session.ErrorText.Should().Contain("view");
        api.FetchedPaths.Should().BeEmpty();
    }

    [Fact]
    public async Task Session_load_surfaces_fetch_failures()
    {
        FakePluginApi api = new();
        api.Allow("demo", "view");
        api.FetchResult = Result<McpPluginFetchContent>.Failure(AppErrorCodes.NotFound, "missing");
        PluginViewSession session = new("demo", TabView);

        (await session.LoadAsync(api)).Should().BeFalse();

        session.Html.Should().BeNull();
        session.ErrorText.Should().Contain("missing");
    }

    [Fact]
    public async Task Session_release_drops_loaded_content_and_page_context()
    {
        FakePluginApi api = new();
        api.Allow("demo", "view");
        api.FetchResult = Result<McpPluginFetchContent>.Success(
            new McpPluginFetchContent("text/html", ViewHtml));
        PluginViewSession session = new("demo", SidebarView);
        await session.LoadAsync(api);
        session.PageUri = "patchouli://texts/d/page-1.xml";

        session.Release();

        session.Html.Should().BeNull();
        session.PageUri.Should().BeNull();
        session.IsLoading.Should().BeFalse();
    }

    // ---------- routing & containers ----------

    [Fact]
    public async Task Tab_placement_opens_one_retained_workspace_tab_per_view()
    {
        using TemporaryAppSettingsFile settings = new();
        await using MainWindowViewModel main = CreateMain(settings.Path);
        FakePluginApi api = new();
        api.Allow("demo", "view");
        api.AddView("demo", TabView);
        api.FetchResult = Result<McpPluginFetchContent>.Success(
            new McpPluginFetchContent("text/html", ViewHtml));
        main.PluginApiProvider = () => api;

        await main.NavigateToVfsUriAsync("patchouli://plugin/demo/views/notes");
        await main.NavigateToVfsUriAsync("patchouli://plugin/demo/views/notes");

        main.Layout.Tabs.Should().ContainSingle(tab =>
            tab.Kind == WorkspaceTabKind.PluginView && tab.TabId == "PluginView:demo:notes");
        WorkspaceTabViewModel tab = main.Layout.Tabs.Single(tab => tab.Kind == WorkspaceTabKind.PluginView);
        tab.Content.Should().BeOfType<PluginViewViewModel>();
        main.Layout.ActiveTab.Should().BeSameAs(tab);

        PluginViewViewModel viewModel = (PluginViewViewModel)tab.Content;
        await viewModel.EnsureLoadedAsync();
        viewModel.Session.Html.Should().Be(ViewHtml);
    }

    [Fact]
    public async Task Window_placement_reuses_the_tracked_popup_until_it_closes()
    {
        using TemporaryAppSettingsFile settings = new();
        await using MainWindowViewModel main = CreateMain(settings.Path);
        FakePluginApi api = new();
        api.Allow("demo", "view");
        api.AddView("demo", WindowView);
        api.FetchResult = Result<McpPluginFetchContent>.Success(
            new McpPluginFetchContent("text/html", ViewHtml));
        main.PluginApiProvider = () => api;
        List<FakePluginViewWindow> windows = [];
        main.PluginViewWindowFactory = session =>
        {
            FakePluginViewWindow window = new(session);
            windows.Add(window);
            return window;
        };

        await main.NavigateToVfsUriAsync("patchouli://plugin/demo/views/palette");
        await main.NavigateToVfsUriAsync("patchouli://plugin/demo/views/palette");

        windows.Should().HaveCount(1);
        windows[0].ShowCalls.Should().Be(1);
        windows[0].ActivateCalls.Should().Be(1);
        windows[0].Session.Html.Should().Be(ViewHtml);

        windows[0].RaiseClosed();
        await main.NavigateToVfsUriAsync("patchouli://plugin/demo/views/palette");

        windows.Should().HaveCount(2);
        windows[1].ShowCalls.Should().Be(1);
        windows[0].Session.Html.Should().BeNull();
    }

    [Fact]
    public async Task Sidebar_placement_falls_back_to_a_workspace_tab_without_a_pdf_workspace()
    {
        using TemporaryAppSettingsFile settings = new();
        await using MainWindowViewModel main = CreateMain(settings.Path);
        FakePluginApi api = new();
        api.Allow("demo", "view");
        api.AddView("demo", SidebarView);
        api.FetchResult = Result<McpPluginFetchContent>.Success(
            new McpPluginFetchContent("text/html", ViewHtml));
        main.PluginApiProvider = () => api;

        await main.NavigateToVfsUriAsync("patchouli://plugin/demo/views/inspect");

        main.Layout.Tabs.Should().ContainSingle(tab =>
            tab.Kind == WorkspaceTabKind.PluginView && tab.TabId == "PluginView:demo:inspect");
    }

    [Fact]
    public async Task Undeclared_or_denied_views_report_errors_without_opening_containers()
    {
        using TemporaryAppSettingsFile settings = new();
        await using MainWindowViewModel main = CreateMain(settings.Path);
        FakePluginApi api = new();
        api.AddView("demo", TabView); // declared but not permitted
        main.PluginApiProvider = () => api;
        List<FakePluginViewWindow> windows = [];
        main.PluginViewWindowFactory = session =>
        {
            FakePluginViewWindow window = new(session);
            windows.Add(window);
            return window;
        };

        await main.NavigateToVfsUriAsync("patchouli://plugin/demo/views/unknown");
        main.StatusIsError.Should().BeTrue();

        await main.NavigateToVfsUriAsync("patchouli://plugin/demo/views/notes");
        main.Status.Should().Contain("权限");

        await main.NavigateToVfsUriAsync("patchouli://plugin/demo/views");
        main.Status.Should().Contain("没有对应页面");

        main.Layout.Tabs.Should().NotContain(tab => tab.Kind == WorkspaceTabKind.PluginView);
        windows.Should().BeEmpty();
    }

    // ---------- pdf sidebar ----------

    [Fact]
    public async Task Sidebar_view_loads_into_the_plugin_tab_with_the_current_page_uri()
    {
        using TemporaryAppSettingsFile settings = new();
        await using MainWindowViewModel main = CreateMain(settings.Path);
        FakePluginApi api = new();
        api.Allow("demo", "view");
        api.AddView("demo", SidebarView);
        api.Mounts.Add(new McpPluginMountInfo("demo", "Demo", "1.0", "patchouli://plugin/demo",
            ["view"], "verified"));
        api.FetchResult = Result<McpPluginFetchContent>.Success(
            new McpPluginFetchContent("text/html", ViewHtml));
        main.PluginApiProvider = () => api;
        main.RuntimeDatabasePath = settings.CreateDatabasePath("runtime");
        DocumentInstanceId documentId = DocumentInstanceId.New();
        using PdfWorkspaceViewModel workspace = new(main, Item(documentId));
        MarkDocumentLoaded(workspace, documentId);

        await RefreshPluginSidebarViewsAsync(workspace);

        workspace.PluginSidebarMenuEntries.Should().ContainSingle()
            .Which.Title.Should().Be("页面检查器");
        workspace.HasNoPluginSidebarViews.Should().BeFalse();

        await workspace.ActivatePluginViewAsync(SidebarView);

        workspace.ActiveSidebarTab.Should().Be(SidebarTab.Plugin);
        workspace.IsPluginTabActive.Should().BeTrue();
        workspace.SidebarTabTitle.Should().Be("页面检查器");
        PluginViewSession session = workspace.ActivePluginSession!;
        session.Html.Should().Be(ViewHtml);
        session.PageUri.Should().Be($"patchouli://texts/{documentId}/page-1.xml");
        workspace.CurrentPageUri.Should().Be(session.PageUri);

        workspace.Clear();
        workspace.ActivePluginSession.Should().BeNull();
        session.Html.Should().BeNull();
        workspace.CurrentPageUri.Should().BeNull();
    }

    [Fact]
    public async Task Page_navigation_pushes_the_canonical_uri_into_the_sidebar_session()
    {
        using TemporaryAppSettingsFile settings = new();
        await using MainWindowViewModel main = CreateMain(settings.Path);
        FakePluginApi api = new();
        api.Allow("demo", "view");
        api.AddView("demo", SidebarView);
        api.FetchResult = Result<McpPluginFetchContent>.Success(
            new McpPluginFetchContent("text/html", ViewHtml));
        main.PluginApiProvider = () => api;
        main.RuntimeDatabasePath = settings.CreateDatabasePath("runtime");
        DocumentInstanceId documentId = DocumentInstanceId.New();
        using PdfWorkspaceViewModel workspace = new(main, Item(documentId));
        MarkDocumentLoaded(workspace, documentId);
        await workspace.ActivatePluginViewAsync(SidebarView);

        typeof(PdfWorkspaceViewModel).GetProperty("PageCount")!
            .SetValue(workspace, 3);
        await workspace.GoToPageAsync(2);

        workspace.ActivePluginSession!.PageUri.Should()
            .Be($"patchouli://texts/{documentId}/page-2.xml");
        workspace.CurrentPageUri.Should().Be(workspace.ActivePluginSession.PageUri);
    }

    // ---------- helpers ----------

    private static MainWindowViewModel CreateMain(string settingsPath)
    {
        return new MainWindowViewModel(settingsPath: settingsPath,
            uiScheduler: ImmediateScheduler.Instance);
    }

    private static LibraryItemViewModel Item(DocumentInstanceId documentId)
    {
        return new LibraryItemViewModel(ItemId.New().ToString(), "Book", "book", "", "", "", null,
            documentId.ToString(), null, "book.pdf", "", 3, 0, "",
            _ => Task.CompletedTask, _ => Task.CompletedTask);
    }

    private static async Task RefreshPluginSidebarViewsAsync(PdfWorkspaceViewModel workspace)
    {
        MethodInfo method = typeof(PdfWorkspaceViewModel)
            .GetMethod("RefreshPluginSidebarViewsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await ((Task)method.Invoke(workspace, null)!).ConfigureAwait(false);
    }

    /// <summary>Mirrors <c>LoadAsync</c>'s loaded-document marker without a document store —
    /// the page-URI projection is keyed off it and cleared again by <c>Clear</c>.</summary>
    private static void MarkDocumentLoaded(PdfWorkspaceViewModel workspace, DocumentInstanceId documentId)
    {
        typeof(PdfWorkspaceViewModel)
            .GetField("_loadedDocumentInstanceId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(workspace, documentId);
    }

    private sealed class FakePluginViewWindow : IPluginViewWindow
    {
        public FakePluginViewWindow(PluginViewSession session)
        {
            Session = session;
        }

        public PluginViewSession Session { get; }
        public int ShowCalls { get; private set; }
        public int ActivateCalls { get; private set; }

        public void Show(Window? owner)
        {
            ShowCalls++;
        }

        public void Activate()
        {
            ActivateCalls++;
        }

        public void Close()
        {
        }

        public event EventHandler? Closed;

        public void RaiseClosed()
        {
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakePluginApi : IMcpPluginApi
    {
        private readonly Dictionary<string, List<McpPluginViewInfo>> _views = new(StringComparer.Ordinal);
        private readonly HashSet<string> _allowed = new(StringComparer.Ordinal);

        public List<McpPluginMountInfo> Mounts { get; } = [];
        public List<string> FetchedPaths { get; } = [];

        public Result<McpPluginFetchContent> FetchResult { get; set; } =
            Result<McpPluginFetchContent>.Failure(AppErrorCodes.NotFound, "unset");

        IReadOnlyList<McpPluginMountInfo> IMcpPluginApi.Mounts => Mounts;

        public void Allow(string pluginId, string verb)
        {
            _allowed.Add(pluginId + "\n" + verb);
        }

        public void AddView(string pluginId, McpPluginViewInfo view)
        {
            if (!_views.TryGetValue(pluginId, out List<McpPluginViewInfo>? views))
            {
                _views[pluginId] = views = [];
            }

            views.Add(view);
        }

        public McpPluginMountInfo? FindMount(string pluginId)
        {
            return Mounts.FirstOrDefault(mount => mount.PluginId == pluginId);
        }

        public bool AllowsVerb(string pluginId, string verb)
        {
            return _allowed.Contains(pluginId + "\n" + verb);
        }

        public bool IsEndpoint(string pluginId, string relativePath)
        {
            return false;
        }

        public IReadOnlyList<McpPluginViewInfo> Views(string pluginId)
        {
            return _views.TryGetValue(pluginId, out List<McpPluginViewInfo>? views) ? views : [];
        }

        public McpPluginViewInfo? FindView(string pluginId, string viewId)
        {
            return Views(pluginId).FirstOrDefault(view => view.ViewId == viewId);
        }

        public Result<McpPluginFetchContent?> TryFetchStatic(string pluginId, string relativePath)
        {
            return Result<McpPluginFetchContent?>.Success(null);
        }

        public Task<Result<McpPluginFindPage>> FindAsync(string pluginId, string relativeScope,
            string? query, int limit, string? cursor, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<McpPluginFetchContent>> FetchAsync(string pluginId, string relativePath,
            CancellationToken cancellationToken = default)
        {
            FetchedPaths.Add(relativePath);
            return Task.FromResult(FetchResult);
        }

        public Task<Result<McpPluginSendOutcome>> SendAsync(string pluginId, string endpointPath,
            string action, JsonElement args, string? requestId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
