using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Files;
using Patchouli.Core.Import;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Host.Watching;
using Patchouli.UI;

namespace Patchouli.Tests;

/// <summary>
/// Issue #30, plan test items 7 (watcher side), 8 (scan side) and 10, against a
/// real <see cref="HostServices"/> with a temporary SQLite library:
/// a path claimed by an explicit import is deferred by rescans and imported
/// exactly once after the claim is released; ".importing" temp files are never
/// imported, while the file published under its final name is imported exactly
/// once; and file events arriving while a rescan is in flight neither cancel
/// it nor get lost — they coalesce into exactly one follow-up watcher pass.
/// The in-flight window of item 10 is made exact by gating the rescan's own
/// progress callback on a TaskCompletionSource barrier.
/// </summary>
public sealed class FileWatcherIssue30Tests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Rescan_defers_a_path_claimed_by_an_explicit_import_and_imports_it_once_after_release()
    {
        await using HostContext context = await HostContext.CreateAsync();
        string root = context.CreateRootDirectory();
        Result<FileSearchRoot> added =
            await context.Services.FileResolution.AddSearchRootAsync(SelectedRoot(root));
        added.IsSuccess.Should().BeTrue(added.ErrorMessage);
        string pdf = TestFixtures.CopyRealThreePagePdfTo(root, "held.pdf");
        await File.AppendAllTextAsync(pdf, "\n% held copy");

        await using FileSearchRootWatcherService watcher = new(context.Services, new NoOpAppLogger());
        await using IFileOperationLease claim =
            await context.Services.FileOperations.ClaimAsync([pdf]);

        Result<FileSearchRootRescanSummary> during = await watcher.RescanFileSearchRootsAsync();
        during.IsSuccess.Should().BeTrue(during.ErrorMessage);
        during.Value.ImportedPdfCount.Should().Be(0,
            "the path is owned by an explicit import, so the rescan must defer it");
        (await CountAssetsAsync(context, pdf)).Should().Be(0);

        await claim.DisposeAsync();
        Result<FileSearchRootRescanSummary> after = await watcher.RescanFileSearchRootsAsync();
        after.IsSuccess.Should().BeTrue(after.ErrorMessage);
        after.Value.ImportedPdfCount.Should().Be(1);
        (await CountAssetsAsync(context, pdf)).Should().Be(1,
            "after the claim is released the file is imported exactly once");
    }

    [Fact]
    public async Task Importing_temp_files_are_never_imported_and_the_published_file_is_imported_exactly_once()
    {
        await using HostContext context = await HostContext.CreateAsync();
        string root = context.CreateRootDirectory();
        (await context.Services.FileResolution.AddSearchRootAsync(SelectedRoot(root)))
            .IsSuccess.Should().BeTrue();

        // Exactly the name a cross-volume add publishes from (plan §9):
        // ".{final name}.{32 hex guid}.importing" inside the target directory.
        string tempName = $".pub.pdf.{Guid.NewGuid():N}.importing";
        string tempPath = Path.Combine(root, tempName);
        File.Copy(TestFixtures.RealThreePagePdf, tempPath);
        await File.AppendAllTextAsync(tempPath, "\n% published copy");
        string looseTemp = Path.Combine(root, "loose.importing");
        File.Copy(TestFixtures.RealThreePagePdf, looseTemp);

        await using FileSearchRootWatcherService watcher = new(context.Services, new NoOpAppLogger());
        Result<FileSearchRootRescanSummary> first = await watcher.RescanFileSearchRootsAsync();
        first.IsSuccess.Should().BeTrue(first.ErrorMessage);
        first.Value.ImportedPdfCount.Should().Be(0,
            "scans only recognize *.pdf; an in-flight copy is invisible");
        (await CountAllAssetsAsync(context)).Should().Be(0);

        string published = Path.Combine(root, "pub.pdf");
        File.Move(tempPath, published);
        Result<FileSearchRootRescanSummary> second = await watcher.RescanFileSearchRootsAsync();
        second.IsSuccess.Should().BeTrue(second.ErrorMessage);
        second.Value.ImportedPdfCount.Should().Be(1);
        (await CountAssetsAsync(context, published)).Should().Be(1);

        Result<FileSearchRootRescanSummary> third = await watcher.RescanFileSearchRootsAsync();
        third.IsSuccess.Should().BeTrue(third.ErrorMessage);
        third.Value.ImportedPdfCount.Should().Be(0);
        (await CountAssetsAsync(context, published)).Should().Be(1,
            "the published file is imported exactly once across repeated scans");
    }

    [Fact]
    public async Task Events_arriving_during_an_in_flight_rescan_neither_cancel_it_nor_get_lost()
    {
        await using HostContext context = await HostContext.CreateAsync();
        string root = context.CreateRootDirectory();
        Result<FileSearchRoot> added =
            await context.Services.FileResolution.AddSearchRootAsync(SelectedRoot(root));
        added.IsSuccess.Should().BeTrue(added.ErrorMessage);
        string seed = TestFixtures.CopyRealThreePagePdfTo(root, "seed.pdf");
        await File.AppendAllTextAsync(seed, "\n% seed copy");

        FileSearchRootWatcherService watcher = new(context.Services, new NoOpAppLogger());
        CompletionLog completions = new();
        watcher.RescanCompleted += (_, args) => completions.Add(args);
        try
        {
            watcher.RefreshWatchers([added.Value]);
            // Activation runs one full reconciliation rescan (plan §11); wait for
            // it so later completions are attributable to this test's own actions.
            await completions.WaitForCountAsync(1, Timeout);
            completions.Snapshot().Should().ContainSingle()
                .Which.Trigger.Should().Be(FileSearchRootWatcherService.FileWatcherTrigger);
            (await CountAssetsAsync(context, seed)).Should().Be(1);
            completions.Clear();

            // Stage the two new files outside the root so moving them in is the
            // only file activity while the rescan is gated.
            string staging = Path.Combine(context.TempRoot, "staging");
            string stagedA = TestFixtures.CopyRealThreePagePdfTo(staging, "a.pdf");
            await File.AppendAllTextAsync(stagedA, "\n% a copy");
            string stagedB = TestFixtures.CopyRealThreePagePdfTo(staging, "b.pdf");
            await File.AppendAllTextAsync(stagedB, "\n% b copy");

            TaskCompletionSource gateEntered = NewSignal();
            TaskCompletionSource gateRelease = NewSignal();
            int gateArmed = 1;
            Action<int?, int?, string, string?> progress = (current, total, _, _) =>
            {
                // The per-root "processed" progress fires after this root's scan
                // enumeration, so files created from here on cannot be picked up
                // by the in-flight rescan itself — only by the watcher loop.
                if (current is not null && total is not null && current.Value == total.Value &&
                    Interlocked.Exchange(ref gateArmed, 0) == 1)
                {
                    gateEntered.TrySetResult();
                    gateRelease.Task.GetAwaiter().GetResult();
                }
            };

            using CancellationTokenSource rescanCancellation = new();
            Task<Result<FileSearchRootRescanSummary>> rescan = watcher.RescanFileSearchRootsAsync(
                cancellationToken: rescanCancellation.Token, progress: progress);
            await gateEntered.Task.WaitAsync(Timeout);
            rescan.IsCompleted.Should().BeFalse("the rescan is deterministically in flight");

            string finalA = Path.Combine(root, "a.pdf");
            string finalB = Path.Combine(root, "b.pdf");
            File.Move(stagedA, finalA);
            File.Move(stagedB, finalB);

            gateRelease.TrySetResult();
            Result<FileSearchRootRescanSummary> manual = await rescan.WaitAsync(Timeout);
            manual.IsSuccess.Should().BeTrue(manual.ErrorMessage);
            rescanCancellation.IsCancellationRequested.Should().BeFalse(
                "file events have no channel to cancel work already running");

            // The events coalesce into exactly one follow-up watcher pass that
            // imports both new files — the final state is covered, nothing lost.
            await completions.WaitForCountAsync(2, Timeout);
            IReadOnlyList<FileSearchRootRescanCompleted> snapshot = completions.Snapshot();
            FileSearchRootRescanCompleted[] watcherPasses = snapshot
                .Where(entry => entry.Trigger == FileSearchRootWatcherService.FileWatcherTrigger)
                .ToArray();
            watcherPasses.Should().ContainSingle("the burst coalesces into one follow-up pass")
                .Which.Summary.ImportedPdfCount.Should().Be(2);
            snapshot.Count(entry => entry.Trigger == FileSearchRootWatcherService.ManualTrigger)
                .Should().Be(1, "only the manual rescan itself reports as manual");

            (await CountAssetsAsync(context, finalA)).Should().Be(1);
            (await CountAssetsAsync(context, finalB)).Should().Be(1);
            (await CountAllAssetsAsync(context)).Should().Be(3);
        }
        finally
        {
            await watcher.StopAsync();
            await watcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task Full_rescan_with_case_variant_dirty_paths_pending_imports_each_file_once()
    {
        // Needs a case-sensitive file system (Linux): "A.pdf" and "a.pdf" are two
        // files in one root. The dirty set keys paths with the platform comparer
        // (Ordinal here), so both entries coexist in it; the full rescan must
        // snapshot the dirty set with that same comparer. Copying it with a
        // hardcoded OrdinalIgnoreCase comparer throws ArgumentException (the two
        // keys collide) and fails the whole rescan. The known-paths set must use
        // the platform comparer too, or the second file is skipped as "known"
        // once the first one has been imported.
        await using HostContext context = await HostContext.CreateAsync();
        string root = context.CreateRootDirectory();
        Result<FileSearchRoot> added =
            await context.Services.FileResolution.AddSearchRootAsync(SelectedRoot(root));
        added.IsSuccess.Should().BeTrue(added.ErrorMessage);

        // Stage the two files outside the root so moving them in is the only
        // file activity once the watchers are live.
        string staging = Path.Combine(context.TempRoot, "staging");
        string stagedUpper = TestFixtures.CopyRealThreePagePdfTo(staging, "A.pdf");
        await File.AppendAllTextAsync(stagedUpper, "\n% upper copy");
        string stagedLower = TestFixtures.CopyRealThreePagePdfTo(staging, "a.pdf");
        await File.AppendAllTextAsync(stagedLower, "\n% lower copy");

        RecordingAppLogger logger = new();
        FileSearchRootWatcherService watcher = new(context.Services, logger);
        CompletionLog completions = new();
        watcher.RescanCompleted += (_, args) => completions.Add(args);
        try
        {
            watcher.RefreshWatchers([added.Value]);
            // Activation runs one full reconciliation rescan (plan §11); wait for
            // it so the watch loop is idle before the case variants arrive.
            await completions.WaitForCountAsync(1, Timeout);
            completions.Clear();

            string upper = Path.Combine(root, "A.pdf");
            string lower = Path.Combine(root, "a.pdf");
            File.Move(stagedUpper, upper);
            File.Move(stagedLower, lower);

            // NoteFileEvent records a path in the dirty set before logging its
            // "Change detected" line, so observing both lines proves both case
            // variants are pending in the dirty set. The watch loop cannot
            // consume them inside the quiet window after the last event, so the
            // manual rescan below deterministically snapshots a two-entry set.
            await logger.WaitForChangeDetectedAsync(upper, Timeout);
            await logger.WaitForChangeDetectedAsync(lower, Timeout);

            Result<FileSearchRootRescanSummary> rescan = await watcher.RescanFileSearchRootsAsync();
            rescan.IsSuccess.Should().BeTrue(rescan.ErrorMessage);
            rescan.Value.ImportedPdfCount.Should().Be(2,
                "both case variants are distinct pending paths and each is imported");
            (await CountAssetsAsync(context, upper)).Should().Be(1);
            (await CountAssetsAsync(context, lower)).Should().Be(1);
            (await CountAllAssetsAsync(context)).Should().Be(2);
        }
        finally
        {
            await watcher.StopAsync();
            await watcher.DisposeAsync();
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<int> CountAssetsAsync(HostContext context, string path)
    {
        await using SqliteConnection connection = context.Services.ConnectionFactory.CreateReadConnection();
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<int>(
            "select count(*) from file_assets where original_path = @Path;", new { Path = path });
    }

    private static async Task<int> CountAllAssetsAsync(HostContext context)
    {
        await using SqliteConnection connection = context.Services.ConnectionFactory.CreateReadConnection();
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<int>("select count(*) from file_assets;");
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

    private sealed class CompletionLog
    {
        private readonly object _sync = new();
        private readonly List<FileSearchRootRescanCompleted> _entries = [];
        private TaskCompletionSource _changed = NewSignal();

        public void Add(FileSearchRootRescanCompleted entry)
        {
            TaskCompletionSource changed;
            lock (_sync)
            {
                _entries.Add(entry);
                changed = _changed;
                _changed = NewSignal();
            }

            changed.TrySetResult();
        }

        public IReadOnlyList<FileSearchRootRescanCompleted> Snapshot()
        {
            lock (_sync)
            {
                return _entries.ToArray();
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _entries.Clear();
            }
        }

        public async Task WaitForCountAsync(int count, TimeSpan timeout)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
            while (true)
            {
                Task changed;
                lock (_sync)
                {
                    if (_entries.Count >= count)
                    {
                        return;
                    }

                    changed = _changed.Task;
                }

                TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
                remaining.Should().BePositive("the expected rescan completions did not arrive in time");
                await changed.WaitAsync(remaining);
            }
        }
    }

    private sealed class RecordingAppLogger : IAppLogger
    {
        private readonly object _sync = new();
        private readonly List<string> _messages = [];
        private TaskCompletionSource _changed = NewSignal();

        public Task LogAsync(string operation, string message)
        {
            TaskCompletionSource changed;
            lock (_sync)
            {
                _messages.Add(message);
                changed = _changed;
                _changed = NewSignal();
            }

            changed.TrySetResult();
            return Task.CompletedTask;
        }

        public async Task WaitForChangeDetectedAsync(string path, TimeSpan timeout)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
            while (true)
            {
                Task changed;
                lock (_sync)
                {
                    if (_messages.Exists(message =>
                            message.Contains("Change detected", StringComparison.Ordinal) &&
                            message.Contains(path, StringComparison.Ordinal)))
                    {
                        return;
                    }

                    changed = _changed.Task;
                }

                TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
                remaining.Should().BePositive($"no change event was recorded for '{path}' in time");
                await changed.WaitAsync(remaining);
            }
        }
    }

    private sealed class NoOpAppLogger : IAppLogger
    {
        public Task LogAsync(string operation, string message)
        {
            return Task.CompletedTask;
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
            string tempRoot = Path.Combine(Path.GetTempPath(), $"patchouli-issue30-{Guid.NewGuid():N}");
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
                Sync = defaults.Sync with { DeviceId = "issue30-test-device" }
            };
            settings.Save(settingsPath).IsSuccess.Should().BeTrue();
            HostServices services = await HostServices.CreateAsync(databasePath, settings, settingsPath);
            Result<LibraryMetadata> create = await services.Library.CreateLibraryAsync("Issue 30 Test");
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
