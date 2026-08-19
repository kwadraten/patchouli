namespace Patchouli.Performance;

public enum PerfProfile
{
    Smoke,
    Full
}

/// <summary>
/// Parsed command-line configuration for the patchouli-perf harness. All options are
/// documented by <c>--help</c>. Scale of the synthetic fixture is controlled by the
/// profile plus <c>--items</c>/<c>--pages-per-item</c>/<c>--boxes-per-page</c> so a
/// full-scale fixture can be built on a designated runner without shipping a large binary
/// fixture in the repository.
/// </summary>
public sealed record PerfOptions(
    PerfProfile Profile,
    int Iterations,
    long Seed,
    string? DatabasePath,
    bool KeepDatabase,
    string OutputPath,
    string? ReportPath,
    string? EmitBaselinePath,
    bool CheckRegression,
    string? BaselinePath,
    double DeterministicTolerance,
    double AllocationTolerance,
    double LatencyTolerance,
    double LatencyCeilingMs,
    int Items,
    int PagesPerItem,
    int BoxesPerPage,
    bool Quiet,
    bool UiProbes,
    bool EnforceUiBudgets,
    bool UiOnly)
{
    public string ProfileName => Profile == PerfProfile.Full ? "full" : "smoke";

    public static PerfOptions Parse(string[] args)
    {
        PerfProfile profile = PerfProfile.Smoke;
        int iterations = 5;
        long seed = 20260802;
        string? databasePath = null;
        bool keepDatabase = false;
        string? outputPath = null;
        string? reportPath = null;
        string? emitBaseline = null;
        bool check = false;
        string? baselinePath = null;
        double deterministicTolerance = 1.5;
        double allocationTolerance = 2.0;
        double latencyTolerance = 3.0;
        double latencyCeilingMs = 2000;
        bool quiet = false;
        bool uiProbes = false;
        bool enforceUiBudgets = false;
        bool uiOnly = false;
        int? requestedItems = null;
        int? requestedPagesPerItem = null;
        int? requestedBoxesPerPage = null;

        for (int index = 0; index < args.Length; index++)
        {
            string flag = args[index];
            string? next = index + 1 < args.Length ? args[index + 1] : null;
            switch (flag)
            {
                case "--help":
                case "-h":
                    throw new PerfUsageException(HelpText);
                case "--profile":
                    profile = Value(next, flag) switch
                    {
                        "smoke" => PerfProfile.Smoke,
                        "full" => PerfProfile.Full,
                        _ => throw new PerfUsageException("--profile must be 'smoke' or 'full'.")
                    };
                    index++;
                    break;
                case "--iterations":
                    iterations = IntValue(next, flag, 1, 1000);
                    index++;
                    break;
                case "--items":
                    requestedItems = IntValue(next, flag, 1, 1000000);
                    index++;
                    break;
                case "--pages-per-item":
                    requestedPagesPerItem = IntValue(next, flag, 1, 10000);
                    index++;
                    break;
                case "--boxes-per-page":
                    requestedBoxesPerPage = IntValue(next, flag, 1, 100000);
                    index++;
                    break;
                case "--seed":
                    seed = LongValue(next, flag);
                    index++;
                    break;
                case "--db":
                    databasePath = Value(next, flag);
                    index++;
                    break;
                case "--keep-db":
                    keepDatabase = true;
                    break;
                case "--output":
                    outputPath = Value(next, flag);
                    index++;
                    break;
                case "--report":
                    reportPath = Value(next, flag);
                    index++;
                    break;
                case "--emit-baseline":
                    emitBaseline = Value(next, flag);
                    index++;
                    break;
                case "--check":
                    check = true;
                    break;
                case "--baseline":
                    baselinePath = Value(next, flag);
                    index++;
                    break;
                case "--det-tolerance":
                    deterministicTolerance = DoubleValue(next, flag, 1.0, 100.0);
                    index++;
                    break;
                case "--alloc-tolerance":
                    allocationTolerance = DoubleValue(next, flag, 1.0, 100.0);
                    index++;
                    break;
                case "--latency-tolerance":
                    latencyTolerance = DoubleValue(next, flag, 1.0, 100.0);
                    index++;
                    break;
                case "--latency-ceiling-ms":
                    latencyCeilingMs = DoubleValue(next, flag, 0.0, 600000.0);
                    index++;
                    break;
                case "--quiet":
                    quiet = true;
                    break;
                case "--ui":
                    uiProbes = true;
                    break;
                case "--enforce-ui-budgets":
                    enforceUiBudgets = true;
                    break;
                case "--ui-only":
                    uiProbes = true;
                    uiOnly = true;
                    break;
                default:
                    throw new PerfUsageException($"Unknown option '{flag}'.");
            }
        }

        if (iterations < 3)
        {
            throw new PerfUsageException("--iterations must be at least 3 for meaningful percentiles.");
        }

        bool isFull = profile == PerfProfile.Full;
        if (!isFull && iterations < 5)
        {
            iterations = 5;
        }

        int items = requestedItems ?? (isFull ? 100 : 20);
        int pagesPerItem = requestedPagesPerItem ?? (isFull ? 8 : 3);
        int boxesPerPage = requestedBoxesPerPage ?? (isFull ? 25 : 8);

        string stamp =
            DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string profileName = isFull ? "full" : "smoke";
        string defaultOutput = outputPath
                               ?? Path.Combine("artifacts", "perf", $"{profileName}-{stamp}.json");
        return new PerfOptions(
            profile, iterations, seed, databasePath, keepDatabase, defaultOutput, reportPath, emitBaseline, check,
            baselinePath, deterministicTolerance, allocationTolerance, latencyTolerance, latencyCeilingMs,
            items, pagesPerItem, boxesPerPage, quiet, uiProbes, enforceUiBudgets, uiOnly);
    }

    private static string Value(string? value, string flag)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
        {
            throw new PerfUsageException($"{flag} requires a value.");
        }

        return value;
    }

    private static int IntValue(string? value, string flag, int minimum, int maximum)
    {
        if (!int.TryParse(value, out int parsed) || parsed < minimum || parsed > maximum)
        {
            throw new PerfUsageException($"{flag} must be an integer between {minimum} and {maximum}.");
        }

        return parsed;
    }

    private static long LongValue(string? value, string flag)
    {
        if (!long.TryParse(value, out long parsed) || parsed < 0)
        {
            throw new PerfUsageException($"{flag} must be a non-negative integer.");
        }

        return parsed;
    }

    private static double DoubleValue(string? value, string flag, double minimum, double maximum)
    {
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double parsed) ||
            parsed < minimum || parsed > maximum)
        {
            throw new PerfUsageException($"{flag} must be a number between {minimum} and {maximum}.");
        }

        return parsed;
    }

    private const string HelpText =
        """
        patchouli-perf: repeatable, privacy-safe performance smoke and fixture benchmark.

        Usage:
          dotnet run --project src/Patchouli.Performance -- [options]

        Options:
          --profile <smoke|full>       Fixture scale. smoke (default) is small enough to run in
                                       normal tests and CI; full targets a designated runner and can
                                       be scaled with --items / --pages-per-item / --boxes-per-page
                                       (defaults: 100, 8, 25).
          --items <n>                  Fixture item count (default 20 for smoke, 100 for full).
          --pages-per-item <n>         Pages per item (default 3 for smoke, 8 for full).
          --boxes-per-page <n>         Boxes per page (default 8 for smoke, 25 for full).
          --iterations <n>             Samples per operation (default 5, minimum 3).
          --seed <n>                   Deterministic fixture seed (default 20260802).
          --db <path>                  Reuse a database path instead of a temp file.
          --keep-db                    Keep the database after the run.
          --output <path.json>         JSON report path (default artifacts/perf/<profile>-<stamp>.json).
          --report <path.md>           Optional human-readable markdown report path.
          --emit-baseline <path>       Write this run's metric values as a baseline file (no --check).
          --check                      Compare this run against a baseline and fail on regression.
          --baseline <path>            Baseline JSON (default .agents/perf/baseline.<profile>.json).
          --det-tolerance <x>          SQL/rows regression factor (default 1.5).
          --alloc-tolerance <x>        Allocation regression factor (default 2.0).
          --latency-tolerance <x>      Latency regression factor (default 3.0).
          --latency-ceiling-ms <n>     Absolute latency ceiling for --check (default 2000).
          --quiet                      Suppress progress output.
           --ui                         Run the real-UI (headless Avalonia) probes: interactive
                                       framework cold/hot, first library rows cold/hot, and the
                                       100 ms dispatcher heartbeat max-gap during a box adoption
                                        at the profile fixture scale. Measured on the actual UI
                                        dispatcher, never synthesized in the console.
           --ui-only                    Run only the UI probes. This uses a bounded first-screen
                                        fixture and stages/adopts the configured total Box count in
                                        one atomic revision for the heartbeat measurement.
           --enforce-ui-budgets         Fail the run when the PRD AC2/AC3 budgets are exceeded:
                                        interactive framework 2000 ms cold / 1000 ms hot, first
                                       library rows 3000 ms, heartbeat max-gap 250 ms.

        Exit codes: 0 ok, 1 run error, 2 regression check failed, 3 privacy/report error.
        """;
}

public sealed class PerfUsageException(string message) : Exception(message);
