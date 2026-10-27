using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.Core.Bibliography;
using Patchouli.Host.Composition;
using Patchouli.UI.ViewModels.Editor;

namespace Patchouli.Tests;

[Collection("RetainedTabUI")]
public sealed class LibraryContextIsolationTests(AvaloniaSharedSessionFixture headless) : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    [Fact]
    public async Task Library_switch_drains_an_accepted_editor_write_and_refuses_later_saves_from_the_old_editor()
    {
        await headless.Session.Dispatch<bool>(async () =>
        {
            await using MainWindowViewModel main = new(settingsPath: _settings.Path)
            {
                RuntimeDatabasePath = _settings.CreateDatabasePath("editor-drain")
            };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            ItemMetadata item = (await Task.Run(() => services.Items.CreateItemAsync("book", "Before switch"))).Value;
            await main.EditItemByIdAsync(item.ItemId.ToString());
            ItemEditorViewModel editor = main.ItemEditor;
            editor.Fields.Single(field => field.Key == "Title").Value = "Accepted before switch";
            IDisposable? writer = await services.ConnectionFactory.EnterWriteAsync();
            Task? saving = null;
            Task? switching = null;
            try
            {
                saving = editor.SaveCommand.ExecuteAsync();
                await Task.Delay(50);
                editor.IsSaving.Should().BeTrue();
                switching = main.BeginLibrarySwitchAsync();
                await Task.Delay(50);
                switching.IsCompleted.Should()
                    .BeFalse("the old Host must retain ownership until the accepted write really finishes");
                writer.Dispose();
                writer = null;
                await saving.WaitAsync(TimeSpan.FromSeconds(10));
                await switching.WaitAsync(TimeSpan.FromSeconds(10));
                main.HasOpenRuntimeDatabase.Should().BeFalse();
                await editor.SaveCommand.ExecuteAsync();
                editor.Status.Should().Contain("所属的书库已关闭");
            }
            finally
            {
                writer?.Dispose();
                if (saving is not null)
                {
                    await saving;
                }

                if (switching is not null)
                {
                    await switching;
                }
            }

            return true;
        }, CancellationToken.None);
    }

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
