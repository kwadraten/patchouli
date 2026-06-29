using FluentAssertions;
using LiteratureApp.UI;

namespace LiteratureApp.Tests;

public sealed class UiViewModelTests
{
    [Fact]
    public async Task LibraryViewModel_CreateLibrary_updates_current_library()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-{Guid.NewGuid():N}.sqlite");
        try { var vm = new MainWindowViewModel { RuntimeDatabasePath = path }; await vm.OpenDatabaseCommand.ExecuteAsync(); vm.Library.DisplayName = "UI Library"; await vm.Library.CreateCommand.ExecuteAsync(); vm.Library.Details.Should().Contain("UI Library"); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task BibliographyViewModel_CreateItem_returns_item()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-{Guid.NewGuid():N}.sqlite");
        try { var vm = new MainWindowViewModel { RuntimeDatabasePath = path }; await vm.OpenDatabaseCommand.ExecuteAsync(); await vm.Library.CreateCommand.ExecuteAsync(); vm.Bibliography.Title = "UI Item"; await vm.Bibliography.CreateItemCommand.ExecuteAsync(); vm.Bibliography.Output.Should().Contain("UI Item"); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task FileDocumentViewModel_RegisterMissingFile_creates_missing_asset()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-{Guid.NewGuid():N}.sqlite");
        try { var vm = new MainWindowViewModel { RuntimeDatabasePath = path }; await vm.OpenDatabaseCommand.ExecuteAsync(); await vm.Library.CreateCommand.ExecuteAsync(); vm.FileDocument.FilePath = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.pdf"); await vm.FileDocument.RegisterCommand.ExecuteAsync(); vm.FileDocument.Output.Should().Contain("missing"); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task CopyEvidenceMarkdown_writes_pinned_markdown_to_clipboard()
    {
        var clipboard = new FakeClipboard(); var vm = new MainWindowViewModel(clipboard);
        vm.SearchEvidence.Markdown = "Pinned source text";
        await vm.SearchEvidence.CopyMarkdownCommand.ExecuteAsync();
        clipboard.Text.Should().Be("Pinned source text");
        vm.SearchEvidence.Output.Should().Be("Copied Evidence Markdown");
    }

    [Fact]
    public async Task CopyEvidenceMarkdown_without_markdown_returns_validation_error()
    {
        var vm = new MainWindowViewModel(new FakeClipboard());
        await vm.SearchEvidence.CopyMarkdownCommand.ExecuteAsync();
        vm.SearchEvidence.Output.Should().Contain("validation_failed");
    }

    [Fact]
    public async Task McpPreviewViewModel_SafetyCheck_flags_specific_local_path()
    {
        var vm = new MainWindowViewModel(new FakeClipboard());
        vm.McpPreview.Output = "{\"bad\":\"/tmp/private.sqlite\"}";
        vm.McpPreview.SpecificPath = "/tmp/private.sqlite";
        await vm.McpPreview.SafetyCommand.ExecuteAsync();
        vm.McpPreview.Safety.Should().Contain("Warning");
    }

    [Fact]
    public async Task McpPreviewViewModel_SafetyCheck_passes_clean_output()
    {
        var vm = new MainWindowViewModel(new FakeClipboard());
        vm.McpPreview.Output = "{\"title\":\"A historical source\"}";
        await vm.McpPreview.SafetyCommand.ExecuteAsync();
        vm.McpPreview.Safety.Should().Be("No obvious local path or secret exposure detected.");
    }

    [Fact]
    public async Task QueueViewModel_enqueue_mock_adds_task_and_displays_runtime_only_warning()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-queue-{Guid.NewGuid():N}.sqlite");
        try
        {
            var vm = new MainWindowViewModel { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync(); await vm.Library.CreateCommand.ExecuteAsync();
            vm.OcrQueue.DocumentInstanceId = LiteratureApp.Core.Ids.DocumentInstanceId.New().ToString();
            vm.OcrQueue.PresetId = LiteratureApp.Core.Ids.OcrPresetId.New().ToString();
            vm.OcrQueue.PageIds = LiteratureApp.Core.Ids.PageId.New().ToString();
            await vm.OcrQueue.EnqueueMockCommand.ExecuteAsync();
            vm.OcrQueue.Output.Should().Contain("Queued mock OCR task");
            vm.OcrQueue.Tasks.Should().ContainSingle();
            vm.OcrQueue.Output.ToLowerInvariant().Should().NotContain("secret");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task QueueViewModel_start_stop_pause_and_validation_are_visible()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-queue-{Guid.NewGuid():N}.sqlite");
        try
        {
            var vm = new MainWindowViewModel { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync(); await vm.Library.CreateCommand.ExecuteAsync();
            await vm.OcrQueue.StartCommand.ExecuteAsync();
            vm.OcrQueue.StatusSummary.Should().Contain("running");
            await vm.OcrQueue.PauseGlobalCommand.ExecuteAsync();
            vm.OcrQueue.StatusSummary.Should().Contain("global:");
            vm.OcrQueue.TaskId = "not-a-guid";
            await vm.OcrQueue.CancelCommand.ExecuteAsync();
            vm.OcrQueue.Output.Should().Contain("validation_failed");
            await vm.OcrQueue.StopCommand.ExecuteAsync();
            vm.OcrQueue.StatusSummary.Should().Contain("stopped");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task SearchProfileViewModel_creates_rule_and_previews_plan()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-profile-{Guid.NewGuid():N}.sqlite");
        try
        {
            var vm = new MainWindowViewModel { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync(); await vm.Library.CreateCommand.ExecuteAsync();
            vm.SearchProfiles.Name = "UI variants"; await vm.SearchProfiles.CreateProfileCommand.ExecuteAsync();
            vm.SearchProfiles.Pattern = "臺灣"; vm.SearchProfiles.Replacement = "台湾";
            await vm.SearchProfiles.AddRuleCommand.ExecuteAsync();
            vm.SearchProfiles.Query = "臺灣"; await vm.SearchProfiles.PreviewCommand.ExecuteAsync();
            vm.SearchProfiles.Output.Should().Contain("台湾").And.Contain("OriginalQuery");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public string? Text { get; private set; }
        public Task SetTextAsync(string text) { Text = text; return Task.CompletedTask; }
    }
}
