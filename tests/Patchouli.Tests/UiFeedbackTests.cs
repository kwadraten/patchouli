using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Patchouli.Core.Agents;
using Patchouli.Core.Ids;
using Patchouli.UI.Controls;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class UiFeedbackTests
{
    [Fact]
    public async Task Every_agent_has_a_renderable_offline_brand_mark_in_light_and_dark_colors()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(() =>
        {
            StackPanel rows = new();
            foreach (bool dark in new[] { false, true })
            {
                StackPanel row = new() { Orientation = Avalonia.Layout.Orientation.Horizontal };
                foreach (AgentCatalogEntry entry in AgentCatalog.All)
                {
                    AgentIcon.IconData data = AgentIcon.LoadIcon(entry.Id);
                    data.Shapes.Should().NotBeEmpty(entry.DisplayName);
                    data.ViewBox.Width.Should().BeGreaterThan(0);
                    data.Shapes.Should().OnlyContain(shape => shape.Geometry.Bounds.Width > 0);
                    row.Children.Add(new Border
                    {
                        Width = 64, Height = 64, Background = dark ? Brushes.Black : Brushes.White,
                        Child = new AgentIcon
                        {
                            AgentId = entry.Id, Width = 28, Height = 28,
                            Foreground = dark ? Brushes.White : Brushes.Black
                        }
                    });
                }

                rows.Children.Add(row);
            }

            Window window = new() { Width = 64 * AgentCatalog.All.Length, Height = 128, Content = rows };
            window.Show();
            try
            {
                window.UpdateLayout();
                using RenderTargetBitmap image = new(new PixelSize(64 * AgentCatalog.All.Length, 128));
                image.Render(rows);
                string path = TestPaths.FromRepositoryRoot(".tmp", "ui-feedback", "agent-icons.png");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                image.Save(path);
            }
            finally
            {
                window.Close();
            }

            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Target_language_selector_saves_a_named_choice_and_preserves_existing_language_tags()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile file = new();
            await using MainWindowViewModel main = new(settingsPath: file.Path)
                { RuntimeDatabasePath = file.CreateDatabasePath("language-selector") };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            await main.Settings.SelectSectionAsync("translation");
            SettingsPage page = new() { DataContext = main.Settings };
            Window window = new() { Width = 1280, Height = 900, Content = page };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                ComboBox selector = page.GetVisualDescendants().OfType<ComboBox>()
                    .Single(control => control.Name == "TranslationTargetLanguageSelector");
                selector.IsEditable.Should().BeFalse();
                LlmSettingsViewModel settings = main.Settings.LlmSettings;
                settings.SelectedTranslationLanguage.Code.Should().Be("en");
                selector.SelectedItem = settings.TranslationLanguageOptions.Single(option => option.Code == "fr");
                settings.TranslationTargetLanguage.Should().Be("fr");
                await settings.SaveAsync();
                main.AppOptions.Llm.Translation.TargetLanguage.Should().Be("fr");
                settings.TranslationTargetLanguage = "x-existing-custom";
                settings.SelectedTranslationLanguage.Code.Should().Be("x-existing-custom");
                selector.SelectedItem.Should().Be(settings.SelectedTranslationLanguage);
                settings.SelectedTranslationLanguage = null!;
                settings.TranslationTargetLanguage.Should().Be("x-existing-custom");
                page.GetVisualDescendants().OfType<TextBlock>()
                    .Should().NotContain(block => block.Text != null && block.Text.Contains("BCP 47"));
            }
            finally
            {
                window.Close();
            }

            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Page_edit_mode_always_shows_boxes_and_blocks_chapter_switching()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile file = new();
            await using MainWindowViewModel main = new(settingsPath: file.Path);
            LibraryItemViewModel item = new(ItemId.New().ToString(), "Book", "book", "", "", "", null,
                null, null, "book.pdf", "", 3, 0, "", _ => Task.CompletedTask, _ => Task.CompletedTask);
            using PdfWorkspaceViewModel workspace = new(main, item);
            typeof(PdfWorkspaceViewModel).GetProperty(nameof(workspace.ActiveSidebarTab))!
                .SetValue(workspace, SidebarTab.Chapters);
            workspace.ShowChaptersTabCommand.CanExecute(null).Should().BeTrue();
            typeof(PdfWorkspaceViewModel).GetProperty(nameof(workspace.IsEditMode))!.SetValue(workspace, true);
            workspace.IsBoxSidebarVisible.Should().BeTrue();
            workspace.IsNavigationSidebarVisible.Should().BeFalse();
            workspace.ShowChaptersTabCommand.CanExecute(null).Should().BeFalse();
            await workspace.ShowChaptersTabCommand.ExecuteAsync();
            workspace.IsBoxSidebarVisible.Should().BeTrue();
            PdfWorkspacePage page = new() { DataContext = workspace };
            Window window = new() { Width = 1280, Height = 900, Content = page };
            window.Show();
            try
            {
                window.UpdateLayout();
                page.GetVisualDescendants().OfType<TextBlock>().Should().NotContain(block => block.Text == "文献加工");
                page.GetVisualDescendants().OfType<Button>().Should()
                    .NotContain(button => Equals(button.Content, "章节"));
                typeof(PdfWorkspaceViewModel).GetProperty(nameof(workspace.IsEditMode))!.SetValue(workspace, false);
                workspace.IsNavigationSidebarVisible.Should().BeTrue();
                workspace.IsBoxSidebarVisible.Should().BeFalse();
                workspace.ShowChaptersTabCommand.CanExecute(null).Should().BeTrue();
            }
            finally
            {
                window.Close();
            }

            return 0;
        }, CancellationToken.None);
    }
}
