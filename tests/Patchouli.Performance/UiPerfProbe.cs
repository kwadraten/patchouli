using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Credentials;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Credentials;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Workflows;
using Patchouli.Ocr;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.Host.Composition;

namespace Patchouli.Performance;

/// <summary>
/// The real-UI performance probe results. Every value is measured on an actual headless Avalonia
/// dispatcher against a real <see cref="MainWindow"/>, real view models, and the real host
/// services. Nothing here is synthesized in the console. <c>FirstLibraryRows*Ms</c> keeps its
/// original headline definition (construction through startup/first-run completion);
/// <c>LibraryFirstBatchReady*Ms</c> and <c>LibraryAllPagesDrained*Ms</c> are the newer,
/// explicitly versioned paging measurements recorded under
/// <see cref="ReportUiPagingMeasurements"/>.
/// </summary>
public sealed record UiPerfResult(
    bool Measured,
    double InteractiveFrameworkColdMs,
    double InteractiveFrameworkHotMs,
    double FirstLibraryRowsColdMs,
    double FirstLibraryRowsHotMs,
    double HeartbeatMaxGapMs,
    int HeartbeatSamples,
    long UiThreadDatabaseCommands,
    int FirstLibraryRowCount,
    double LibraryFirstBatchReadyColdMs,
    double LibraryFirstBatchReadyHotMs,
    double LibraryAllPagesDrainedColdMs,
    double LibraryAllPagesDrainedHotMs)
{
    public ReportUi ToReportUi()
    {
        return new ReportUi(
            Measured,
            InteractiveFrameworkColdMs,
            InteractiveFrameworkHotMs,
            FirstLibraryRowsColdMs,
            FirstLibraryRowsHotMs,
            HeartbeatMaxGapMs,
            UiPerfProbe.HeartbeatTargetMs,
            HeartbeatSamples,
            UiThreadDatabaseCommands,
            FirstLibraryRowCount,
            Measured
                ? new ReportUiPagingMeasurements(
                    UiPerfProbe.PagingMeasurementVersion,
                    LibraryFirstBatchReadyColdMs,
                    LibraryFirstBatchReadyHotMs,
                    LibraryAllPagesDrainedColdMs,
                    LibraryAllPagesDrainedHotMs)
                : null);
    }
}

/// <summary>
/// Drives the AC1/AC2/AC3 UI probes on the real UI boundary (headless Avalonia). The headless
/// session owns a genuine UI dispatcher and windowing, so "interactive framework", "first library
/// rows", and the 100 ms dispatcher heartbeat are measured against the framework itself, not
/// against a Stopwatch placed around a console-side SQL query.
/// </summary>
public static class UiPerfProbe
{
    /// <summary>AC3's stated UI heartbeat cadence.</summary>
    public const double HeartbeatTargetMs = 100;

    /// <summary>
    /// The measurement-definition version stamped into <see cref="ReportUiPagingMeasurements"/>.
    /// It is incremented whenever the meaning of the split paging measurements changes; the
    /// regression evaluator never computes a numeric ratio between values recorded under
    /// different versions — such pairs are marked non-comparable and require a new baseline.
    /// </summary>
    public const int PagingMeasurementVersion = 1;

