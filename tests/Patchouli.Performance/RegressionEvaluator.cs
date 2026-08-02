using System.Globalization;

namespace Patchouli.Performance;

public sealed record RegressionTolerance(
    double Deterministic = 1.5,
    double Allocation = 2.0,
    double Latency = 3.0,
    double LatencyCeilingMs = 2000,
    bool EnforceUiBudgets = false,
    double UiHeartbeatBudgetMs = 250,
    double UiInteractiveFrameworkColdBudgetMs = 2000,
    double UiInteractiveFrameworkHotBudgetMs = 1000,
    double UiFirstLibraryRowsBudgetMs = 3000);

/// <summary>
/// Compares a current run against a committed baseline and flags regressions. Counts (SQL
/// statements, rows read) and allocations are deterministic and machine-independent, so they use
/// strict multiplicative budgets. Latency uses both a relative budget and an absolute ceiling so
/// a slower or faster runner does not produce flaky results; latency is only an "obvious
/// regression" signal here, never a fine-grained gate.
/// </summary>
public static class RegressionEvaluator
{
    public static RegressionReport Evaluate(
        PerformanceReport current,
        PerformanceReport? baseline,
        string baselinePath,
        RegressionTolerance tolerance)
    {
        List<RegressionCheck> checks = new();
        int failed = 0;

        if (baseline is null)
        {
            checks.Add(new RegressionCheck("run", "baseline", "missing", null, null, "warn"));
            // Absolute AC2/AC3 UI budgets are compared against PRD numbers, not a baseline, so
            // they must still be enforced when a baseline is missing.
            CompareUi(checks, ref failed, current.Ui, null, tolerance);
            return new RegressionReport(BaselineName(baselinePath), 0, failed, failed == 0, checks);
        }
        else
        {
            Dictionary<string, OperationReport> baselineByName =
                baseline.Operations.ToDictionary(operation => operation.Name, StringComparer.Ordinal);
            foreach (OperationReport operation in current.Operations)
            {
                if (!baselineByName.TryGetValue(operation.Name, out OperationReport? reference))
                {
                    checks.Add(new RegressionCheck(operation.Name, "operation", "new", null, null, "warn"));
                    continue;
                }

                Compare(checks, ref failed, operation.Name, "sql_statements", operation.SqlStatements,
                    reference.SqlStatements, tolerance.Deterministic, null);
                Compare(checks, ref failed, operation.Name, "rows_read", operation.RowsRead,
                    reference.RowsRead, tolerance.Deterministic, null);
                Compare(checks, ref failed, operation.Name, "allocated_bytes", operation.AllocatedBytes,
                    reference.AllocatedBytes, tolerance.Allocation, null);
                Compare(checks, ref failed, operation.Name, "median_ms", operation.MedianMs,
                    reference.MedianMs, tolerance.Latency, tolerance.LatencyCeilingMs);
                Compare(checks, ref failed, operation.Name, "p95_ms", operation.P95Ms,
                    reference.P95Ms, tolerance.Latency, tolerance.LatencyCeilingMs);
            }
        }

        // UI budgets are absolute (AC2/AC3), so they are still enforced without a baseline.
        CompareUi(checks, ref failed, current.Ui, baseline?.Ui, tolerance);

        return new RegressionReport(BaselineName(baselinePath), checks.Count, failed, failed == 0, checks);
    }

