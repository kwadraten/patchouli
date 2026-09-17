using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class LibraryContextIsolationTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    [Fact]
    public async Task Beginning_database_switch_invalidates_and_clears_lazy_page_state_before_old_host_shutdown()
    {
        MainWindowViewModel main = new(new FakeClipboard(), settingsPath: _settings.Path);
        main.RuntimeDatabasePath = _settings.CreateDatabasePath("old-context-isolation");
        await main.OpenDatabaseCommand.ExecuteAsync();
        await main.Library.CreateCommand.ExecuteAsync();

        SearchEvidenceViewModel search = main.SearchEvidence;
        OcrQueueViewModel ocr = main.OcrQueue;
        SnapshotViewModel snapshot = main.Snapshot;
        search.BibliographicResults.Add(CreateItem());
        search.Output = "old database result";
        await ocr.RefreshAsync();
        await snapshot.RefreshAsync();
        int previousGeneration = main.LibraryGeneration;

        Task switching = main.BeginLibrarySwitchAsync();

        main.LibraryGeneration.Should().Be(previousGeneration + 1,
            "late callbacks must become stale before shutdown awaits any old-host work");
        search.BibliographicResults.Should().BeEmpty();
        search.Output.Should().BeEmpty();
        ocr.ActiveTaskRows.Should().BeEmpty();
        ocr.FinishedTaskRows.Should().BeEmpty();
        ocr.StatusSummary.Should().Be("等待运行数据库打开。");
        snapshot.OperationMessage.Should().Be("等待运行数据库打开。");

        await switching;
        main.HasOpenRuntimeDatabase.Should().BeFalse();
        await main.ShutdownAsync();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _settings.Dispose();
    }

    private static LibraryItemViewModel CreateItem()
    {
        return new LibraryItemViewModel(
            Guid.NewGuid().ToString(),
            "Old item",
            "book",
            "",
            "",
            "",
            null,
            null,
            null,
            "",
            "",
            0,
            0,
            "not_indexed",
            _ => Task.CompletedTask,
            _ => Task.CompletedTask);
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public Task<string?> GetTextAsync()
        {
            return Task.FromResult<string?>(null);
        }

        public Task SetTextAsync(string text)
        {
            return Task.CompletedTask;
        }
    }
}
