using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Performance;

namespace Patchouli.Tests;

public sealed class PerformanceHarnessTests
{
    private sealed class UiThreadIndicator
    {
        private int _isSet;

        public bool IsSet => Volatile.Read(ref _isSet) == 1;

        public void Set(bool value)
        {
            Interlocked.Exchange(ref _isSet, value ? 1 : 0);
        }
    }

    [Fact]
    public void Median_of_known_samples_is_correct()
    {
        PerfMetrics.Median([1.0, 2.0, 3.0, 4.0, 100.0]).Should().Be(3.0);
        PerfMetrics.Median([1.0, 2.0, 3.0, 4.0]).Should().Be(2.5);
        PerfMetrics.Median([]).Should().Be(0.0);
    }

    [Fact]
    public void P95_uses_nearest_rank()
    {
        PerfMetrics.Percentile([1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0], 0.95).Should().Be(10.0);
        PerfMetrics.Percentile([1.0, 2.0, 3.0, 4.0], 0.95).Should().Be(4.0);
        PerfMetrics.Percentile([], 0.95).Should().Be(0.0);
    }

    [Fact]
    public void Regression_evaluator_passes_equal_runs()
    {
        PerformanceReport baseline = ReportWith(Operation("op", 3, 12, 1000));
        PerformanceReport current = ReportWith(Operation("op", 3, 12, 1000));

        RegressionReport result = RegressionEvaluator.Evaluate(current, baseline, "baseline.json",
            new RegressionTolerance());

        result.Passed.Should().BeTrue();
        result.FailedChecks.Should().Be(0);
        result.Checks.Should().OnlyContain(check => check.Result == "pass");
    }

    [Fact]
    public void Regression_evaluator_flags_sql_and_row_count_blowups()
    {
        PerformanceReport baseline = ReportWith(Operation("op", 3, 12, 1000));
        PerformanceReport current = ReportWith(Operation("op", 300, 1200, 1000));

        RegressionReport result = RegressionEvaluator.Evaluate(current, baseline, "baseline.json",
            new RegressionTolerance());

        result.Passed.Should().BeFalse();
        result.FailedChecks.Should().Be(2);
        result.Checks.Where(check => check.Result == "fail").Select(check => check.Metric)
            .Should().BeEquivalentTo("sql_statements", "rows_read");
    }

    [Fact]
    public void Regression_evaluator_uses_absolute_latency_ceiling()
    {
        PerformanceReport baseline = ReportWith(Operation("op", 1, 1, 1));
        PerformanceReport current = ReportWith(Operation("op", 1, 1, 1,
            2500, 2600, 2400, 2700, 2500));

        RegressionReport result = RegressionEvaluator.Evaluate(current, baseline, "baseline.json",
            new RegressionTolerance(LatencyCeilingMs: 2000));

        result.Passed.Should().BeFalse();
        result.Checks.Where(check => check.Result == "fail").Select(check => check.Metric)
            .Should().BeEquivalentTo("median_ms", "p95_ms");
    }

    [Fact]
    public void Regression_evaluator_warns_on_missing_baseline()
    {
        PerformanceReport current = ReportWith(Operation("op", 3, 12, 1000));

        RegressionReport result = RegressionEvaluator.Evaluate(current, null, "missing.json",
            new RegressionTolerance());

        result.Passed.Should().BeTrue();
        result.Checks.Single().Result.Should().Be("warn");
    }

