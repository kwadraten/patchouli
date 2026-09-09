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

            await File.WriteAllTextAsync(Path.Combine(root, "removed-root.pdf"), "fake pdf payload");
            await Task.Delay(TimeSpan.FromSeconds(4));
            completed.Count.Should().Be(0, "a removed root must no longer raise rescan triggers");
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        condition().Should().BeTrue("the debounced rescan should complete well within the timeout");
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