    public static async Task<UiPerfResult?> RunAsync(
        PerfOptions options,
        string migrationsDirectory,
        CancellationToken cancellationToken)
    {
        if (!options.UiProbes)
        {
            return null;
        }

        string root = Directory.CreateDirectory(Path.Combine(
                Path.GetTempPath(), $"patchouli-uiperf-{Guid.NewGuid():N}"))
            .FullName;
        string databasePath = Path.Combine(root, "fixture.sqlite");
        string settingsPath = Path.Combine(root, "settings.json");
        try
        {
            CountingConnectionFactory seedDatabase = new(databasePath);
            // Seed the requested item count so the UI probe measures the real list scale instead of
            // silently capping the fixture at 100 rows. The heartbeat below independently stages/adopts
            // the requested total Box count in one document transaction.
            await PerformanceFixture.SeedAsync(
                seedDatabase, migrationsDirectory, options.Seed, options.Items, 1, 1,
                cancellationToken);
            seedDatabase.ClearPools();
            WriteSettings(settingsPath, databasePath, root);
            await CompleteUiFixtureSetupAsync(seedDatabase, settingsPath, root, cancellationToken);
            seedDatabase.ClearPools();

            UiPerfResult result;
            await using (HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp)))
            {
                Trace(!options.Quiet, $"[ui-perf] session ready");
                result = await session.Dispatch(
                    async () => await MeasureOnUiThreadAsync(options, databasePath, settingsPath, cancellationToken),
                    cancellationToken);
                Trace(!options.Quiet, $"[ui-perf] dispatch complete");
            }

            Trace(!options.Quiet, $"[ui-perf] session disposed");

