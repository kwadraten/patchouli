using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaRichEditor;
using AvaloniaRichEditor.Controls;
using AvaloniaRichEditor.Documents;
using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.UI.Controls;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

[Collection("RetainedTabUI")]
public sealed class TableEditorToolbarTests(AvaloniaSharedSessionFixture headless)
{
    private const string SourceHtml =
        "<table><thead><tr><th rowspan=\"2\">Metric</th><th colspan=\"2\">Value</th></tr>" +
        "<tr><th>2024</th><th>2025</th></tr></thead><tbody><tr><td>Revenue</td>" +
        "<td><eq>LaTex</eq></td><td>10</td></tr></tbody></table>";

    [Fact]
    public async Task Upgraded_editor_keeps_the_dialog_open_gate_and_save_path_working()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(() =>
        {
            using IDisposable decorationIsolation = HeadlessMarkdownDecorations.IsolateCurrentThread();
            (RichEditorView view, Window window) = CreateView();
            try
            {
                RichEditor editor = view.Editor;
                editor.LoadHtml(SourceHtml);
                TableBoxPayload baseline = TableContentAdapter.CreateHtml(SourceHtml).Value;

                // The dialog refuses to edit when the editor cannot roundtrip the table losslessly.
                TableContentAdapter.IsLosslessEditorRoundtrip(baseline, editor.ToHtml()).Should().BeTrue();

                Result<string> saved = TableContentAdapter.TryCreateEditorSaveHtml(baseline, editor.ToHtml());
                saved.IsSuccess.Should().BeTrue(saved.ErrorMessage);
                saved.Value.Should().Contain("<eq>LaTex</eq>").And.Contain("rowspan=\"2\"")
                    .And.Contain("colspan=\"2\"");
                return 0;
            }
            finally
            {
                CloseWindow(window);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Structure_commands_are_available_and_a_saved_edit_keeps_the_new_row()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(() =>
        {
            using IDisposable decorationIsolation = HeadlessMarkdownDecorations.IsolateCurrentThread();
            RichEditorTableStructureCommands.IsSupported.Should().BeTrue();

            (RichEditorView view, Window window) = CreateView();
            try
            {
                RichEditor editor = view.Editor;
                editor.LoadHtml(SourceHtml);
                TableBoxPayload baseline = TableContentAdapter.CreateHtml(SourceHtml).Value;
                TableBlock table = editor.Document!.Blocks.OfType<TableBlock>().Single();

                editor.InsertTableRow(table, 0).Should().BeTrue();
                editor.ToHtml().Split("<tr>").Length.Should().Be(5);

                Result<string> saved = TableContentAdapter.TryCreateEditorSaveHtml(baseline, editor.ToHtml());
                saved.IsSuccess.Should().BeTrue(saved.ErrorMessage);
                saved.Value.Split("<tr>").Length.Should().Be(5);
                saved.Value.Should().Contain("<eq>LaTex</eq>");
                return 0;
            }
            finally
            {
                CloseWindow(window);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public void Rich_editor_chrome_is_localized_to_chinese()
    {
        RichEditorChineseLocalization.Apply();

        RichEditorLocalization.GetString("MergeCells").Should().Be("合并单元格");
        RichEditorLocalization.GetString("UnmergeCells").Should().Be("拆分单元格");
        RichEditorLocalization.GetString("InsertRowAbove").Should().Be("上方插入行");
        RichEditorLocalization.GetString("Paste").Should().Be("粘贴");
    }

    [Fact]
    public void Dialog_toolbar_icons_resolve_to_bundled_lucide_assets()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.UI", "Views", "BoxEditorDialog.axaml"));
        string[] icons = System.Text.RegularExpressions.Regex
            .Matches(xaml, "<lucide:Lucide Icon=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        icons.Should().NotBeEmpty();
        foreach (string icon in icons)
        {
            string file = TestPaths.FromRepositoryRoot(
                "src", "Patchouli.UI", "Assets", "Lucide", ToKebabCase(icon) + ".svg");
            File.Exists(file).Should().BeTrue($"{icon} must map to a bundled lucide asset ({file})");
        }
    }

    private static string ToKebabCase(string value)
    {
        System.Text.StringBuilder builder = new();
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (char.IsUpper(current) || char.IsDigit(current))
            {
                if (index > 0 && builder.Length > 0 && builder[^1] != '-')
                {
                    builder.Append('-');
                }
            }

            builder.Append(char.ToLowerInvariant(current));
        }

        return builder.ToString();
    }

    [Fact]
    public async Task Dialog_renders_the_icon_toolbar_for_a_table()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using IDisposable decorationIsolation = HeadlessMarkdownDecorations.IsolateCurrentThread();
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            LibraryItemViewModel item = new(ItemId.New().ToString(), "Book", "book", "", "", "", null, null, null,
                "book.pdf", "", 1, 0, "", _ => Task.CompletedTask, _ => Task.CompletedTask);
            using PdfWorkspaceViewModel workspace = new(main, item);
            DocumentBox box = new(
                DocumentTreeRevisionId.New(),
                DocumentBoxId.New(),
                DocumentInstanceId.New(),
                PageId.New(),
                null,
                null,
                DocumentBoxType.Table,
                null,
                new NormalizedBBox(0.1, 0.1, 0.8, 0.6),
                TableContentAdapter.CreateHtml(SourceHtml).Value,
                null,
                null,
                null,
                false,
                null);
            PdfBBoxViewModel viewModel = new(main, workspace, box, 1000, 800, false);
            BoxEditorDialog dialog = new() { DataContext = viewModel, Width = 1200, Height = 1000 };
            try
            {
                dialog.Show();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();

                RichEditorView editorView = dialog.FindControl<RichEditorView>("TableEditorView")!;
                editorView.Bounds.Height.Should().BeGreaterThan(700,
                    "the table editor must fill the dialog's remaining height instead of a fixed 480");
                editorView.Bounds.Width.Should().BeGreaterThan(1050,
                    "the table editor must fill the dialog's remaining width");

                Button[] toolbar = dialog.GetVisualDescendants().OfType<Button>()
                    .Where(button => button.Classes.Contains("IconButton"))
                    .ToArray();
                toolbar.Should().HaveCount(10);
                dialog.FindControl<Button>("MergeCellsButton")!.IsEnabled.Should().BeTrue();
                dialog.FindControl<Button>("UnmergeCellsButton")!.IsEnabled.Should().BeTrue();

                string directory = TestPaths.FromRepositoryRoot(".tmp", "tests", "box-editor-preview");
                Directory.CreateDirectory(directory);
                using WriteableBitmap? frame = dialog.CaptureRenderedFrame();
                frame.Should().NotBeNull();
                frame!.Save(Path.Combine(directory, "table-editor-toolbar.png"));
                return 0;
            }
            finally
            {
                await main.ShutdownAsync();
                dialog.DataContext = null;
                CloseWindow(dialog);
            }
        }, CancellationToken.None);
    }

    private static (RichEditorView View, Window Window) CreateView()
    {
        RichEditorView view = new()
        {
            Width = 800,
            Height = 500,
            ShowStatusBar = false,
            ShowFileActions = false
        };
        Window window = new() { Width = 900, Height = 700, Content = view };
        window.Show();
        RichEditor editor = view.Editor;
        editor.AllowImages = false;
        editor.AllowRemoteImagesOnPaste = false;
        editor.ShowFormattingMenu = false;
        view.Toolbar.IsVisible = false;
        return (view, window);
    }

    private static void CloseWindow(Window window)
    {
        window.Content = null;
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
