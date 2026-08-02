using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core;
using Patchouli.Core.Documents;
using Patchouli.Core.Time;
using SQLitePCL;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Evidence;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Search;
using Patchouli.Mcp;

namespace Patchouli.Performance;

public sealed record PerformanceRunResult(PerformanceReport Report, bool RegressionFailed);

/// <summary>
/// Drives one benchmark run: seeds the deterministic fixture through the same services the
/// production host uses, measures representative read and write operations through the MCP read
/// path, and produces the machine-readable report plus an optional regression check.
/// </summary>
public static class PerformanceRunner
{
    public static async Task<PerformanceRunResult> RunAsync(PerfOptions options, CancellationToken cancellationToken,
        string? migrationsDirectory = null)
    {
        if (options.UiOnly)
        {
            UiPerfResult uiOnly = await UiPerfProbe.RunAsync(options,
                                      migrationsDirectory ?? Path.Combine(AppContext.BaseDirectory, "migrations"),
                                      cancellationToken)
                                  ?? throw new InvalidOperationException(
                                      "The UI-only performance probe was not enabled.");
            PerformanceReport report = BuildReport(options, BuildEnvironment(),
                new ReportFixture(options.ProfileName, options.Items, options.PagesPerItem, options.BoxesPerPage,
                    0, 0, 0, 0, options.Seed),
                new ReportDatabase(0, 0, 0, 0, 0, "not-measured", 0),
                new ReportCache(false, 0, 0, null, false), [], [], uiOnly.ToReportUi(), null);
            string json = report.ToJson();
            ReportPrivacy.AssertSafe(json);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);
            await File.WriteAllTextAsync(options.OutputPath, json, cancellationToken);
            return new PerformanceRunResult(report, false);
        }

        string databasePath = options.DatabasePath
                              ?? Path.Combine(Path.GetTempPath(), $"patchouli-perf-{Guid.NewGuid():N}.sqlite");
        bool ownsDatabase = options.DatabasePath is null;
        CountingConnectionFactory database = new(databasePath);

        try
        {
            PerformanceFixtureState fixture = await PerformanceFixture.SeedAsync(
                database, migrationsDirectory ?? Path.Combine(AppContext.BaseDirectory, "migrations"),
                options.Seed, options.Items, options.PagesPerItem, options.BoxesPerPage, cancellationToken);

            IClock clock = new FixedClock(new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero));
            LibraryIdentityService libraryService = new(database, clock);
            SearchProfileService profiles = new(database, libraryService, clock);
            SqliteSearchService search = new(database, profiles);
            EvidenceReferenceService evidence = new(database, clock);
            McpReadApi api = new(database, search, evidence);
            DocumentTreeService tree = new(database, clock, new MarkdigMarkdownEngine());

            if (!options.Quiet)
            {
                Console.Error.WriteLine(
                    $"[patchouli-perf] seeded {fixture.TotalItems} items / {fixture.TotalPages} pages / " +
                    $"{fixture.TotalBoxes} boxes at {databasePath}");
            }

            (long mainAfterSeed, long walAfterSeed) = DbBytes(database);
            List<OperationReport> operations = new();
            QueryCounters counters = database.Counters;

            operations.Add(await MeasureAsync(counters, "browse_items_first_page", options.Iterations,
                (iteration, ct) => api.BrowseItemsAsync(0, 20, cancellationToken: ct), null, null, cancellationToken));
            operations.Add(await MeasureAsync(counters, "mcp_item_fetch", options.Iterations,
                (iteration, ct) => api.GetItemMetadataAsync(fixture.ItemIds[0], ct), null, null, cancellationToken));
            operations.Add(await MeasureAsync(counters, "mcp_document_status", options.Iterations,
                (iteration, ct) => api.GetDocumentStatusAsync(fixture.DocumentIds[0], ct), null, null,
                cancellationToken));
            operations.Add(await MeasureAsync(counters, "mcp_document_outline", options.Iterations,
                (iteration, ct) => api.GetDocumentOutlineAsync(fixture.DocumentIds[0], ct), null, null,
                cancellationToken));