            seedDatabase.ClearPools();
            return result;
        }
        finally
        {
            new SqliteConnectionFactory(databasePath).ClearPools();
            TryDeleteDirectory(root);
        }
    }

    /// <summary>
    /// Monotonic bound for each first-rows measurement (startup, initial-page readiness, and
    /// page drain combined). A bound this loose only trips on genuinely stuck paging state;
    /// legitimate slow runs are still measured and reported.
    /// </summary>
    private static readonly TimeSpan UiWaitBudget = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Dispatcher-friendly poll interval used while waiting out a paging state that cannot
    /// progress yet (refresh or page load in flight, or a refresh not yet started).
    /// </summary>
    private static readonly TimeSpan NoProgressPollInterval = TimeSpan.FromMilliseconds(50);

    private static async Task<UiPerfResult> MeasureOnUiThreadAsync(
        PerfOptions options,
        string databasePath,
        string settingsPath,
        CancellationToken cancellationToken)
    {
        double frameworkCold = await MeasureFrameworkAsync(settingsPath, databasePath, cancellationToken);
        Trace(!options.Quiet, $"[ui-perf] framework cold {frameworkCold:0.##} ms");
        FirstRowsMeasurement firstRowsCold = await MeasureFirstRowsAsync(
            settingsPath, databasePath, cancellationToken);
        Trace(!options.Quiet, $"[ui-perf] first rows cold {firstRowsCold.FirstRowsMs:0.##} ms " +
                              $"({firstRowsCold.RowCount} rows; first batch ready " +
                              $"{firstRowsCold.FirstBatchReadyMs:0.##} ms, all pages drained " +
                              $"{firstRowsCold.AllPagesDrainedMs:0.##} ms)");

        double frameworkHot = await MeasureFrameworkAsync(settingsPath, databasePath, cancellationToken);
        Trace(!options.Quiet, $"[ui-perf] framework hot {frameworkHot:0.##} ms");
        FirstRowsMeasurement firstRowsHot = await MeasureFirstRowsAsync(
            settingsPath, databasePath, cancellationToken);
        Trace(!options.Quiet, $"[ui-perf] first rows hot {firstRowsHot.FirstRowsMs:0.##} ms " +
                              $"(first batch ready {firstRowsHot.FirstBatchReadyMs:0.##} ms, " +
                              $"all pages drained {firstRowsHot.AllPagesDrainedMs:0.##} ms)");

        (double heartbeatMaxGap, int heartbeatSamples, long uiThreadCommands) = await MeasureHeartbeatAsync(
            databasePath, options, cancellationToken);
        Trace(!options.Quiet, $"[ui-perf] heartbeat max gap {heartbeatMaxGap:0.##} ms over {heartbeatSamples} " +
                              $"samples (ui-thread db commands {uiThreadCommands})");

        return new UiPerfResult(true, frameworkCold, frameworkHot, firstRowsCold.FirstRowsMs,
            firstRowsHot.FirstRowsMs, heartbeatMaxGap, heartbeatSamples, uiThreadCommands,
            firstRowsCold.RowCount, firstRowsCold.FirstBatchReadyMs, firstRowsHot.FirstBatchReadyMs,
            firstRowsCold.AllPagesDrainedMs, firstRowsHot.AllPagesDrainedMs);
    }

    /// <summary>
    /// Measures the time from view-model/window construction to a shown, measured, and arranged
    /// window — the interactive framework boundary on the real UI dispatcher. Runs on the UI thread.
    /// </summary>
    private static async Task<double> MeasureFrameworkAsync(
        string settingsPath, string databasePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long start = Stopwatch.GetTimestamp();
        MainWindowViewModel? viewModel = null;
        MainWindow? window = null;
        Exception? pending = null;
        try
        {
            viewModel = CreateViewModel(settingsPath, databasePath);
            window = new MainWindow(viewModel);
            window.Width = 1280;
            window.Height = 820;
            window.Show();
            window.Measure(new Size(1280, 820));
            window.Arrange(new Rect(0, 0, 1280, 820));
            return ElapsedMs(start, Stopwatch.GetTimestamp());
        }
        catch (Exception exception)
        {
            pending = exception;
            throw;
        }
        finally
        {
            // The window and its view model are released on every path — success, failure, and
            // cancellation alike — so a stuck measure cannot leak host services or connections.
            window?.Close();
            await StopServicesAsync(viewModel, pending);
        }
    }

    /// <summary>
    /// One first-rows pass against the real cold-open path: <see cref="HostServices.CreateAsync"/>
    /// (migrations, OCR reconciliation, queue start) plus the library pages projected into the
    /// shell. Three monotonic boundaries are captured from the same start timestamp:
    /// <see cref="FirstRowsMeasurement.FirstRowsMs"/> is the headline metric with its original
    /// definition (construction through <see cref="MainWindow.ShowFirstRunIfNeededAsync"/>);
    /// <see cref="FirstRowsMeasurement.FirstBatchReadyMs"/> ends when the shell's current paging
    /// generation has published its first page; <see cref="FirstRowsMeasurement.AllPagesDrainedMs"/>
    /// ends when every page of the requested fixture scale has been published.
    /// </summary>
    private static async Task<FirstRowsMeasurement> MeasureFirstRowsAsync(
        string settingsPath, string databasePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long start = Stopwatch.GetTimestamp();
        MainWindowViewModel? viewModel = null;
        MainWindow? window = null;
        Exception? pending = null;
        try
        {
            viewModel = CreateViewModel(settingsPath, databasePath);
            window = new MainWindow(viewModel);
            window.Show();
            // The startup wait shares the monotonic deadline so a hung startup fails bounded
            // instead of blocking the dispatcher forever.
            await WaitWithDeadlineAsync(
                window.ShowFirstRunIfNeededAsync(false),
                start, UiWaitBudget, cancellationToken,
                () => $"startup/first-run {DescribePaging(viewModel.Shell.GetPagingStatus())}");
            long stop = Stopwatch.GetTimestamp();
            if (viewModel.IsFirstRunVisible)
            {
                throw new InvalidOperationException(
                    "UI perf fixture setup incomplete: FirstRun is visible, " +
                    "the seeded library is not shown. Check search root, OCR preset, and credential seeding.");
            }

            // Startup can return while a superseding refresh is still in flight; the first page
            // is only usable once the current generation has actually published it.
            await WaitForInitialLibraryPageAsync(
                viewModel.Shell, UiWaitBudget - Stopwatch.GetElapsedTime(start), cancellationToken);
            long firstBatchReady = Stopwatch.GetTimestamp();

            // The shell pages its list, so the requested fixture scale only becomes visible after
            // draining the remaining keyset pages. The first-screen timing above stays untouched.
            await DrainLibraryPagesAsync(
                viewModel.Shell, UiWaitBudget - Stopwatch.GetElapsedTime(start), cancellationToken);
            long allPagesDrained = Stopwatch.GetTimestamp();

            return new FirstRowsMeasurement(
                ElapsedMs(start, stop),
                ElapsedMs(start, firstBatchReady),
                ElapsedMs(start, allPagesDrained),
                viewModel.Shell.Items.Count);
        }
        catch (Exception exception)
        {
            pending = exception;
            throw;
        }
        finally
        {
            window?.Close();
            await StopServicesAsync(viewModel, pending);
        }
    }

    /// <summary>
    /// Waits until the shell's current paging generation has published its first page — no
    /// refresh in flight and a page query installed. The wait is bounded by
    /// <paramref name="budget"/>; a stuck state throws <see cref="TimeoutException"/> carrying a
    /// privacy-safe paging-state diagnostic, and caller cancellation propagates.
    /// </summary>
    public static async Task WaitForInitialLibraryPageAsync(
        LibraryShellViewModel shell, TimeSpan budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shell);
        long startTimestamp = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LibraryPagingStatus status = shell.GetPagingStatus();
            if (status is { IsRefreshingItems: false, HasPageQuery: true })
            {
                return;
            }

            await WaitForPagingSignalAsync(
                shell, startTimestamp, budget, cancellationToken, "initial library page readiness");
        }
    }

    /// <summary>
    /// Drains the shell's remaining keyset pages. Progress is judged from the paging state, not
    /// only the row count: a filtered page can publish zero rows while still advancing the
    /// cursor. States that cannot progress right now — a refresh or page load in flight, or
    /// <c>HasMoreItems</c> advertised without a page query/cursor — wait dispatcher-friendly and
    /// fail bounded with a privacy-safe diagnostic instead of spinning the dispatcher or
    /// silently returning an incomplete row set.
    /// </summary>
    public static async Task DrainLibraryPagesAsync(
        LibraryShellViewModel shell, TimeSpan budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shell);
        long startTimestamp = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LibraryPagingStatus status = shell.GetPagingStatus();
            if (!status.HasMoreItems)
            {
                return;
            }

            if (status is
                {
                    IsRefreshingItems: false, IsLoadingMoreItems: false,
                    HasPageQuery: true, HasNextPageCursor: true
                })
            {
                await shell.LoadNextPageAsync(cancellationToken);
                continue;
            }

            await WaitForPagingSignalAsync(
                shell, startTimestamp, budget, cancellationToken, "library page drain");
        }
    }

    /// <summary>
    /// Waits one bounded step while paging cannot progress: prefers the current refresh's
    /// completion signal (subscribed before the status is re-checked by the caller's loop), and
    /// falls back to a short <see cref="Task.Delay"/> when no refresh is in flight. Both keep
    /// the dispatcher free to run the queued refresh.
    /// </summary>
    private static async Task WaitForPagingSignalAsync(
        LibraryShellViewModel shell,
        long startTimestamp,
        TimeSpan budget,
        CancellationToken cancellationToken,
        string stage)
    {
        Task refreshCompletion = shell.CurrentRefreshCompletion;
        Task signal = refreshCompletion.IsCompleted
            ? Task.Delay(NoProgressPollInterval, cancellationToken)
            : refreshCompletion;
        await WaitWithDeadlineAsync(
            signal, startTimestamp, budget, cancellationToken,
            () => $"{stage} {DescribePaging(shell.GetPagingStatus())}");
    }

    private static async Task WaitWithDeadlineAsync(
        Task task,
        long startTimestamp,
        TimeSpan budget,
        CancellationToken cancellationToken,
        Func<string> describeState)
    {
        if (budget - Stopwatch.GetElapsedTime(startTimestamp) <= TimeSpan.Zero)
        {
            throw new TimeoutException(
                $"UI perf probe bounded wait exceeded: {describeState()}");
        }

        try
        {
            await task.WaitAsync(budget - Stopwatch.GetElapsedTime(startTimestamp), cancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"UI perf probe bounded wait exceeded: {describeState()}", exception);
        }
    }

    /// <summary>
    /// Formats the privacy-safe paging snapshot for bounded-wait diagnostics: generation,
    /// loading flags, row count, and cursor presence/version only — never item content or
    /// file system paths.
    /// </summary>
    private static string DescribePaging(LibraryPagingStatus status)
    {
        return $"generation={status.PagingGeneration} refreshing={status.IsRefreshingItems} " +
               $"loadingMore={status.IsLoadingMoreItems} rows={status.RowCount} " +
               $"hasMore={status.HasMoreItems} hasPageQuery={status.HasPageQuery} " +
               $"hasCursor={status.HasNextPageCursor} cursorVersion={status.CursorVersion}";
    }

    /// <summary>
    /// Runs a real begin-working + commit of the full fixture box count through
    /// <see cref="DocumentTreeService"/> on a background worker while a 100 ms heartbeat is posted to
    /// the UI dispatcher. The max observed gap is the AC3 UI-responsiveness signal; the counting
    /// connection proves none of the commit's database commands executed on the UI dispatcher thread.
    /// </summary>
    private static async Task<(double MaxGapMs, int Samples, long UiThreadCommands)> MeasureHeartbeatAsync(
        string databasePath,
        PerfOptions options,
        CancellationToken cancellationToken)
    {
        Dispatcher dispatcher = Dispatcher.UIThread;
        List<long> ticks = new();
        DispatcherTimer timer = new(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(HeartbeatTargetMs)
        };
        timer.Tick += (_, _) => ticks.Add(Stopwatch.GetTimestamp());
        timer.Start();

        CountingConnectionFactory heartbeatDatabase =
            new(databasePath, () => SafeIsUiThread(dispatcher));
        DocumentTreeService treeService =
            new(heartbeatDatabase, new ProbeClock(), new MarkdigMarkdownEngine());
        try
        {
            long requestedBoxes = (long)options.Items * options.PagesPerItem * options.BoxesPerPage;
            await Task.Run(() => CommitHeartbeatAsync(treeService, heartbeatDatabase, requestedBoxes,
                cancellationToken), cancellationToken);
        }
        finally
        {
            timer.Stop();
            heartbeatDatabase.ClearPools();
        }

        long[] stamps = ticks.ToArray();
        double maxGap = 0;
        for (int index = 1; index < stamps.Length; index++)
        {
            maxGap = Math.Max(maxGap, (stamps[index] - stamps[index - 1]) * 1000.0 / Stopwatch.Frequency);
        }

        return (maxGap, stamps.Length, heartbeatDatabase.Counters.UiThreadCommands);
    }

    /// <summary>
    /// Creates and commits one synthetic revision with the configured total Box count (for example
    /// 500,000) so the heartbeat measures the actual high-volume atomic commit boundary.
    /// </summary>
    private static async Task CommitHeartbeatAsync(
        DocumentTreeService treeService,
        CountingConnectionFactory database,
        long requestedBoxes,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        PageTarget page = await connection.QuerySingleAsync<PageTarget>(
            "select document_instance_id as DocumentInstanceId, page_id as PageId from pages " +
            "order by page_index, page_id limit 1;");
        if (requestedBoxes > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedBoxes));
        }

        int boxCount = (int)requestedBoxes;
        DocumentBoxSeed[] seeds = new DocumentBoxSeed[boxCount];
        for (int boxIndex = 0; boxIndex < boxCount; boxIndex++)
        {
            double top = 0.04 + 0.9 * boxIndex / Math.Max(1, boxCount);
            seeds[boxIndex] = new DocumentBoxSeed(
                null, null, boxIndex, DocumentBoxType.Text, null,
                new NormalizedBBox(0.04, top, 0.92, Math.Max(0.004, 0.88 / boxCount)),
                new TextBoxPayload($"ui heartbeat commit box {boxIndex:000000}"), null);
        }

        Result<DocumentTreeRevision> working = await treeService.BeginWorkingRevisionAsync(
            DocumentInstanceId.Parse(page.DocumentInstanceId), PageId.Parse(page.PageId), seeds,
            DocumentTreeRevisionSource.Import, cancellationToken: cancellationToken);
        if (working.IsFailure)
        {
            throw new InvalidOperationException(
                $"heartbeat begin working failed: {working.ErrorCode} {working.ErrorMessage}");
        }

        Result<DocumentTreeRevision> committed = await treeService.CommitWorkingRevisionAsync(
            working.Value.TreeRevisionId, null, cancellationToken);
        if (committed.IsFailure)
        {
            throw new InvalidOperationException(
                $"heartbeat commit failed: {committed.ErrorCode} {committed.ErrorMessage}");
        }
    }

    private static MainWindowViewModel CreateViewModel(string settingsPath, string databasePath)
    {
        // The fixture opts into deterministic OCR through explicit host dependencies; the
        // production settings file has no mock switch.
        HostOcrCompositionOptions ocrComposition = new(
            new MockOcrEngine(), [new MockOcrAdapter()]);
        MainWindowViewModel viewModel = new(new ProbeClipboard(), new NoOpLogger(), autoStartMcpServer: false,
            settingsPath: settingsPath, ocrComposition: ocrComposition);
        viewModel.RuntimeDatabasePath = databasePath;
        return viewModel;
    }

    private static void WriteSettings(string settingsPath, string databasePath, string root)
    {
        AppRuntimeOptions runtime = PatchouliAppSettings.Default().Runtime with
        {
            RuntimeDatabasePath = databasePath,
            DefaultSyncRoot = Path.Combine(root, "sync"),
            DefaultStagingRoot = Path.Combine(root, "staging"),
            LogDirectory = Path.Combine(root, "logs"),
            FileSearchRoot = Path.Combine(root, "search"),
            RememberLastDatabase = true
        };
        SyncAppSettings sync = PatchouliAppSettings.Default().Sync with
        {
            DeviceId = Guid.NewGuid().ToString("D"),
            DeviceName = "ui-perf",
            SyncRoot = runtime.DefaultSyncRoot
        };
        MinerUAppSettings minerU = PatchouliAppSettings.Default().MinerU with
        {
            // Inert loopback: never contacted because the fixture injects mock OCR explicitly,
            // but must not point at a real service.
            BaseUrl = "http://127.0.0.1:1"
        };
        OcrEnginesAppSettings ocrEngines = new(
            OcrEngineIds.Mock, OcrEngineIds.Mock, OcrEngineIds.Mock);
        PatchouliAppSettings settings = PatchouliAppSettings.Default() with
        {
            Runtime = runtime,
            Sync = sync,
            MinerU = minerU,
            OcrEngines = ocrEngines
        };
        SettingsSaveResult saved = settings.Save(settingsPath);
        if (!saved.IsSuccess)
        {
            throw new InvalidOperationException(
                $"UI perf fixture failed to write settings: {saved.ErrorMessage}");
        }
    }

    /// <summary>
    /// Completes the onboarding setup contract for the UI perf fixture so
    /// <see cref="MainWindowViewModel"/> shows the seeded library instead of
    /// redirecting to FirstRun. Uses only isolated fixture state: a fresh
    /// database, inert loopback URLs, an explicitly injected mock OCR engine/adapter, and a marker token
    /// with no real credential meaning. Never reads real user credentials.
    /// </summary>
    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private static async Task CompleteUiFixtureSetupAsync(
        CountingConnectionFactory database,
        string settingsPath,
        string root,
        CancellationToken cancellationToken)
    {
        FixedClock clock = new(new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero));
        LibraryIdentityService library = new(database, clock);

        string searchRoot = Path.Combine(root, "search");
        Directory.CreateDirectory(searchRoot);

        FileResolutionService files = new(
            database,
            library,
            clock,
            rootBindings: new AppSettingsDeviceRootBindingStore(settingsPath));
        Result<FileSearchRoot> rootResult = await files.AddSearchRootAsync(
            new SelectedFileSearchRoot(
                searchRoot, "ui-perf", FileSearchRootAuthorizationKinds.None,
                null, null, clock.UtcNow),
            cancellationToken);
        if (rootResult.IsFailure)
        {
            throw new InvalidOperationException(
                $"UI perf fixture failed to add search root: {rootResult.ErrorMessage}");
        }

        OcrPresetService presets = new(database, library, clock);
        Result<OcrPreset> presetResult = await presets.CreatePresetAsync(
            "UI perf mock", null, OcrEngineIds.Mock, OcrModelIds.MockBasic,
            null, "{}", false, cancellationToken);
        if (presetResult.IsFailure)
        {
            throw new InvalidOperationException(
                $"UI perf fixture failed to create OCR preset: {presetResult.ErrorMessage}");
        }

        CredentialStore credentials = new(settingsPath);
        Result<ProviderCredentialMetadata> credentialResult = await credentials.SaveAsync(
            ProviderIds.MinerU, "UI performance fixture", "ui-perf-inert-token",
            cancellationToken);
        if (credentialResult.IsFailure)
        {
            throw new InvalidOperationException(
                $"UI perf fixture failed to save credential: {credentialResult.ErrorMessage}");
        }

        ExistingDatabaseSetup setup =
            await ExistingDatabaseSetupInspector.InspectAsync(database.DatabasePath, cancellationToken);
        if (!setup.IsComplete)
        {
            throw new InvalidOperationException(
                "UI perf fixture setup is not complete after seeding; " +
                $"CurrentStep={setup.CurrentStep}, IsComplete={setup.IsComplete}");
        }
    }

    private static async Task StopServicesAsync(MainWindowViewModel? viewModel, Exception? pending)
    {
        if (viewModel is null)
        {
            return;
        }

        try
        {
            await viewModel.DisposeAsync();
        }
        catch (Exception disposeException) when (pending is not null)
        {
            // A cleanup failure must not mask the original assertion/cancellation, but it is
            // still reported instead of being swallowed silently.
            Trace(true, $"[ui-perf] view-model cleanup after failure threw " +
                        $"{disposeException.GetType().Name}: {disposeException.Message}");
        }
    }

    private static bool SafeIsUiThread(Dispatcher dispatcher)
    {
        try
        {
            return dispatcher.CheckAccess();
        }
        catch
        {
            return false;
        }
    }

    private static double ElapsedMs(long start, long stop)
    {
        return (stop - start) * 1000.0 / Stopwatch.Frequency;
    }

    private static void Trace(bool verbose, string message)
    {
        if (verbose)
        {
            Console.Error.WriteLine(message);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }

                return;
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>
    /// The three monotonic boundaries captured by one first-rows pass, all measured from the same
    /// start timestamp. <see cref="FirstRowsMs"/> is the headline metric and keeps its original
    /// definition (construction through startup/first-run completion); it is deliberately not
    /// redefined to include paging. <see cref="FirstBatchReadyMs"/> and
    /// <see cref="AllPagesDrainedMs"/> are the newer, versioned split measurements.
    /// </summary>
    private sealed record FirstRowsMeasurement(
        double FirstRowsMs,
        double FirstBatchReadyMs,
        double AllPagesDrainedMs,
        int RowCount);

    private sealed class PageTarget
    {
        public string DocumentInstanceId { get; init; } = "";
        public string PageId { get; init; } = "";
    }

    private sealed class ProbeClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class ProbeClipboard : IClipboardService
    {
        public string? Text { get; private set; }

        public Task SetTextAsync(string text)
        {
            Text = text;
            return Task.CompletedTask;
        }

        public Task<string?> GetTextAsync()
        {
            return Task.FromResult(Text);
        }
    }

    private sealed class NoOpLogger : IAppLogger
    {
        public Task LogAsync(string operation, string message)
        {
            return Task.CompletedTask;
        }
    }
}
