using Avalonia.Headless;
using FluentAssertions;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.Tests;

/// <remarks>
/// R9 classification: pure settings routing/dirty-state/layout assertions. These cases never trigger
/// extension activation or worker startup; extension listing stays behind the lightweight descriptor
/// boundary (bundled manifests + working-source reads). Keep them free of
/// <c>TranslationExtensionCatalog.InitializeAsync</c>.
/// R13 shared session: no Dispatcher statics or cross-case subscriptions; each case still owns its
/// <see cref="TemporaryAppSettingsFile"/> and <see cref="MainWindowViewModel"/> inside the dispatch.
/// </remarks>
[Collection("RetainedTabUI")]
public sealed class SettingsGroupingTests(AvaloniaSharedSessionFixture headless)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Section_errors_use_the_status_bar_and_clearing_preserves_unrelated_notifications(bool laterError)
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            SettingsViewModel page = main.Settings;
            page.LlmSettings.ReportSaveFailure("Provider 'deepseek' is missing model.");
            main.Status.Should().Contain("模型连接").And.Contain("missing model");
            main.StatusIsError.Should().BeTrue();
            page.ActiveCategory = page.Categories.Single(category => category.Title == "同步");
            main.Status.Should().Contain("missing model");
            if (laterError)
            {
                main.ReportError("另一个操作失败");
            }

            await page.LlmSettings.DiscardAsync();
            main.Status.Should().Be(laterError ? "另一个操作失败" : "设置校验已通过。");
            main.StatusIsError.Should().Be(laterError);
            return 0;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("mcp", "MCP 服务", "AI 集成")]
    [InlineData("permissions", "访问权限", "AI 集成")]
    [InlineData("llm", "模型连接", "AI 集成")]
    [InlineData("agents", "Agent", "AI 集成")]
    [InlineData("startup", "启动", "行为")]
    [InlineData("system_integration", "链接与系统", "行为")]
    [InlineData("update", "更新", "行为")]
    [InlineData("import", "导入", "数据")]
    [InlineData("local_files", "本机存储", "数据")]
    [InlineData("ocr_presets", "OCR 预设", "文献加工")]
    [InlineData("ocr_components", "OCR 组件", "文献加工")]
    [InlineData("translation", "翻译", "文献加工")]
    [InlineData("metadata", "元数据来源", "文献加工")]
    [InlineData("search_rewrite", "搜索重写", "搜索")]
    [InlineData("search_awareness", "页面感知", "搜索")]
    [InlineData("search_links", "链接解析", "搜索")]
    [InlineData("csl_localization", "本地化", "引用")]
    [InlineData("csl_installed", "已安装样式", "引用")]
    [InlineData("csl_online", "在线样式", "引用")]
    [InlineData("csl_cayw", "Cite As You Write", "引用")]
    public async Task Tabbed_section_routes_select_the_correct_subpage(string id, string title, string category)
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            await main.OpenSettingsAsync(id);
            main.Settings.ActiveCategory.Title.Should().Be(category);
            SettingsSectionGroupViewModel group = (SettingsSectionGroupViewModel)main.Settings.ActiveCategory.Content!;
            group.SelectedTab.Should().BeSameAs(group.ActiveEntry);
            group.ActiveEntry.Id.Should().Be(id);
            group.ActiveEntry.Title.Should().Be(title);
            return 0;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("appearance", "主题与阅读", "显示")]
    [InlineData("library", "文件与扫描", "书库")]
    [InlineData("sync", "同步", "同步")]
    public async Task Single_page_categories_select_their_owner(string id, string title, string category)
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            await main.OpenSettingsAsync(id);
            main.Settings.ActiveCategory.Title.Should().Be(category);
            main.Settings.HasSectionTabs.Should().BeFalse();
            SettingsSectionEntryViewModel entry = main.Settings.SectionEntries.Single(candidate => candidate.Id == id);
            entry.Title.Should().Be(title);
            main.Settings.ActiveCategory.Content.Should().BeSameAs(entry.Content);
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Official_ids_route_directly_and_unknown_ids_fail()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);

            await main.OpenSettingsAsync("system_integration");
            ((SettingsSectionGroupViewModel)main.Settings.ActiveCategory.Content!).ActiveEntry.Id
                .Should().Be("system_integration");
            await main.OpenSettingsAsync("ocr_components");
            ((SettingsSectionGroupViewModel)main.Settings.ActiveCategory.Content!).ActiveEntry.Id
                .Should().Be("ocr_components");
            await main.OpenSettingsAsync("ocr_presets");
            ((SettingsSectionGroupViewModel)main.Settings.ActiveCategory.Content!).ActiveEntry.Id
                .Should().Be("ocr_presets");
            await main.OpenSettingsAsync("translation");
            ((SettingsSectionGroupViewModel)main.Settings.ActiveCategory.Content!).ActiveEntry.Id
                .Should().Be("translation");

            bool missingThrew = false;
            try
            {
                await main.OpenSettingsAsync("missing_settings_section");
            }
            catch (ArgumentException)
            {
                missingThrew = true;
            }

            missingThrew.Should().BeTrue();

            bool malformedThrew = false;
            try
            {
                await main.OpenSettingsAsync("settings/unknown");
            }
            catch (ArgumentException)
            {
                malformedThrew = true;
            }

            malformedThrew.Should().BeTrue();
            return 0;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("virtual_links")]
    [InlineData("extensions")]
    [InlineData("mineru")]
    [InlineData("ocr")]
    [InlineData("layout_models")]
    [InlineData("ocr_engines")]
    [InlineData("ocr_textlayer")]
    [InlineData("network_proxy")]
    public async Task Removed_section_ids_are_not_aliases(string id)
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            await Assert.ThrowsAsync<ArgumentException>(() => main.Settings.SelectSectionAsync(id));
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Category_icons_use_existing_Lucide_assets_instead_of_the_unknown_icon_fallback()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            foreach (NavCategoryViewModel category in main.Settings.Categories)
            {
                string name = string.Join('-', System.Text.RegularExpressions.Regex
                    .Matches(category.IconName, "[A-Z][a-z]*|[0-9]+")
                    .Select(match => match.Value.ToLowerInvariant()));
                File.Exists(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Assets", "Lucide", name + ".svg"))
                    .Should().BeTrue($"{category.Title} must render its declared {category.IconName} icon");
            }

            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Saving_and_discarding_include_sections_inside_inactive_groups()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            SettingsViewModel page = main.Settings;
            page.StartupSettings.RememberLastDatabase = !page.StartupSettings.RememberLastDatabase;
            page.ImportSettings.MaxFailedPageRatioPercent = 35;
            page.McpSettings.Port += 1;
            page.ActiveCategory.Title.Should().Be("显示");
            page.HasDirtySections.Should().BeTrue();

            (await page.SaveAllDirtySectionsAsync()).Should().BeTrue(page.GlobalStatus);
            page.HasDirtySections.Should().BeFalse();
            main.AppOptions.Import.MaxFailedPageRatio.Should().Be(0.35);

            page.StartupSettings.RememberLastDatabase = !page.StartupSettings.RememberLastDatabase;
            page.ImportSettings.MaxFailedPageRatioPercent = 50;
            await page.DiscardCommand.ExecuteAsync();
            page.HasDirtySections.Should().BeFalse();
            page.ImportSettings.MaxFailedPageRatioPercent.Should().Be(35);
            return 0;
        }, CancellationToken.None);
    }
}
