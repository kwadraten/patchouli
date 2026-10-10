using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Highlighting;
using FluentAssertions;
using Patchouli.UI;
using Patchouli.UI.Controls;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI.ViewModels.Settings;
using Patchouli.UI.Views;
using Patchouli.Workflows.Scripting;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class WorkflowEditorDialogTests
{
    [Fact]
    public async Task Model_picker_displays_only_model_names_and_preserves_typed_selection()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            WorkflowSettingsViewModel section = new(main, main.RuntimeDatabasePath);
            await section.CreateNewAsync();
            WorkflowParameterFieldViewModel field = section.Editor.ParameterFields.Single(row => row.IsModel);
            WorkflowModelOption first = new("ChatGPT / Codex 订阅", "codex-subscription", "gpt-5.6-luna");
            WorkflowModelOption second = new(first.ProviderLabel, first.ProviderId, "gpt-6-luna");
            field.ModelOptions.Clear();
            field.ModelOptions.Add(first);
            field.ModelOptions.Add(second);
            field.ModelProviders.Add(new WorkflowModelProviderOption(first.ProviderLabel, first.ProviderId));
            field.Value = first.Value;
            using WorkflowEditorDialogViewModel model = new(section);
            WorkflowEditorDialog dialog = new() { DataContext = model };
            dialog.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                ComboBox picker = dialog.GetVisualDescendants().OfType<ComboBox>()
                    .Single(control => control.IsEditable && ReferenceEquals(control.DataContext, field));
                picker.Text.Should().Be(first.Model);
                field.ModelName.Should().Be(first.Model);

                picker.SelectedItem = second;
                Dispatcher.UIThread.RunJobs();
                picker.Text.Should().Be(second.Model);
                ModelSelection selected = ModelSelectionCodec.Decode(field.Value);
                selected.ProviderId.Should().Be(second.ProviderId);
                selected.Model.Should().Be(second.Model);

                picker.Text = "custom-api-model";
                Dispatcher.UIThread.RunJobs();
                field.ModelName.Should().Be("custom-api-model");
                ModelSelectionCodec.Decode(field.Value).Model.Should().Be("custom-api-model");
            }
            finally
            {
                dialog.DataContext = null;
                dialog.Close();
            }
        }, CancellationToken.None);
    }

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
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
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
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Save_and_close_keeps_the_dialog_open_when_the_script_does_not_compile()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
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

            section.Editor.ScriptText = goodScript.Replace("新工作流", "已保存的工作流", StringComparison.Ordinal);
            (await section.Editor.CheckScriptAsync()).Should().BeTrue(section.Editor.ResultText);
            section.Editor.ParameterFields.Single(field => field.Key == "model").Value =
                ModelSelectionCodec.Encode(new ModelSelection("openai", "test-model"));
            await dialog.SaveAndCloseCommand.ExecuteAsync();
            closed.Should().BeTrue();
            section.Editor.IsDirty.Should().BeFalse();
        }, CancellationToken.None);
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
