namespace Patchouli.Performance;

public static class PerfMetrics
{
    public static double Median(IReadOnlyCollection<double> samples)
    {
        if (samples.Count == 0)
        {
            return 0;
        }

        double[] sorted = samples.OrderBy(static value => value).ToArray();
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    /// <summary>Nearest-rank percentile, for example 0.95 for p95.</summary>
    public static double Percentile(IReadOnlyCollection<double> samples, double percentile)
    {
        if (samples.Count == 0)
        {
            return 0;
        }

        double[] sorted = samples.OrderBy(static value => value).ToArray();
        int rank = (int)Math.Clamp(Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[rank];
    }

    public static double Mean(IReadOnlyCollection<double> samples)
    {
        return samples.Count == 0 ? 0 : samples.Average();
    }
}
