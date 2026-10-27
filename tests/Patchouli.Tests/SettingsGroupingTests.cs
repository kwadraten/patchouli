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
            page.ActiveCategory = page.Categories.Single(category => category.Title == "同步与快照");
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
    [InlineData("mcp", "MCP 服务与权限", "AI集成")]
    [InlineData("llm", "模型连接", "AI集成")]
    [InlineData("agents", "Agent 集成", "AI集成")]
    [InlineData("import", "导入时设置", "文献加工")]
    [InlineData("ocr_presets", "OCR 预设编排", "文献加工")]
    [InlineData("ocr_engines", "OCR 引擎列表", "文献加工")]
    [InlineData("ocr_textlayer", "文本层提取算法列表", "文献加工")]
    [InlineData("translation", "文献翻译", "文献加工")]
    [InlineData("library", "书库与源文件", "书库与本机文件")]
    [InlineData("update", "更新设置", "更新设置")]
    [InlineData("local_files", "模型与临时文件", "书库与本机文件")]
    [InlineData("csl_localization", "题录本地化", "引用与题录")]
    [InlineData("csl_installed", "已安装样式", "引用与题录")]
    [InlineData("csl_online", "在线样式", "引用与题录")]
    public async Task Stable_section_routes_select_the_correct_subpage(string id, string title, string category)
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            await main.OpenSettingsAsync(id);
            main.Settings.ActiveCategory.Title.Should().Be(category);
            if (id == "update")
            {
                main.Settings.ActiveCategory.Content.Should().BeSameAs(main.Settings.UpdateSettings);
                return 0;
            }

            SettingsSectionGroupViewModel group = (SettingsSectionGroupViewModel)main.Settings.ActiveCategory.Content!;
            group.SelectedTab.Should().BeSameAs(group.ActiveEntry);
            group.ActiveEntry.Id.Should().Be(id);
            group.ActiveEntry.Title.Should().Be(title);
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
            page.LibrarySettings.RememberLastDatabase = !page.LibrarySettings.RememberLastDatabase;
            page.ImportSettings.MaxFailedPageRatioPercent = 35;
            page.McpSettings.Port += 1;
            page.ActiveCategory.Title.Should().Be("外观与阅读");
            page.HasDirtySections.Should().BeTrue();

            (await page.SaveAllDirtySectionsAsync()).Should().BeTrue(page.GlobalStatus);
            page.HasDirtySections.Should().BeFalse();
            main.AppOptions.Import.MaxFailedPageRatio.Should().Be(0.35);

            page.LibrarySettings.RememberLastDatabase = !page.LibrarySettings.RememberLastDatabase;
            page.ImportSettings.MaxFailedPageRatioPercent = 50;
            await page.DiscardCommand.ExecuteAsync();
            page.HasDirtySections.Should().BeFalse();
            page.ImportSettings.MaxFailedPageRatioPercent.Should().Be(35);
            return 0;
        }, CancellationToken.None);
    }
}
