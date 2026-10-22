using Dapper;
using Microsoft.Data.Sqlite;
using FluentAssertions;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Files;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Host.Watching;
using Patchouli.UI;

namespace Patchouli.Tests;

public sealed class FileSearchRootWatcherServiceTests
{
    [Fact]
    public async Task Rescan_skips_renamed_content_and_remembers_location_on_following_scans()
    {
        await using HostContext context = await HostContext.CreateAsync();
        string root = context.CreateRootDirectory();
        (await context.Services.FileResolution.AddSearchRootAsync(SelectedRoot(root))).IsSuccess.Should().BeTrue();
        string original = TestFixtures.CopyRealThreePagePdfTo(root, "original.pdf");
        await using FileSearchRootWatcherService watcher = new(context.Services, new NoOpAppLogger());
        Result<FileSearchRootRescanSummary> first = await watcher.RescanFileSearchRootsAsync();
        first.IsSuccess.Should().BeTrue(first.ErrorMessage);
        first.Value.ImportedPdfCount.Should().Be(1);
        string renamed = Path.Combine(root, "renamed.pdf");
        File.Move(original, renamed);
        Result<FileSearchRootRescanSummary> second = await watcher.RescanFileSearchRootsAsync();
        second.IsSuccess.Should().BeTrue(second.ErrorMessage);
        second.Value.ImportedPdfCount.Should().Be(0);
        second.Value.SkippedKnownPdfCount.Should().Be(1);

        // An unchanged scan must not publish another library change.
        await using SqliteConnection connection = context.Services.ConnectionFactory.CreateReadConnection();
        await connection.OpenAsync();
        (await connection.ExecuteScalarAsync<string>("select original_path from file_assets;"))
            .Should().Be(renamed);
        (await connection.ExecuteScalarAsync<string>("select file_name from file_assets;"))
            .Should().Be("renamed.pdf");
        (await connection.ExecuteScalarAsync<string>("select title from items;"))
            .Should().Be("original");
        (await connection.ExecuteScalarAsync<string>("select title from document_instances;"))
            .Should().Be("original");
        (await connection.ExecuteScalarAsync<string>(
                "select status from known_file_locations where path = @Path;", new { Path = original }))
            .Should().Be(FileAssetStatus.Missing);
        long revision = await connection.ExecuteScalarAsync<long>("select library_revision from library_metadata;");
        Result<FileSearchRootRescanSummary> third = await watcher.RescanFileSearchRootsAsync();
        third.IsSuccess.Should().BeTrue(third.ErrorMessage);
        third.Value.SkippedKnownPdfCount.Should().Be(1);
        (await connection.ExecuteScalarAsync<long>("select library_revision from library_metadata;"))
            .Should().Be(revision);
        (await connection.ExecuteScalarAsync<int>("select count(*) from items;")).Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rescan_recognizes_both_copies_and_recovers_after_original_is_removed_or_replaced(bool replace)
    {
        await using HostContext context = await HostContext.CreateAsync();
        string root = context.CreateRootDirectory();
        (await context.Services.FileResolution.AddSearchRootAsync(SelectedRoot(root))).IsSuccess.Should().BeTrue();
        string original = TestFixtures.CopyRealThreePagePdfTo(root, "original.pdf");
        await using FileSearchRootWatcherService watcher = new(context.Services, new NoOpAppLogger());
        (await watcher.RescanFileSearchRootsAsync()).Value.ImportedPdfCount.Should().Be(1);
        string copy = Path.Combine(root, "copy.pdf");
        File.Copy(original, copy);
        (await watcher.RescanFileSearchRootsAsync()).Value.ImportedPdfCount.Should().Be(0);
        await using SqliteConnection connection = context.Services.ConnectionFactory.CreateReadConnection();
        await connection.OpenAsync();
        string assetId = (await connection.ExecuteScalarAsync<string>("select file_asset_id from file_assets;"))!;
        (await connection.ExecuteScalarAsync<int>(
            "select count(*) from known_file_locations where status = 'available';")).Should().Be(2);
        (await connection.ExecuteScalarAsync<string>("select original_path from file_assets;"))
            .Should().Be(original);
        if (replace)
        {
            File.Copy(TestFixtures.RealThreePagePdf, original, true);
            await File.AppendAllTextAsync(original, "\n% a distinct PDF revision");
        }
        else
        {
            File.Delete(original);
        }

        Result<FileSearchRootRescanSummary> result = await watcher.RescanFileSearchRootsAsync();
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.FailedPdfCount.Should().Be(0);
        result.Value.ImportedPdfCount.Should().Be(replace ? 1 : 0);
        (await connection.ExecuteScalarAsync<string>(
                "select original_path from file_assets where file_asset_id = @Id;", new { Id = assetId }))
            .Should().Be(copy);
        (await connection.ExecuteScalarAsync<string>(
                "select status from known_file_locations where file_asset_id = @Id and path = @Path;",
                new { Id = assetId, Path = original })).Should()
            .Be(replace ? FileAssetStatus.Changed : FileAssetStatus.Missing);
        (await connection.ExecuteScalarAsync<int>("select count(*) from items;"))
            .Should().Be(replace ? 2 : 1);
    }

    [Fact]
    public async Task Rescan_repairs_an_already_remembered_rename_from_an_older_version()
    {
        await using HostContext context = await HostContext.CreateAsync();
        string root = context.CreateRootDirectory();
        (await context.Services.FileResolution.AddSearchRootAsync(SelectedRoot(root))).IsSuccess.Should().BeTrue();
        string original = TestFixtures.CopyRealThreePagePdfTo(root, "original.pdf");
        await using FileSearchRootWatcherService watcher = new(context.Services, new NoOpAppLogger());
        (await watcher.RescanFileSearchRootsAsync()).IsSuccess.Should().BeTrue();
        string renamed = Path.Combine(root, "renamed.pdf");
        File.Move(original, renamed);
        await using (SqliteConnection connection = context.Services.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into known_file_locations (location_id, file_asset_id, path, last_seen_at, status)
                select @LocationId, file_asset_id, @Path, updated_at, 'available' from file_assets;
                """, new { LocationId = Guid.NewGuid().ToString(), Path = renamed });
        }

        (await watcher.RescanFileSearchRootsAsync()).Value.ImportedPdfCount.Should().Be(0);
        await using SqliteConnection read = context.Services.ConnectionFactory.CreateReadConnection();
        await read.OpenAsync();
        (await read.ExecuteScalarAsync<string>("select original_path from file_assets;"))
            .Should().Be(renamed);
        (await read.ExecuteScalarAsync<int>(
            "select count(*) from known_file_locations where status = 'available';")).Should().Be(1);
    }

    [Fact]
    public async Task Watcher_rename_switches_location_without_changing_titles_or_identity()
    {
        await using HostContext context = await HostContext.CreateAsync();
        string root = context.CreateRootDirectory();
        Result<FileSearchRoot> added = await context.Services.FileResolution.AddSearchRootAsync(SelectedRoot(root));
        added.IsSuccess.Should().BeTrue();
        string original = TestFixtures.CopyRealThreePagePdfTo(root, "original.pdf");
        await using FileSearchRootWatcherService watcher = new(context.Services, new NoOpAppLogger());
        (await watcher.RescanFileSearchRootsAsync()).IsSuccess.Should().BeTrue();
        TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.RescanCompleted += (_, args) =>
        {
            if (args.Trigger == FileSearchRootWatcherService.FileWatcherTrigger)
            {
                completed.TrySetResult();
            }
        };
        watcher.RefreshWatchers([added.Value]);
        string renamed = Path.Combine(root, "renamed.pdf");
        File.Move(original, renamed);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await using SqliteConnection read = context.Services.ConnectionFactory.CreateReadConnection();
        await read.OpenAsync();
        (await read.ExecuteScalarAsync<string>("select original_path from file_assets;")).Should().Be(renamed);
        (await read.ExecuteScalarAsync<string>("select title from items;")).Should().Be("original");
        (await read.ExecuteScalarAsync<int>("select count(*) from document_instances;")).Should().Be(1);
    }

    [Fact]
    public async Task Watcher_rechecks_changed_content_even_when_size_and_mtime_are_preserved()
    {
        await using HostContext context = await HostContext.CreateAsync();
        string root = context.CreateRootDirectory();
        Result<FileSearchRoot> added = await context.Services.FileResolution.AddSearchRootAsync(SelectedRoot(root));
        added.IsSuccess.Should().BeTrue();
        string path = TestFixtures.CopyRealThreePagePdfTo(root, "same-path.pdf");
        await File.AppendAllTextAsync(path, "\n% A");
        await using FileSearchRootWatcherService watcher = new(context.Services, new NoOpAppLogger());
        (await watcher.RescanFileSearchRootsAsync()).IsSuccess.Should().BeTrue();
        TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.RescanCompleted += (_, args) =>
        {
            if (args.Trigger == FileSearchRootWatcherService.FileWatcherTrigger)
            {
                completed.TrySetResult();
            }
        };
        watcher.RefreshWatchers([added.Value]);
        DateTime modified = File.GetLastWriteTimeUtc(path);
        await using (FileStream stream = File.OpenWrite(path))
        {
            stream.Position = stream.Length - 1;
            stream.WriteByte((byte)'B');
        }

        File.SetLastWriteTimeUtc(path, modified);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await using SqliteConnection read = context.Services.ConnectionFactory.CreateReadConnection();
        await read.OpenAsync();
        (await read.ExecuteScalarAsync<int>("select count(*) from file_assets;")).Should().Be(2);
        (await read.ExecuteScalarAsync<int>(
            "select count(*) from known_file_locations where status = 'available';")).Should().Be(1);
    }

    [Fact]
    public async Task Debounced_rescan_coalesces_rapid_file_events_into_a_single_rescan()
    {
        await using HostContext context = await HostContext.CreateAsync();
        string root = context.CreateRootDirectory();
        Result<FileSearchRoot> added = await context.Services.FileResolution.AddSearchRootAsync(SelectedRoot(root));
        added.IsSuccess.Should().BeTrue(added.ErrorMessage);

        FileSearchRootWatcherService watcher = new(context.Services, new NoOpAppLogger());
        List<string> completedTriggers = new();
        object sync = new();
        try
        {
            watcher.RescanCompleted += (_, eventArgs) =>
            {
                lock (sync)
                {
                    completedTriggers.Add(eventArgs.Trigger);
                }
            };
            watcher.RefreshWatchers([added.Value]);

            // Issue #30 (plan §11): activation runs one full reconciliation scan and
            // publishes its own completion. Drain that completion first so the
            // assertions below measure only what the rapid file events produce.
            await WaitUntilAsync(() =>
            {
                lock (sync)
                {
                    return completedTriggers.Count > 0;
                }
            }, "the activation reconciliation scan should complete well within the timeout");
            lock (sync)
            {
                completedTriggers.Clear();
            }

            for (int index = 0; index < 3; index++)
            {
                await File.WriteAllTextAsync(Path.Combine(root, $"rapid-{index}.pdf"), "fake pdf payload");
            }

            await WaitUntilAsync(() =>
            {
                lock (sync)
                {
                    return completedTriggers.Count > 0;
                }
            });
            // Wait past one full debounce window so a duplicate rescan would have fired.
            await Task.Delay(TimeSpan.FromSeconds(5));

            lock (sync)
            {
                completedTriggers.Should().Equal(FileSearchRootWatcherService.FileWatcherTrigger);
            }
        }
        finally
        {
            await watcher.StopAsync();
        }
    }

    [Fact]
    public async Task StopAsync_disposes_watchers_and_suppresses_pending_rescans()
    {
        await using HostContext context = await HostContext.CreateAsync();
        string root = context.CreateRootDirectory();
        Result<FileSearchRoot> added = await context.Services.FileResolution.AddSearchRootAsync(SelectedRoot(root));
        added.IsSuccess.Should().BeTrue(added.ErrorMessage);

        FileSearchRootWatcherService watcher = new(context.Services, new NoOpAppLogger());
        SignalCounter completed = new();
        watcher.RescanCompleted += (_, _) => completed.Increment();
        watcher.RefreshWatchers([added.Value]);
        await watcher.StopAsync();

        await File.WriteAllTextAsync(Path.Combine(root, "after-stop.pdf"), "fake pdf payload");
        await Task.Delay(TimeSpan.FromSeconds(4));
        completed.Count.Should().Be(0, "stopped watchers must not schedule rescans");
    }

    [Fact]
    public async Task RefreshWatchers_drops_watchers_for_removed_roots()
    {
        await using HostContext context = await HostContext.CreateAsync();
        string root = context.CreateRootDirectory();
        Result<FileSearchRoot> added = await context.Services.FileResolution.AddSearchRootAsync(SelectedRoot(root));
        added.IsSuccess.Should().BeTrue(added.ErrorMessage);

        FileSearchRootWatcherService watcher = new(context.Services, new NoOpAppLogger());
        SignalCounter completed = new();
        try
        {
            watcher.RescanCompleted += (_, _) => completed.Increment();
            watcher.RefreshWatchers([added.Value]);
            watcher.RefreshWatchers([]);

            // Issue #30 (plan §11): the activation reconciliation scan requested by the
            // first RefreshWatchers still runs to completion and publishes exactly once,
            // even though the watcher itself is already gone. Drain that one completion;
            // a removed root must not produce any further ones.
            await WaitUntilAsync(() => completed.Count > 0,
                "the activation reconciliation scan should complete well within the timeout");
            int activationCompletions = completed.Count;
            activationCompletions.Should().Be(1,
                "activation publishes exactly one reconciliation completion");

            await File.WriteAllTextAsync(Path.Combine(root, "removed-root.pdf"), "fake pdf payload");
            await Task.Delay(TimeSpan.FromSeconds(4));
            completed.Count.Should().Be(activationCompletions,
                "a removed root must no longer raise rescan triggers");
        }
        finally
        {
            await watcher.StopAsync();
        }
    }

    private static SelectedFileSearchRoot SelectedRoot(string path)
    {
        return new SelectedFileSearchRoot(
            path,
            "test",
            FileSearchRootAuthorizationKinds.None,
            null,
            null,
            DateTimeOffset.UtcNow);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because =
        "the debounced rescan should complete well within the timeout")
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        condition().Should().BeTrue(because);
    }

    private sealed class NoOpAppLogger : IAppLogger
    {
        public Task LogAsync(string operation, string message)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class SignalCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment()
        {
            Interlocked.Increment(ref _count);
        }
    }

    private sealed class HostContext : IAsyncDisposable
    {
        private HostContext(string tempRoot, HostServices services)
        {
            TempRoot = tempRoot;
            Services = services;
        }

        public string TempRoot { get; }
        public HostServices Services { get; }

        public static async Task<HostContext> CreateAsync()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), $"patchouli-watcher-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempRoot);
            string databasePath = Path.Combine(tempRoot, "runtime.sqlite");
            PatchouliAppSettings defaults = PatchouliAppSettings.Default();
            string settingsPath = Path.Combine(tempRoot, "appsettings.json");
            PatchouliAppSettings settings = defaults with
            {
                Runtime = defaults.Runtime with
                {
                    RuntimeDatabasePath = databasePath,
                    DefaultSyncRoot = Path.Combine(tempRoot, "sync"),
                    DefaultStagingRoot = Path.Combine(tempRoot, "staging"),
                    LogDirectory = Path.Combine(tempRoot, "logs"),
                    UseMockOcrOnly = true
                },
                Sync = defaults.Sync with { DeviceId = "watcher-test-device" }
            };
            settings.Save(settingsPath).IsSuccess.Should().BeTrue();
            HostServices services = await HostServices.CreateAsync(databasePath, settings, settingsPath);
            Result<LibraryMetadata> create = await services.Library.CreateLibraryAsync("Watcher Test");
            create.IsSuccess.Should().BeTrue(create.ErrorMessage);
            return new HostContext(tempRoot, services);
        }

        public string CreateRootDirectory()
        {
            string root = Path.Combine(TempRoot, $"search-root-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return root;
        }

        public ValueTask DisposeAsync()
        {
            SqliteTestCleanup.ReleasePoolsInDirectory(TempRoot);
            try
            {
                Directory.Delete(TempRoot, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }
}