    private static void CompareUi(
        List<RegressionCheck> checks,
        ref int failed,
        ReportUi? current,
        ReportUi? baseline,
        RegressionTolerance tolerance)
    {
        if (current is not { Measured: true } ui)
        {
            return;
        }

        if (baseline is { Measured: true })
        {
            CompareNullable(checks, ref failed, "ui.framework_cold", "ms", ui.InteractiveFrameworkColdMs,
                baseline.InteractiveFrameworkColdMs, tolerance.Latency, tolerance.LatencyCeilingMs);
            CompareNullable(checks, ref failed, "ui.framework_hot", "ms", ui.InteractiveFrameworkHotMs,
                baseline.InteractiveFrameworkHotMs, tolerance.Latency, tolerance.LatencyCeilingMs);
            CompareNullable(checks, ref failed, "ui.first_rows_cold", "ms", ui.FirstLibraryRowsColdMs,
                baseline.FirstLibraryRowsColdMs, tolerance.Latency, tolerance.LatencyCeilingMs);
            CompareNullable(checks, ref failed, "ui.first_rows_hot", "ms", ui.FirstLibraryRowsHotMs,
                baseline.FirstLibraryRowsHotMs, tolerance.Latency, tolerance.LatencyCeilingMs);
            CompareNullable(checks, ref failed, "ui.heartbeat_max_gap", "ms", ui.HeartbeatMaxGapMs,
                baseline.HeartbeatMaxGapMs, tolerance.Latency, tolerance.LatencyCeilingMs);
        }
        else
        {
            checks.Add(new RegressionCheck("ui", "measured", "new", null, null, "warn"));
        }

        if (!tolerance.EnforceUiBudgets)
        {
            return;
        }

        // AC2 / AC3 absolute budgets from the PRD. Enforcement is opt-in and is run on the
        // designated runner against the full (scalable) fixture where the numbers are meaningful.
        CompareBudget(checks, ref failed, "ui.framework_cold", "budget", ui.InteractiveFrameworkColdMs,
            tolerance.UiInteractiveFrameworkColdBudgetMs);
        CompareBudget(checks, ref failed, "ui.framework_hot", "budget", ui.InteractiveFrameworkHotMs,
            tolerance.UiInteractiveFrameworkHotBudgetMs);
        CompareBudget(checks, ref failed, "ui.first_rows_cold", "budget", ui.FirstLibraryRowsColdMs,
            tolerance.UiFirstLibraryRowsBudgetMs);
        CompareBudget(checks, ref failed, "ui.heartbeat_max_gap", "budget", ui.HeartbeatMaxGapMs,
            tolerance.UiHeartbeatBudgetMs);
    }

    private static void CompareBudget(
        List<RegressionCheck> checks,
        ref int failed,
        string operation,
        string metric,
        double? observed,
        double budget)
    {
        if (observed is not double value)
        {
            checks.Add(new RegressionCheck(operation, metric, "not measured", Format(budget), Format(budget), "warn"));
            return;
        }

        bool exceeded = value > budget;
        if (exceeded)
        {
            failed++;
        }

        checks.Add(new RegressionCheck(operation, metric, Format(value), Format(budget), Format(budget),
            exceeded ? "fail" : "pass"));
    }

    private static string BaselineName(string baselinePath)
    {
        return Path.GetFileName(baselinePath);
    }

    private static void Compare(
        List<RegressionCheck> checks,
        ref int failed,
        string operation,
        string metric,
        double observed,
        double baseline,
        double tolerance,
        double? absoluteCeiling)
    {
        double budget = baseline * tolerance;
        if (absoluteCeiling is double ceiling)
        {
            budget = Math.Max(budget, ceiling);
        }

        bool exceeded = observed > budget;
        if (exceeded)
        {
            failed++;
        }

        checks.Add(new RegressionCheck(operation, metric, Format(observed), Format(baseline), Format(budget),
            exceeded ? "fail" : "pass"));
    }

    private static void CompareNullable(
        List<RegressionCheck> checks,
        ref int failed,
        string operation,
        string metric,
        double? observed,
        double? baseline,
        double tolerance,
        double? absoluteCeiling)
    {
        if (observed is not double current || baseline is not double reference)
        {
            checks.Add(new RegressionCheck(operation, metric, "not measured", "not measured", "not measured", "warn"));
            return;
        }

        Compare(checks, ref failed, operation, metric, current, reference, tolerance, absoluteCeiling);
    }

    private static string Format(double value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