            int coldPageCount = Math.Min(options.Iterations, fixture.PageIds.Count - 1);
            operations.Add(await MeasureAsync(counters, "mcp_page_text_cold", coldPageCount,
                (iteration, ct) => api.GetPageTextAsync(new McpPageTextRequest(fixture.PageIds[iteration + 1]), ct),
                null, null, cancellationToken));
            operations.Add(await MeasureAsync(counters, "mcp_page_text_warm", options.Iterations,
                (iteration, ct) => api.GetPageTextAsync(new McpPageTextRequest(fixture.PageIds[0]), ct),
                null, null, cancellationToken));
            operations.Add(await MeasureAsync(counters, "mcp_page_blocks", options.Iterations,
                (iteration, ct) => api.GetPageBlocksAsync(new McpPageBlocksRequest(fixture.PageIds[0]), ct),
                null, null, cancellationToken));
            if (fixture.EvidenceRefId is not null)
            {
                operations.Add(await MeasureAsync(counters, "mcp_evidence_fetch", options.Iterations,
                    (iteration, ct) => api.GetEvidenceRecordAsync(fixture.EvidenceRefId!, ct), null, null,
                    cancellationToken));
            }

            long beforeOcr = DbBytes(database).Main;
            operations.Add(await MeasureAsync(counters, "ocr_stage_adopt", options.Iterations,
                async (iteration, ct) =>
                {
                    DocumentBoxSeed[] seeds = BoxSeeds(iteration);
                    Core.Results.Result<DocumentTreeRevision> staging = await tree.StagePageAsync(
                        fixture.DocumentIds[0], fixture.PageIds[0], seeds,
                        DocumentTreeRevisionSource.Import, cancellationToken: ct);
                    if (staging.IsFailure)
                    {
                        throw new InvalidOperationException(
                            $"stage failed: {staging.ErrorCode} {staging.ErrorMessage}");
                    }

                    Core.Results.Result<DocumentTreeRevision> committed = await tree.AdoptStagingRevisionAsync(
                        staging.Value.TreeRevisionId, ct);
                    if (committed.IsFailure)
                    {
                        throw new InvalidOperationException(
                            $"adopt failed: {committed.ErrorCode} {committed.ErrorMessage}");
                    }
                },
                beforeOcr, null, cancellationToken));
            long afterOcr = DbBytes(database).Main;

            OperationReport coldPage = operations.Single(operation => operation.Name == "mcp_page_text_cold");
            OperationReport warmPage = operations.Single(operation => operation.Name == "mcp_page_text_warm");
            ReportCache cache = new(
                false,
                coldPage.MedianMs,
                warmPage.MedianMs,
                coldPage.MedianMs <= 0 ? null : warmPage.MedianMs / coldPage.MedianMs,
                warmPage.MedianMs < coldPage.MedianMs * 0.8,
                api.CompiledMarkdownCacheMetrics.Hits,
                api.CompiledMarkdownCacheMetrics.Misses,
                api.CompiledMarkdownCacheMetrics.Evictions);

            ReportDatabase databaseStats = await DatabaseStatsAsync(database, mainAfterSeed, walAfterSeed, afterOcr);
            IReadOnlyList<QueryPlanLine> queryPlan = await QueryPlanAsync(database, cancellationToken);

            ReportUi? ui = (await UiPerfProbe.RunAsync(options,
                    migrationsDirectory ?? Path.Combine(AppContext.BaseDirectory, "migrations"), cancellationToken))
                ?.ToReportUi();

            ReportEnvironment environment = BuildEnvironment();
            ReportFixture reportFixture = new(
                options.ProfileName, options.Items, options.PagesPerItem, options.BoxesPerPage,
                fixture.TotalItems, fixture.TotalPages, fixture.TotalBoxes, fixture.TotalSearchUnits, options.Seed);

            RegressionReport? regression = null;
            if (options.CheckRegression)
            {
                string baselinePath = options.BaselinePath
                                      ?? Path.Combine(".agent", "perf", $"baseline.{options.ProfileName}.json");
                if (!File.Exists(baselinePath))
                {
                    throw new InvalidOperationException(
                        $"Regression baseline '{baselinePath}' was not found. Generate it with --emit-baseline " +
                        "before running --check.");
                }

                PerformanceReport? baseline = PerformanceReport.FromJson(File.ReadAllText(baselinePath));
                if (baseline is null)
                {
                    throw new InvalidOperationException(
                        $"Regression baseline '{baselinePath}' is not a valid performance report.");
                }

                RegressionTolerance tolerance = new(
                    options.DeterministicTolerance, options.AllocationTolerance,
                    options.LatencyTolerance, options.LatencyCeilingMs, options.EnforceUiBudgets);
                regression = RegressionEvaluator.Evaluate(BuildReport(
                        options, environment, reportFixture, databaseStats, cache, operations, queryPlan, ui, null),
                    baseline, baselinePath, tolerance);
                if (!options.Quiet)
                {
                    Console.Error.WriteLine(
                        $"[patchouli-perf] regression check vs {baselinePath}: " +
                        $"{(regression.Passed ? "passed" : $"FAILED ({regression.FailedChecks} checks)")}");
                    foreach (RegressionCheck check in regression.Checks.Where(check => check.Result == "fail"))
                    {
                        Console.Error.WriteLine(
                            $"  FAIL {check.Operation}.{check.Metric}: observed {check.Observed}, " +
                            $"baseline {check.Baseline}, budget {check.Budget}");
                    }
                }
            }

