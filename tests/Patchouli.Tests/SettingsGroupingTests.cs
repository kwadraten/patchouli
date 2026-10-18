using Avalonia.Headless;
using FluentAssertions;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class SettingsGroupingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Section_errors_use_the_status_bar_and_clearing_preserves_unrelated_notifications(bool laterError)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            SettingsViewModel page = main.Settings;
            page.LlmSettings.ReportSaveFailure("Provider 'deepseek' is missing model.");
            main.Status.Should().Contain("模型连接与聊天").And.Contain("missing model");
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
    [InlineData("llm", "模型连接与聊天", "AI集成")]
    [InlineData("workflows", "工作流", "AI集成")]
    [InlineData("import", "导入规则", "导入与识别")]
    [InlineData("ocr", "OCR 引擎", "导入与识别")]
    [InlineData("ocr_model", "多模态 OCR 模型", "导入与识别")]
    [InlineData("library", "书库与源文件", "书库与本机文件")]
    [InlineData("local_files", "模型与临时文件", "书库与本机文件")]
    [InlineData("csl_localization", "题录本地化", "引用与题录")]
    [InlineData("csl_installed", "已安装样式", "引用与题录")]
    [InlineData("csl_online", "在线样式", "引用与题录")]
    public async Task Stable_section_routes_select_the_correct_subpage(string id, string title, string category)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
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

    [Fact]
    public async Task Saving_and_discarding_include_sections_inside_inactive_groups()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
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