    [Fact]
    public void Counting_connection_counts_statements_and_rows_without_recording_sql()
    {
        string path = Path.Combine(Path.GetTempPath(), $"patchouli-count-{Guid.NewGuid():N}.sqlite");
        CountingConnectionFactory factory = new(path);
        try
        {
            factory.Counters.Statements.Should().Be(0);
            // Command execution is measured through the DbConnection/DbCommand abstraction, the
            // same way Dapper (and therefore the production services) reach SQLite. A call on a
            // concrete SqliteConnection reference is hidden by Microsoft.Data.Sqlite's `new
            // CreateCommand()` and is not how production code executes queries.
            System.Data.Common.DbConnection connection = factory.CreateConnection();
            using (connection)
            {
                connection.Open();
                using (System.Data.Common.DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "create table t (id integer primary key, value text not null);";
                    command.ExecuteNonQuery();
                }

                using (System.Data.Common.DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "insert into t (id, value) values (1, 'a'), (2, 'b'), (3, 'c');";
                    command.ExecuteNonQuery();
                }

                using (System.Data.Common.DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "select id, value from t order by id;";
                    using System.Data.Common.DbDataReader reader = command.ExecuteReader();
                    int rows = 0;
                    while (reader.Read())
                    {
                        rows++;
                    }

                    rows.Should().Be(3);
                }
            }

            factory.Counters.Statements.Should().Be(3);
            factory.Counters.Writes.Should().Be(2);
            factory.Counters.RowsRead.Should().Be(3);

            // Read-only connections (used by every MCP read path) must be instrumented too, but
            // stay file-level read only: they count reads and never gain write capability.
            factory.Counters.Reset();
            using (System.Data.Common.DbConnection readOnly = factory.CreateReadConnection())
            {
                readOnly.Open();
                using System.Data.Common.DbCommand command = readOnly.CreateCommand();
                command.CommandText = "select id, value from t order by id;";
                using System.Data.Common.DbDataReader reader = command.ExecuteReader();
                int readRows = 0;
                while (reader.Read())
                {
                    readRows++;
                }

                readRows.Should().Be(3);
                System.Data.Common.DbCommand write = readOnly.CreateCommand();
                write.CommandText = "insert into t (id, value) values (9, 'blocked');";
                write.Invoking(command => command.ExecuteNonQuery()).Should().Throw<SqliteException>();
            }

            factory.Counters.Statements.Should().Be(2);
            factory.Counters.Writes.Should().Be(1);
            factory.Counters.RowsRead.Should().Be(3);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(path);
            TryDelete(path + "-wal");
            TryDelete(path + "-shm");
        }
    }