            PerformanceReport report = BuildReport(
                options, environment, reportFixture, databaseStats, cache, operations, queryPlan, ui, regression);

            string json = report.ToJson();
            ReportPrivacy.AssertSafe(json);

            string? outputPath = options.OutputPath;
            if (!string.IsNullOrEmpty(outputPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
                await File.WriteAllTextAsync(outputPath, json, cancellationToken);
            }

            if (options.EmitBaselinePath is string baselineOutput)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(baselineOutput))!);
                await File.WriteAllTextAsync(baselineOutput, json, cancellationToken);
            }

            if (options.ReportPath is string markdownPath)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(markdownPath))!);
                await File.WriteAllTextAsync(markdownPath, RenderMarkdown(report), cancellationToken);
            }

            if (!options.Quiet)
            {
                PrintSummary(report, regression);
            }

            return new PerformanceRunResult(report, regression is { Passed: false });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (ownsDatabase && !options.KeepDatabase)
            {
                TryDelete(databasePath);
                TryDelete(databasePath + "-wal");
                TryDelete(databasePath + "-shm");
            }
        }
    }

    private static PerformanceReport BuildReport(
        PerfOptions options,
        ReportEnvironment environment,
        ReportFixture fixture,
        ReportDatabase databaseStats,
        ReportCache cache,
        IReadOnlyList<OperationReport> operations,
        IReadOnlyList<QueryPlanLine> queryPlan,
        ReportUi? ui,
        RegressionReport? regression)
    {
        return new PerformanceReport
        {
            Profile = options.ProfileName,
            GeneratedAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Environment = environment,
            Fixture = fixture,
            Database = databaseStats,
            Cache = cache,
            Ui = ui,
            Operations = operations,
            QueryPlan = queryPlan,
            Regression = regression
        };
    }

    private static ReportEnvironment BuildEnvironment()
    {
        return new ReportEnvironment(
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            SqliteVersion(),
            BuildInfo.Version);
    }

    private static string SqliteVersion()
    {
        try
        {
            return raw.sqlite3_libversion().utf8_to_string();
        }
        catch
        {
            return "unknown";
        }
    }

    private static async Task<OperationReport> MeasureAsync(
        QueryCounters counters,
        string name,
        int iterations,
        Func<int, CancellationToken, Task> operation,
        long? dbBefore,
        long? dbAfter,
        CancellationToken cancellationToken)
    {
        // Warm up JIT, SQLite statement caches, and one-time allocations once so the timed
        // window reflects steady-state work. The operation receives index -1 for the warmup;
        // operations that pick per-iteration resources must keep warmup resources distinct.
        counters.Reset();
        await operation(-1, cancellationToken);

        List<double> samples = new(iterations);
        long statements = 0;
        long rowsRead = 0;
        // Allocation is measured on this thread only: the harness may run in-process alongside
        // other work (tests, the UI host), and a process-wide GC total would absorb allocations
        // made by unrelated threads. A per-thread delta stays deterministic and machine-independent.
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
        {
            counters.Reset();
            long start = Stopwatch.GetTimestamp();
            await operation(index, cancellationToken);
            long stop = Stopwatch.GetTimestamp();
            samples.Add((stop - start) * 1000.0 / Stopwatch.Frequency);
            statements = counters.Statements;
            rowsRead = counters.RowsRead;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        return new OperationReport(
            name, iterations,
            PerfMetrics.Median(samples), PerfMetrics.Percentile(samples, 0.95),
            samples.Min(), samples.Max(), PerfMetrics.Mean(samples),
            statements, rowsRead, allocated, dbBefore, dbAfter);
    }

    private static DocumentBoxSeed[] BoxSeeds(int iteration)
    {
        int boxes = 8;
        DocumentBoxSeed[] seeds = new DocumentBoxSeed[boxes];
        for (int boxIndex = 0; boxIndex < boxes; boxIndex++)
        {
            double top = 0.04 + 0.9 * boxIndex / boxes;
            seeds[boxIndex] = new DocumentBoxSeed(
                null, null, boxIndex, DocumentBoxType.Text, null, null,
                new Core.Layout.NormalizedBBox(0.04, top, 0.92, 0.88 / boxes),
                new TextBoxPayload($"perf ocr adoption sample {iteration} box {boxIndex:00}"),
                null);
        }

        return seeds;
    }

    private const int IntendedBusyTimeoutMs = 5000;

    private static async Task<ReportDatabase> DatabaseStatsAsync(
        SqliteConnectionFactory database,
        long bytesAfterSeed,
        long walAfterSeed,
        long bytesAfterOcr)
    {
        string journalMode = "unknown";
        int busyTimeout = 0;
        await using SqliteConnection connection = database.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteScalarAsync<int>($"PRAGMA busy_timeout={IntendedBusyTimeoutMs};");
        journalMode = await connection.ExecuteScalarAsync<string>("PRAGMA journal_mode;") ?? "unknown";
        busyTimeout = await connection.ExecuteScalarAsync<int>("PRAGMA busy_timeout;");

        long bytesAfterOcrWal = DbBytes(database).Wal;
        return new ReportDatabase(
            bytesAfterSeed, walAfterSeed, bytesAfterOcr, bytesAfterOcrWal,
            Math.Max(0, bytesAfterOcr - bytesAfterSeed), journalMode, busyTimeout);
    }

    private static (long Main, long Wal) DbBytes(SqliteConnectionFactory database)
    {
        FileInfo main = new(database.DatabasePath);
        FileInfo wal = new(database.DatabasePath + "-wal");
        return (main.Exists ? main.Length : 0, wal.Exists ? wal.Length : 0);
    }

    private static async Task<IReadOnlyList<QueryPlanLine>> QueryPlanAsync(
        SqliteConnectionFactory database, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        IReadOnlyList<PlanRow> rows = (await connection.QueryAsync<PlanRow>(
            """
            EXPLAIN QUERY PLAN
            select item_id, title
            from items
            where deleted_at is null
            order by updated_at desc, item_id
            limit 20;
            """)).ToArray();
        return rows.Select(row => new QueryPlanLine(NormalizePlanDetail(row.Detail))).ToArray();
    }

    private static string NormalizePlanDetail(string detail)
    {
        return Regex.Replace(detail, @"\b\d+\b", "?");
    }

    private sealed class PlanRow
    {
        public string Detail { get; set; } = "";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void PrintSummary(PerformanceReport report, RegressionReport? regression)
    {
        Console.WriteLine();
        Console.WriteLine($"patchouli-perf {report.Profile}  generator {report.Environment.GeneratorVersion}  " +
                          $"sqlite {report.Environment.SqliteVersion}");
        Console.WriteLine(
            $"fixture {report.Fixture.TotalItems} items / {report.Fixture.TotalPages} pages / " +
            $"{report.Fixture.TotalBoxes} boxes / {report.Fixture.TotalSearchUnits} search units  " +
            $"db {report.Database.BytesAfterSeed / 1024} KiB  journal {report.Database.JournalMode}");
        Console.WriteLine();
        Console.WriteLine($"{"operation",-26} {"median",10} {"p95",10} {"sql",8} {"rows",8} {"alloc KiB",10}");
        foreach (OperationReport operation in report.Operations)
        {
            Console.WriteLine(
                $"{operation.Name,-26} {operation.MedianMs,9:0.##}ms {operation.P95Ms,9:0.##}ms " +
                $"{operation.SqlStatements,8} {operation.RowsRead,8} {operation.AllocatedBytes / 1024.0,9:0.#}");
        }

        if (report.Cache.Measured is false)
        {
            Console.WriteLine(
                $"cache behavior: cold median {report.Cache.ColdMedianMs:0.##}ms, " +
                $"warm median {report.Cache.WarmMedianMs:0.##}ms, " +
                $"warm materially faster: {report.Cache.WarmMateriallyFaster}, " +
                $"hits/misses/evictions {report.Cache.Hits}/{report.Cache.Misses}/{report.Cache.Evictions}, " +
                $"hit rate {report.Cache.HitRate:P1}");
        }

        if (report.Ui is { Measured: true } ui)
        {
            Console.WriteLine();
            Console.WriteLine("ui probes (headless Avalonia dispatcher):");
            Console.WriteLine(
                $"  framework cold {ui.InteractiveFrameworkColdMs:0.##}ms / hot {ui.InteractiveFrameworkHotMs:0.##}ms");
            Console.WriteLine(
                $"  first rows cold {ui.FirstLibraryRowsColdMs:0.##}ms / hot {ui.FirstLibraryRowsHotMs:0.##}ms " +
                $"({ui.FirstLibraryRowCount} rows)");
            Console.WriteLine(
                $"  heartbeat max gap {ui.HeartbeatMaxGapMs:0.##}ms over {ui.HeartbeatSamples} samples " +
                $"(target {ui.HeartbeatTargetMs:0}ms), ui-thread db commands {ui.UiThreadDatabaseCommands}");
        }

        if (regression is not null)
        {
            Console.WriteLine();
            Console.WriteLine(regression.Passed
                ? $"regression check passed ({regression.ComparedOperations} comparisons vs {regression.Baseline})."
                : $"regression check FAILED ({regression.FailedChecks} checks) vs {regression.Baseline}.");
        }
    }

    private static string RenderMarkdown(PerformanceReport report)
    {
        StringBuilder builder = new();
        builder.Append("# Patchouli performance report\n\n");
        builder.Append($"- Profile: `{report.Profile}`\n");
        builder.Append($"- Generated: `{report.GeneratedAtUtc}`\n");
        builder.Append($"- Environment: {report.Environment.Os} / {report.Environment.Framework} / " +
                       $"{report.Environment.Architecture}\n");
        builder.Append($"- SQLite: `{report.Environment.SqliteVersion}`\n");
        builder.Append(
            $"- Fixture: {report.Fixture.TotalItems} items, {report.Fixture.TotalPages} pages, " +
            $"{report.Fixture.TotalBoxes} boxes, {report.Fixture.TotalSearchUnits} search units, " +
            $"seed {report.Fixture.Seed}\n");
        builder.Append($"- Database: {report.Database.BytesAfterSeed} bytes (WAL " +
                       $"{report.Database.WalBytesAfterSeed} bytes, journal `{report.Database.JournalMode}`), " +
                       $"OCR growth {report.Database.OcrGrowthBytes} bytes\n\n");
        builder.Append($"- Compiled Markdown cache: {report.Cache.Hits} hits / {report.Cache.Misses} misses / " +
                       $"{report.Cache.Evictions} evictions (hit rate {report.Cache.HitRate:P1})\n\n");

        builder.Append("| operation | median | p95 | sql | rows | alloc KiB |\n");
        builder.Append("|---|---|---|---|---|---|\n");
        foreach (OperationReport operation in report.Operations)
        {
            builder.Append(
                $"| {operation.Name} | {operation.MedianMs:0.##} ms | {operation.P95Ms:0.##} ms | " +
                $"{operation.SqlStatements} | {operation.RowsRead} | {operation.AllocatedBytes / 1024.0:0.#} |\n");
        }

        builder.Append("\n## Query plan (browse first page)\n\n```\n");
        foreach (QueryPlanLine line in report.QueryPlan)
        {
            builder.Append(line.Detail).Append('\n');
        }

        builder.Append("```\n");

        if (report.Ui is { Measured: true } ui)
        {
            builder.Append("\n## UI probes (headless Avalonia dispatcher)\n\n");
            builder.Append(
                $"- Interactive framework: cold **{ui.InteractiveFrameworkColdMs:0.##} ms**, " +
                $"hot **{ui.InteractiveFrameworkHotMs:0.##} ms**\n");
            builder.Append(
                $"- First library rows: cold **{ui.FirstLibraryRowsColdMs:0.##} ms**, " +
                $"hot **{ui.FirstLibraryRowsHotMs:0.##} ms** ({ui.FirstLibraryRowCount} rows)\n");
            builder.Append(
                $"- 100 ms heartbeat max observed gap: **{ui.HeartbeatMaxGapMs:0.##} ms** " +
                $"over {ui.HeartbeatSamples} samples; database commands on the UI dispatcher: " +
                $"{ui.UiThreadDatabaseCommands}\n");
            builder.Append("- AC3 budget: heartbeat max gap ≤ 250 ms; database work never on the UI dispatcher.\n");
        }

        if (report.Regression is not null)
        {
            builder.Append("\n## Regression\n\n");
            builder.Append(report.Regression.Passed ? "Passed.\n" : "**Failed.**\n");
            foreach (RegressionCheck check in report.Regression.Checks)
            {
                builder.Append($"- `{check.Result}` {check.Operation}.{check.Metric}: observed {check.Observed}, " +
                               $"baseline {check.Baseline}, budget {check.Budget}\n");
            }
        }

        return builder.ToString();
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
