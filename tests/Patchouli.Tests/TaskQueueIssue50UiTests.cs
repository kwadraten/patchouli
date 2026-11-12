using System.Xml.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Ocr;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class TaskQueueIssue50UiTests
{
    [Fact]
    public void Queue_menu_is_unique_and_belongs_to_library_not_processing()
    {
        XDocument document = XDocument.Load(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));
        XNamespace ui = "https://github.com/avaloniaui";
        XElement queue = document.Descendants(ui + "MenuItem")
            .Should().ContainSingle(item => (string?)item.Attribute("Header") == "任务队列").Subject;
        queue.Attribute("Command")!.Value.Should().Be("{Binding OpenOcrQueueCommand}");
        queue.Parent!.Attribute("Header")!.Value.Should().Be("书库");
        document.Descendants(ui + "MenuItem")
            .Single(item => (string?)item.Attribute("Header") == "文献加工")
            .Descendants(ui + "MenuItem").Should()
            .NotContain(item => (string?)item.Attribute("Command") == "{Binding OpenOcrQueueCommand}");
    }

    [Fact]
    public void Error_dialog_snapshots_details_and_confirms_without_mutating_source()
    {
        List<string> errors = ["first\nsecond line", "another error"];
        TaskQueueErrorsDialogViewModel model = new("Book", errors);
        bool? result = null;
        model.RequestClose = value => result = value;
        errors[0] = "changed";
        errors.Add("new");

        model.Title.Should().Be("任务错误 · Book");
        model.Errors.Should().Equal("first\nsecond line", "another error");
        model.ConfirmCommand.Execute(null);
        result.Should().BeTrue();
    }

    [Fact]
    public void Queue_row_binds_only_error_count_and_global_controls_name_processing_tasks()
    {
        XDocument document = XDocument.Load(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views",
            "OcrQueuePage.axaml"));
        XNamespace ui = "https://github.com/avaloniaui";
        document.Descendants().Attributes().Should()
            .NotContain(attribute => attribute.Value.Contains("{Binding ErrorText"));
        XElement errors = document.Descendants(ui + "Button")
            .Single(button => (string?)button.Attribute("Content") == "{Binding ErrorCountText}");
        errors.Attribute("IsVisible")!.Value.Should().Be("{Binding HasError}");
        errors.Attribute("AutomationProperties.Name")!.Value.Should().Be("查看任务错误");
        errors.Attribute("ToolTip.Tip").Should().BeNull();
        foreach ((string command, string label) in new[]
                 {
                     ("StartCommand", "启动加工任务"), ("StopCommand", "停止加工任务"),
                     ("PauseGlobalCommand", "暂停加工任务"), ("ResumeGlobalCommand", "恢复加工任务")
                 })
        {
            document.Descendants(ui + "Button")
                .Single(button => (string?)button.Attribute("Command") == "{Binding " + command + "}")
                .Attribute("Content")!.Value.Should().Be(label);
        }
    }

    [Fact]
    public async Task Row_error_count_click_opens_details_for_the_clicked_row_only()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            CapturingDialogService dialogs = new();
            await using MainWindowViewModel main = new(dialogs: dialogs, settingsPath: settings.Path);
            OcrQueueViewModel queue = main.OcrQueue;
            OcrQueueTaskViewModel first = CreateFailedRow(queue, "First", "private first error");
            OcrQueueTaskViewModel second = CreateFailedRow(queue, "Second", "private second error\nfull detail");
            OcrQueuePage page = new() { DataContext = queue };
            page.OnTabClosed();
            IDataTemplate template = (IDataTemplate)page.Resources["TaskRowTemplate"]!;
            StackPanel rows = new();
            Control firstView = template.Build(first)!;
            firstView.DataContext = first;
            Control secondView = template.Build(second)!;
            secondView.DataContext = second;
            rows.Children.Add(firstView);
            rows.Children.Add(secondView);
            page.Content = rows;
            Window window = new() { Width = 1000, Height = 700, Content = page };
            window.Show();
            try
            {
                Pump(window);
                Button[] buttons = page.GetVisualDescendants().OfType<Button>()
                    .Where(button => AutomationProperties.GetName(button) == "查看任务错误").ToArray();
                buttons.Should().HaveCount(2);
                buttons.Should().OnlyContain(button => (string?)button.Content == "错误 1" && button.IsVisible);
                buttons.Should().OnlyContain(button => ToolTip.GetTip(button) == null);
                page.GetVisualDescendants().OfType<TextBlock>()
                    .Should().NotContain(text => text.Text != null && text.Text.Contains("private"));
                Button clicked = buttons.Single(button => ReferenceEquals(button.DataContext, second));
                clicked.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();

                TaskQueueErrorsDialogViewModel shown = dialogs.LastViewModel.Should()
                    .BeOfType<TaskQueueErrorsDialogViewModel>().Subject;
                shown.Title.Should().Contain("Second").And.NotContain("First");
                shown.Errors.Should().Equal(second.Errors);
            }
            finally
            {
                window.Close();
            }

            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Error_dialog_keeps_large_multiline_details_inside_a_bounded_scrollable_view()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(() =>
        {
            string longError = string.Join("\n", Enumerable.Repeat(new string('x', 160), 30));
            TaskQueueErrorsDialogViewModel model = new("Large task", Enumerable.Repeat(longError, 12).ToArray());
            TaskQueueErrorsDialog dialog = new() { DataContext = model };
            dialog.Show();
            try
            {
                Pump(dialog);
                dialog.CanResize.Should().BeTrue();
                dialog.SizeToContent.Should().Be(SizeToContent.Manual);
                dialog.Bounds.Width.Should().BeLessThanOrEqualTo(dialog.MaxWidth);
                dialog.Bounds.Height.Should().BeLessThanOrEqualTo(dialog.MaxHeight);
                double.IsFinite(dialog.MaxHeight).Should().BeTrue();
                ScrollViewer scroll = dialog.GetVisualDescendants().OfType<ScrollViewer>()
                    .Single(view => view.Content is ItemsControl);
                scroll.Extent.Height.Should().BeGreaterThan(scroll.Viewport.Height);
                scroll.HorizontalScrollBarVisibility.Should()
                    .Be(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
                SelectableTextBlock[] entries = dialog.GetVisualDescendants().OfType<SelectableTextBlock>().ToArray();
                entries.Should().HaveCount(model.Errors.Count);
                entries.Should()
                    .OnlyContain(entry => entry.Text == longError && entry.TextWrapping == TextWrapping.Wrap);
                bool closed = false;
                dialog.Closed += (_, _) => closed = true;
                Button close = dialog.GetVisualDescendants().OfType<Button>()
                    .Single(button => Equals(button.Content, "关闭"));
                close.Command.Should().BeSameAs(model.ConfirmCommand);
                close.Command!.Execute(null);
                closed.Should().BeTrue();
                model.RequestClose.Should().BeNull();
            }
            finally
            {
                dialog.Close();
            }

            return 0;
        }, CancellationToken.None);
    }

    private static OcrQueueTaskViewModel CreateFailedRow(OcrQueueViewModel queue, string title, string error)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        OcrQueueTask task = new(OcrQueueTaskId.New(), LibraryId.New(), DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], OcrQueueTaskKind.Pages, OcrEngineIds.NdlLite,
            OcrAdapterKind.LocalLibrary, null, OcrQueuePriority.UserStartedDocument, now, now,
            OcrQueueTaskState.Failed, 1, 1, null, "test-error", error, null);
        return new OcrQueueTaskViewModel(task, title, queue, null, null, now, now);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class CapturingDialogService : IDialogService
    {
        public object? LastViewModel { get; private set; }

        public Task ShowDialogAsync(object viewModel)
        {
            LastViewModel = viewModel;
            return Task.CompletedTask;
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            LastViewModel = viewModel;
            return Task.FromResult(default(TResult?));
        }
    }
}