    [Fact]
    public void Counting_connection_tracks_commands_executed_on_the_ui_thread()
    {
        string path = Path.Combine(Path.GetTempPath(), $"patchouli-count-ui-{Guid.NewGuid():N}.sqlite");
        UiThreadIndicator indicator = new();
        CountingConnectionFactory factory = new(path, () => indicator.IsSet);
        try
        {
            System.Data.Common.DbConnection connection = factory.CreateConnection();
            using (connection)
            {
                connection.Open();
                using (System.Data.Common.DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "create table t (id integer primary key);";
                    command.ExecuteNonQuery();
                }

                factory.Counters.UiThreadCommands.Should().Be(0);
                indicator.Set(true);
                using (System.Data.Common.DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "select 1;";
                    command.ExecuteScalar();
                }

                factory.Counters.UiThreadCommands.Should().Be(1);
                indicator.Set(false);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(path);
            TryDelete(path + "-wal");
            TryDelete(path + "-shm");
        }
    }

    [Fact]
    public void Report_privacy_scan_rejects_evref_and_local_paths()
    {
        ReportPrivacy.IsSafe("{}").Should().BeTrue();
        ReportPrivacy.IsSafe("{\"median_ms\": 1.5}").Should().BeTrue();
        ReportPrivacy.IsSafe("{\"x\":\"evref:v2:abc\"}").Should().BeFalse();
        ReportPrivacy.IsSafe("{\"x\":\"evref=abc\"}").Should().BeFalse();
        ReportPrivacy.IsSafe("{\"x\":\"C:\\\\Users\\\\secret\\\\db.sqlite\"}").Should().BeFalse();
        ReportPrivacy.IsSafe("{\"x\":\"api_key=topsecret\"}").Should().BeFalse();
    }

    [Fact]
    public void Report_privacy_scan_accepts_versioned_evidence_uri_without_local_path()
    {
        const string versionedUri =
            "patchouli://texts/00000000-0000-0000-0000-000000000000/page-1.md?rev=00000000-0000-0000-0000-000000000001&box=00000000-0000-0000-0000-000000000002";
        ReportPrivacy.IsSafe($"{{\"uri\":\"{versionedUri}\"}}").Should().BeTrue();
    }

    [Fact]
    public void Options_parse_defaults_to_smoke_profile_and_floor_iterations()
    {
        PerfOptions options = PerfOptions.Parse(["--quiet"]);
        options.Profile.Should().Be(PerfProfile.Smoke);
        options.Iterations.Should().BeGreaterThanOrEqualTo(5);
        options.Items.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Options_reject_unknown_flags_and_invalid_values()
    {
        Action unknown = () => PerfOptions.Parse(["--nope"]);
        unknown.Should().Throw<PerfUsageException>();
        Action badIterations = () => PerfOptions.Parse(["--iterations", "0"]);
        badIterations.Should().Throw<PerfUsageException>();
    }

    [Fact]
    public void Options_parse_fixture_scale_flags_and_profile_defaults()
    {
        PerfOptions smoke = PerfOptions.Parse(["--quiet"]);
        smoke.Items.Should().Be(20);
        smoke.PagesPerItem.Should().Be(3);
        smoke.BoxesPerPage.Should().Be(8);

        PerfOptions full = PerfOptions.Parse(["--profile", "full"]);
        full.Items.Should().Be(100);
        full.PagesPerItem.Should().Be(8);
        full.BoxesPerPage.Should().Be(25);

        PerfOptions scaled = PerfOptions.Parse(
            ["--profile", "full", "--items", "1000", "--pages-per-item", "10", "--boxes-per-page", "50"]);
        scaled.Items.Should().Be(1000);
        scaled.PagesPerItem.Should().Be(10);
        scaled.BoxesPerPage.Should().Be(50);

        PerfOptions uiOnly = PerfOptions.Parse(["--ui-only"]);
        uiOnly.UiProbes.Should().BeTrue();
        uiOnly.UiOnly.Should().BeTrue();
    }

    [Fact]
    public void Options_reject_invalid_fixture_scale_flags()
    {
        Action tooLarge = () => PerfOptions.Parse(["--items", "0"]);
        tooLarge.Should().Throw<PerfUsageException>();
        Action missingValue = () => PerfOptions.Parse(["--pages-per-item"]);
        missingValue.Should().Throw<PerfUsageException>();
    }

    [Fact]
    public async Task Runner_smoke_produces_a_privacy_safe_report_with_cache_behavior()
    {
        string output = Path.Combine(Path.GetTempPath(), $"patchouli-perf-report-{Guid.NewGuid():N}.json");
        PerfOptions options = PerfOptions.Parse(
            ["--profile", "smoke", "--iterations", "3", "--seed", "42", "--output", output, "--quiet"]);

        PerformanceRunResult result = await PerformanceRunner.RunAsync(
            options, CancellationToken.None, TestPaths.MigrationsDirectory);

        result.RegressionFailed.Should().BeFalse();
        result.Report.Operations.Should().Contain(operation => operation.Name == "browse_items_first_page");
        result.Report.Operations.Should().Contain(operation => operation.Name == "mcp_page_text_cold");
        result.Report.Operations.Should().Contain(operation => operation.Name == "ocr_begin_commit");
        result.Report.Operations.Should().Contain(operation => operation.Name == "mcp_versioned_uri_fetch");

        OperationReport cold = result.Report.Operations.Single(operation => operation.Name == "mcp_page_text_cold");
        OperationReport warm = result.Report.Operations.Single(operation => operation.Name == "mcp_page_text_warm");
        cold.SqlStatements.Should().BeGreaterThan(0);
        warm.SqlStatements.Should().BeGreaterThan(0);
        warm.SqlStatements.Should().BeLessThan(cold.SqlStatements,
            "a warm page-text read must skip the DB-backed Markdown compile and hit the cache");
        result.Report.Cache.WarmVsColdRatio.Should().NotBeNull();

        string json = result.Report.ToJson();
        ReportPrivacy.IsSafe(json).Should().BeTrue();
        File.Exists(output).Should().BeTrue();
        ReportPrivacy.IsSafe(File.ReadAllText(output)).Should().BeTrue();
    }

    [Fact]
    public async Task Runner_regression_check_passes_against_an_emitted_baseline()
    {
        string baselinePath = Path.Combine(Path.GetTempPath(), $"patchouli-baseline-{Guid.NewGuid():N}.json");
        string output = Path.Combine(Path.GetTempPath(), $"patchouli-check-{Guid.NewGuid():N}.json");
        string[] common = ["--profile", "smoke", "--iterations", "3", "--seed", "7", "--quiet"];

        PerformanceRunResult baselineRun = await PerformanceRunner.RunAsync(
            PerfOptions.Parse(common.Append("--emit-baseline").Append(baselinePath).ToArray()),
            CancellationToken.None, TestPaths.MigrationsDirectory);

        PerformanceRunResult selfCheck = await PerformanceRunner.RunAsync(
            PerfOptions.Parse(common.Append("--output").Append(output).Append("--check").Append("--baseline")
                .Append(baselinePath).ToArray()),
            CancellationToken.None, TestPaths.MigrationsDirectory);

        baselineRun.RegressionFailed.Should().BeFalse();
        if (selfCheck.Report.Regression is { } regression && !regression.Passed)
        {
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "perf-selfcheck-failures.txt"),
                regression.Checks.Select(check => $"{check.Result} {check.Operation}.{check.Metric}: " +
                                                  $"observed {check.Observed}, baseline {check.Baseline}, budget {check.Budget}"));
        }

        selfCheck.RegressionFailed.Should().BeFalse();
        selfCheck.Report.Regression.Should().NotBeNull();
        selfCheck.Report.Regression!.Passed.Should().BeTrue();
        File.Exists(baselinePath).Should().BeTrue();
    }

