using System.Text.Json;
using Patchouli.Mcp;

namespace Patchouli.Performance;

public sealed record ReportEnvironment(
    string Os,
    string Framework,
    string Architecture,
    string SqliteVersion,
    string GeneratorVersion);

public sealed record ReportFixture(
    string Profile,
    int Items,
    int PagesPerItem,
    int BoxesPerPage,
    long TotalItems,
    long TotalPages,
    long TotalBoxes,
    long TotalSearchUnits,
    long Seed);

public sealed record ReportDatabase(
    long BytesAfterSeed,
    long WalBytesAfterSeed,
    long BytesAfterOcr,
    long WalBytesAfterOcr,
    long OcrGrowthBytes,
    string JournalMode,
    int BusyTimeoutMs);

public sealed record ReportCache(
    bool Measured,
    double? ColdMedianMs,
    double? WarmMedianMs,
    double? WarmVsColdRatio,
    bool WarmMateriallyFaster,
    long Hits = 0,
    long Misses = 0,
    long Evictions = 0)
{
    public double? HitRate => Hits + Misses == 0 ? null : (double)Hits / (Hits + Misses);
}

/// <summary>
/// Real-UI (headless Avalonia) performance probes. All timings are measured on the actual UI
/// dispatcher against a real <see cref="Patchouli.UI.MainWindow"/>, real view models, and the real
/// host services; they are never synthesized in the console. <c>null</c> fields mean the probe was
/// not run for this profile.
/// </summary>
public sealed record ReportUi(
    bool Measured,
    double? InteractiveFrameworkColdMs,
    double? InteractiveFrameworkHotMs,
    double? FirstLibraryRowsColdMs,
    double? FirstLibraryRowsHotMs,
    double? HeartbeatMaxGapMs,
    double? HeartbeatTargetMs,
    int? HeartbeatSamples,
    long? UiThreadDatabaseCommands,
    int? FirstLibraryRowCount);

public sealed record OperationReport(
    string Name,
    int Iterations,
    double MedianMs,
    double P95Ms,
    double MinMs,
    double MaxMs,
    double MeanMs,
    long SqlStatements,
    long RowsRead,
    long AllocatedBytes,
    long? DbBytesBefore,
    long? DbBytesAfter);

public sealed record RegressionCheck(
    string Operation,
    string Metric,
    string Observed,
    string? Baseline,
    string? Budget,
    string Result);

public sealed record RegressionReport(
    string Baseline,
    int ComparedOperations,
    int FailedChecks,
    bool Passed,
    IReadOnlyList<RegressionCheck> Checks);

public sealed record QueryPlanLine(string Detail);

/// <summary>
/// The machine-readable performance report. It carries only counters, latencies, fixture scale,
/// and environment facts; it never carries document body text, SQL text, local paths,
/// EvidenceRef values, or secrets.
/// </summary>
public sealed class PerformanceReport
{
    public int Schema { get; init; } = 1;
    public string Profile { get; init; } = "";
    public string GeneratedAtUtc { get; init; } = "";
    public ReportEnvironment Environment { get; init; } = null!;
    public ReportFixture Fixture { get; init; } = null!;
    public ReportDatabase Database { get; init; } = null!;
    public ReportCache Cache { get; init; } = null!;
    public ReportUi? Ui { get; init; }
    public IReadOnlyList<OperationReport> Operations { get; init; } = [];
    public IReadOnlyList<QueryPlanLine> QueryPlan { get; init; } = [];
    public RegressionReport? Regression { get; init; }

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, SerializerOptions);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static PerformanceReport? FromJson(string json)
    {
        return JsonSerializer.Deserialize<PerformanceReport>(json, SerializerOptions);
    }
}

/// <summary>
/// Last-line defense that the report never leaks sensitive content. The harness never writes
/// bodies, paths, SQL, or EvidenceRefs; this scan (using the same patterns the MCP surface uses)
/// turns any accidental leak into a hard failure.
/// </summary>
public static class ReportPrivacy
{
    private static readonly string[] ForbiddenMarkers =
    [
        "evref:v2:",
        "Bearer ",
        "sk-",
        "api_key=",
        "provider_secret",
        "file:///"
    ];

    public static void AssertSafe(string reportJson)
    {
        if (!IsSafe(reportJson))
        {
            string? marker = ForbiddenMarkers.FirstOrDefault(candidate =>
                reportJson.Contains(candidate, StringComparison.OrdinalIgnoreCase));
            throw new PerfPrivacyException(marker is null
                ? "The performance report contains sensitive content and was not written."
                : $"The performance report contains sensitive marker '{marker}' and was not written.");
        }
    }

    public static bool IsSafe(string reportJson)
    {
        if (!McpOutputSanitizer.IsSafe(reportJson))
        {
            return false;
        }

        return !ForbiddenMarkers.Any(marker =>
            reportJson.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class PerfPrivacyException(string message) : Exception(message);
