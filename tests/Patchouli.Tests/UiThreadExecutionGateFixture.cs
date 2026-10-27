using System.Text.Json;
using Patchouli.Core.Diagnostics;
using Patchouli.Testing;

namespace Patchouli.Tests;

/// <summary>Checks production UI call sites, not direct service calls made by test preparation.</summary>
public sealed class UiThreadExecutionGateFixture : IAsyncLifetime
{
    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        UiThreadExecutionViolation[] violations = UiThreadExecutionGuard.GetViolations()
            .Where(violation => violation.UiMethod is not null).ToArray();
        string report = Path.Combine(RepositoryTestEnvironment.TestRoot,
            $"ui-thread-execution-{Environment.ProcessId}.json");
        await File.WriteAllTextAsync(report,
            JsonSerializer.Serialize(violations, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(violations.Length == 0,
            "Blocking work executed on the UI thread. Report: " + report + "\n" +
            string.Join("\n", violations.Select(violation => violation.Key + "\n" + violation.Stack)));
    }
}