    [Fact]
    public void Options_parse_ui_flags()
    {
        PerfOptions withUi = PerfOptions.Parse(["--ui"]);
        withUi.UiProbes.Should().BeTrue();
        withUi.EnforceUiBudgets.Should().BeFalse();

        PerfOptions enforced = PerfOptions.Parse(["--ui", "--enforce-ui-budgets"]);
        enforced.UiProbes.Should().BeTrue();
        enforced.EnforceUiBudgets.Should().BeTrue();

        PerfOptions plain = PerfOptions.Parse(["--quiet"]);
        plain.UiProbes.Should().BeFalse();
        plain.EnforceUiBudgets.Should().BeFalse();
    }

    [Fact]
    public void Regression_evaluator_compares_ui_metrics_against_baseline()
    {
        PerformanceReport baseline = ReportWithUi(Ui("ui.framework_cold", 1200, 1200));
        PerformanceReport current = ReportWithUi(Ui("ui.framework_cold", 4800, 4800));

        RegressionReport result = RegressionEvaluator.Evaluate(current, baseline, "baseline.json",
            new RegressionTolerance(Latency: 3.0));

        result.Passed.Should().BeFalse();
        result.Checks.Where(check => check.Operation == "ui.framework_cold" && check.Result == "fail")
            .Should().NotBeEmpty();
    }

    [Fact]
    public void Regression_evaluator_enforces_ac2_and_ac3_ui_budgets()
    {
        ReportUi violating = new(
            true, 2500, 1500, 3500, 600, 500, 100, 40, 0, 20);
        PerformanceReport current = ReportWithUi(violating);

        RegressionReport result = RegressionEvaluator.Evaluate(current, null, "missing.json",
            new RegressionTolerance(EnforceUiBudgets: true));

        result.Passed.Should().BeFalse();
        result.Checks.Where(check => check.Operation.StartsWith("ui.", StringComparison.Ordinal) &&
                                     check.Result == "fail")
            .Select(check => check.Operation)
            .Should().BeEquivalentTo(
                "ui.framework_cold", "ui.framework_hot", "ui.first_rows_cold", "ui.heartbeat_max_gap");
    }

