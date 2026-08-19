using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Documents;
using Patchouli.Ocr;
using Patchouli.UI;
using Patchouli.UI.ViewModels;

namespace Patchouli.Performance;

/// <summary>
/// The real-UI performance probe results. Every value is measured on an actual headless Avalonia
/// dispatcher against a real <see cref="MainWindow"/>, real view models, and the real host
/// services. Nothing here is synthesized in the console.
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
    int FirstLibraryRowCount)
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
            FirstLibraryRowCount);
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
            // The startup probe only needs enough data to exercise the first-screen read path.
            // The heartbeat below independently stages/adopts the requested total Box count in one
            // document transaction, avoiding a misleading 10,000-page fixture setup at 500k scale.
            await PerformanceFixture.SeedAsync(
                seedDatabase, migrationsDirectory, options.Seed, Math.Min(options.Items, 100), 1, 1,
                cancellationToken);
            SqliteConnection.ClearAllPools();
            WriteSettings(settingsPath, databasePath, root);

            HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
            Trace(!options.Quiet, $"[ui-perf] session ready");

            UiPerfResult result = await session.Dispatch(
                async () => await MeasureOnUiThreadAsync(options, databasePath, settingsPath, cancellationToken),
                cancellationToken);
            Trace(!options.Quiet, $"[ui-perf] dispatch complete");
            await session.DisposeAsync();
            Trace(!options.Quiet, $"[ui-perf] session disposed");

            SqliteConnection.ClearAllPools();
            return result;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteDirectory(root);
        }
    }

    private static async Task<UiPerfResult> MeasureOnUiThreadAsync(
        PerfOptions options,
        string databasePath,
        string settingsPath,
        CancellationToken cancellationToken)
    {
        double frameworkCold = await MeasureFrameworkAsync(settingsPath, databasePath);
        Trace(!options.Quiet, $"[ui-perf] framework cold {frameworkCold:0.##} ms");
        (double firstRowsCold, int rowCount) = await MeasureFirstRowsAsync(settingsPath, databasePath);
        Trace(!options.Quiet, $"[ui-perf] first rows cold {firstRowsCold:0.##} ms ({rowCount} rows)");

        double frameworkHot = await MeasureFrameworkAsync(settingsPath, databasePath);
        Trace(!options.Quiet, $"[ui-perf] framework hot {frameworkHot:0.##} ms");
        (double firstRowsHot, int _) = await MeasureFirstRowsAsync(settingsPath, databasePath);
        Trace(!options.Quiet, $"[ui-perf] first rows hot {firstRowsHot:0.##} ms");

        (double heartbeatMaxGap, int heartbeatSamples, long uiThreadCommands) = await MeasureHeartbeatAsync(
            databasePath, options, cancellationToken);
        Trace(!options.Quiet, $"[ui-perf] heartbeat max gap {heartbeatMaxGap:0.##} ms over {heartbeatSamples} " +
                              $"samples (ui-thread db commands {uiThreadCommands})");

        return new UiPerfResult(true, frameworkCold, frameworkHot, firstRowsCold, firstRowsHot, heartbeatMaxGap,
            heartbeatSamples, uiThreadCommands, rowCount);
    }

    /// <summary>
    /// Measures the time from view-model/window construction to a shown, measured, and arranged
    /// window — the interactive framework boundary on the real UI dispatcher. Runs on the UI thread.
    /// </summary>
    private static async Task<double> MeasureFrameworkAsync(string settingsPath, string databasePath)
    {
        long start = Stopwatch.GetTimestamp();
        MainWindowViewModel viewModel = CreateViewModel(settingsPath, databasePath);
        MainWindow window = new(viewModel);
        window.Width = 1280;
        window.Height = 820;
        window.Show();
        window.Measure(new Size(1280, 820));
        window.Arrange(new Rect(0, 0, 1280, 820));
        long stop = Stopwatch.GetTimestamp();
        window.Close();
        await StopServicesAsync(viewModel);
        return ElapsedMs(start, stop);
    }

    /// <summary>
    /// Measures the time from the interactive framework to the first library rows visible in the
    /// shell. This is the real cold-open path: <see cref="AppServices.CreateAsync"/> (migrations,
    /// OCR reconciliation, queue start) plus the first rows query projected into the shell.
    /// </summary>
    private static async Task<(double Ms, int RowCount)> MeasureFirstRowsAsync(
        string settingsPath, string databasePath)
    {
        long start = Stopwatch.GetTimestamp();
        MainWindowViewModel viewModel = CreateViewModel(settingsPath, databasePath);
        MainWindow window = new(viewModel);
        window.Show();
        await window.ShowFirstRunIfNeededAsync(false);
        int rowCount = viewModel.Shell.Items.Count;
        long stop = Stopwatch.GetTimestamp();
        window.Close();
        await StopServicesAsync(viewModel);
        return (ElapsedMs(start, stop), rowCount);
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
            SqliteConnection.ClearAllPools();
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
                null, null, boxIndex, DocumentBoxType.Text, null, null,
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
        MainWindowViewModel viewModel = new(new ProbeClipboard(), new NoOpLogger(), autoStartMcpServer: false,
            settingsPath: settingsPath);
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
            RememberLastDatabase = true,
            UseMockOcrOnly = true
        };
        (PatchouliAppSettings.Default() with { Runtime = runtime }).Save(settingsPath);
    }

    private static async Task StopServicesAsync(MainWindowViewModel viewModel)
    {
        try
        {
            await viewModel.StopMcpServerAsync();
        }
        catch
        {
            // The probe never starts the MCP server; stopping it is best-effort.
        }

        try
        {
            AppServices services = await viewModel.ServicesAsync();
            await ((QueuedOcrRunCoordinator)services.Ocr).Queue.StopAsync();
        }
        catch
        {
            // The OCR queue is best-effort to release the database file for cleanup.
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
