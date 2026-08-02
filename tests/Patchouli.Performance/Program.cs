using Patchouli.Performance;

try
{
    PerfOptions options = PerfOptions.Parse(args);
    using CancellationTokenSource cancellation = new(TimeSpan.FromMinutes(45));
    PerformanceRunResult result = await PerformanceRunner.RunAsync(options, cancellation.Token);
    if (!options.Quiet && options.OutputPath.Length > 0)
    {
        Console.WriteLine($"Report: {options.OutputPath}");
    }

    return result.RegressionFailed ? 2 : 0;
}
catch (PerfUsageException usage)
{
    Console.Error.WriteLine(usage.Message);
    return 2;
}
catch (PerfPrivacyException privacy)
{
    Console.Error.WriteLine($"patchouli-perf abort: {privacy.Message}");
    return 3;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"patchouli-perf failed: {exception.Message}");
    return 1;
}