    [Fact]
    public void Regression_evaluator_skips_ui_budget_checks_when_not_enforcing()
    {
        ReportUi violating = new(true, 9999, 9999, 9999, 9999, 9999, 100, 40, 0, 20);
        PerformanceReport current = ReportWithUi(violating);

        RegressionReport result = RegressionEvaluator.Evaluate(current, null, "missing.json",
            new RegressionTolerance());

        result.Passed.Should().BeTrue();
        result.Checks.Should().NotContain(check => check.Operation.StartsWith("ui.", StringComparison.Ordinal) &&
                                                   check.Result == "fail");
    }

    [Fact]
    public void Regression_evaluator_warns_when_ui_measured_but_baseline_is_not()
    {
        ReportUi measured = new(true, 500, 500, 500, 500, 112, 100, 40, 0, 20);
        PerformanceReport current = ReportWithUi(measured);
        PerformanceReport baselineWithoutUi = ReportWith(Operation("op", 3, 12, 1000));

        RegressionReport result = RegressionEvaluator.Evaluate(current, baselineWithoutUi, "baseline.json",
            new RegressionTolerance());

        result.Passed.Should().BeTrue();
        result.Checks.Single(check => check.Operation == "ui" && check.Result == "warn").Should().NotBeNull();
    }

    [Fact]
    public void UiPerfResult_maps_to_report_ui()
    {
        UiPerfResult probe = new(true, 1000, 100, 2000, 200, 111, 25, 0, 20);

        ReportUi report = probe.ToReportUi();

        report.Measured.Should().BeTrue();
        report.InteractiveFrameworkColdMs.Should().Be(1000);
        report.InteractiveFrameworkHotMs.Should().Be(100);
        report.FirstLibraryRowsColdMs.Should().Be(2000);
        report.FirstLibraryRowsHotMs.Should().Be(200);
        report.HeartbeatMaxGapMs.Should().Be(111);
        report.HeartbeatTargetMs.Should().Be(100);
        report.HeartbeatSamples.Should().Be(25);
        report.UiThreadDatabaseCommands.Should().Be(0);
        report.FirstLibraryRowCount.Should().Be(20);
    }

    private static PerformanceReport ReportWithUi(ReportUi ui)
    {
        return ReportWith(ui, Operation("op", 3, 12, 1000));
    }

    private static ReportUi Ui(string ignoredOperation, double cold, double hot)
    {
        return new ReportUi(true, cold, hot, cold, hot, 112, 100, 40, 0, 20);
    }

    private static OperationReport Operation(string name, long sqlStatements, long rowsRead, long allocatedBytes,
        double medianMs = 10, double p95Ms = 15, double minMs = 8, double maxMs = 20, double meanMs = 11)
    {
        return new OperationReport(name, 5, medianMs, p95Ms, minMs, maxMs, meanMs, sqlStatements, rowsRead,
            allocatedBytes, null, null);
    }

    private static PerformanceReport ReportWith(params OperationReport[] operations)
    {
        return ReportWith(null, operations);
    }

    private static PerformanceReport ReportWith(ReportUi? ui, params OperationReport[] operations)
    {
        return new PerformanceReport
        {
            Profile = "test",
            GeneratedAtUtc = "2026-08-02T00:00:00Z",
            Environment = new ReportEnvironment("test", "test", "test", "test", "test"),
            Fixture = new ReportFixture("test", 1, 1, 1, 1, 1, 1, 1, 1),
            Database = new ReportDatabase(0, 0, 0, 0, 0, "wal", 5000),
            Cache = new ReportCache(false, 10, 5, 0.5, true),
            Ui = ui,
            Operations = operations
        };
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
}
