using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaloniaEdit.Highlighting;
using FluentAssertions;
using Patchouli.UI;
using Patchouli.UI.Controls;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI.ViewModels.Settings;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class WorkflowEditorDialogTests
{
    [Fact]
    public async Task Editor_renders_line_numbers_and_fsharp_highlighting_and_preserves_undo_and_drafts()
    {
        // Skia and mock headless typefaces cannot share a process-wide font cache. Generate the
        // visual preview in a separate test process with this flag; the suite uses its usual backend.
        bool renderPreview = Environment.GetEnvironmentVariable("PATCHOULI_WORKFLOW_EDITOR_PREVIEW") == "1";
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(
            renderPreview ? typeof(EditorRenderApp) : typeof(App));
        await session.Dispatch(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            WorkflowSettingsViewModel section = new(main, main.RuntimeDatabasePath);
            await section.CreateNewAsync();
            using WorkflowEditorDialogViewModel model = new(section);
            WorkflowEditorDialog dialog = new() { DataContext = model };
            dialog.Show();
            try
            {
                WorkflowCodeEditor editor = dialog.FindControl<WorkflowCodeEditor>("ScriptEditor")!;
                editor.ShowLineNumbers.Should().BeTrue();
                editor.SyntaxHighlighting.Name.Should().Be("F#");
                editor.Text.Should().Be(section.Editor.ScriptText);
                string script = editor.Text;
                editor.Document.Insert(0, "// draft\n");
                section.Editor.ScriptText.Should().StartWith("// draft");
                editor.Undo();
                section.Editor.ScriptText.Should().Be(script);

                section.Editor.ScriptText =
                    "let message = \"你好\"\n// comment\nlet value = 42\n(* outer (* nested *) comment *)";
                editor.Document.LineCount.Should().Be(4);
                using DocumentHighlighter highlighter = new(editor.Document, editor.SyntaxHighlighting);
                highlighter.HighlightLine(1).Sections.Select(highlight => highlight.Color.Name)
                    .Should().Contain("Keyword").And.Contain("String");
                highlighter.HighlightLine(2).Sections.Should().Contain(highlight => highlight.Color.Name == "Comment");
                highlighter.HighlightLine(3).Sections.Should().Contain(highlight => highlight.Color.Name == "Number");
                section.Editor.ScriptText = "// 示例：由 agent 完成研究任务\n" + script;
                Dispatcher.UIThread.RunJobs();
                dialog.Measure(new Size(1100, 760));
                dialog.Arrange(new Rect(0, 0, 1100, 760));
                if (renderPreview)
                {
                    using RenderTargetBitmap bitmap = new(new PixelSize(1100, 760), new Vector(96, 96));
                    bitmap.Render(dialog);
                    string preview = TestPaths.FromRepositoryRoot(".tmp", "previews", "workflow-editor-preview.png");
                    Directory.CreateDirectory(Path.GetDirectoryName(preview)!);
                    bitmap.Save(preview);
                    new FileInfo(preview).Length.Should().BeGreaterThan(0);
                }

                dialog.Close();
                dialog.IsVisible.Should().BeTrue("closing must protect the unsaved script");
                model.IsCloseConfirmationVisible.Should().BeTrue();
                await model.DiscardAndCloseCommand.ExecuteAsync();
                section.Editor.IsDirty.Should().BeFalse();
                dialog.IsVisible.Should().BeFalse();
                model.RequestClose.Should().BeNull();
            }
            finally
            {
                dialog.DataContext = null;
                dialog.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task List_edit_action_opens_the_selected_workflow_in_a_read_only_modal()
    {
        using TemporaryAppSettingsFile settings = new();
        RecordingDialogs dialogs = new();
        await using MainWindowViewModel main = new(settingsPath: settings.Path, dialogs: dialogs);
        WorkflowSettingsViewModel section = new(main, main.RuntimeDatabasePath);
        await section.LoadAsync();
        await section.Workflows[0].OpenEditorCommand.ExecuteAsync();
        dialogs.Shown.Should().BeOfType<WorkflowEditorDialogViewModel>();
        WorkflowEditorDialogViewModel model = (WorkflowEditorDialogViewModel)dialogs.Shown!;
        model.Editor.Should().BeSameAs(section.Editor);
        model.Editor.IsReadOnly.Should().BeTrue();
        model.Editor.ScriptText.Should().Contain("let run : AgentWorkflow");
    }

    [Fact]
    public async Task Save_and_close_keeps_the_dialog_open_when_the_script_does_not_compile()
    {
        using TemporaryAppSettingsFile settings = new();
        await using MainWindowViewModel main = new(settingsPath: settings.Path);
        WorkflowSettingsViewModel section = new(main, main.RuntimeDatabasePath);
        await section.CreateNewAsync();
        using WorkflowEditorDialogViewModel dialog = new(section);
        bool closed = false;
        dialog.RequestClose = _ => closed = true;
        string goodScript = section.Editor.ScriptText;
        section.Editor.ScriptText = "let broken = missingIdentifier";
        await dialog.SaveAndCloseCommand.ExecuteAsync();
        closed.Should().BeFalse();
        section.Editor.ResultIsError.Should().BeTrue();
        section.Editor.IsDirty.Should().BeTrue();

        section.Editor.ScriptText = goodScript;
        section.Editor.Name = "已保存的工作流";
        await dialog.SaveAndCloseCommand.ExecuteAsync();
        closed.Should().BeTrue();
        section.Editor.IsDirty.Should().BeFalse();
    }

    private sealed class RecordingDialogs : IDialogService
    {
        public object? Shown { get; private set; }

        public Task ShowDialogAsync(object viewModel)
        {
            Shown = viewModel;
            return Task.CompletedTask;
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            Shown = viewModel;
            return Task.FromResult(default(TResult));
        }
    }

    public static class EditorRenderApp
    {
        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>().UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
        }
    }
}
