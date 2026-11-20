using FluentAssertions;
using Patchouli.UI.Diagnostics;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

/// <summary>
/// Application shutdown runs each teardown step in isolation: a failing or hung step is reported
/// and the remaining steps (above all the final settings flush) still run.
/// </summary>
public sealed class ApplicationShutdownStepTests
{
    [Fact]
    public async Task A_failing_step_is_reported_and_does_not_throw()
    {
        List<(Exception Exception, string Boundary, string? Operation)> reports = [];
        RecordingUnexpectedExceptionSink sink = new((exception, boundary, operation) =>
            reports.Add((exception, boundary, operation)));
        bool nextStepRan = false;

        await MainWindowViewModel.RunShutdownStepAsync("dispose-shell",
            () => throw new InvalidOperationException("boom"), TimeSpan.FromSeconds(5), sink);
        await MainWindowViewModel.RunShutdownStepAsync("flush-settings", () =>
        {
            nextStepRan = true;
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(5), sink);

        nextStepRan.Should().BeTrue();
        reports.Should().ContainSingle();
        reports[0].Exception.Should().BeOfType<InvalidOperationException>();
        reports[0].Boundary.Should().Be("application-shutdown");
        reports[0].Operation.Should().Be("dispose-shell");
    }

    [Fact]
    public async Task A_faulted_async_step_is_reported()
    {
        List<Exception> reports = [];
        RecordingUnexpectedExceptionSink sink = new((exception, _, _) => reports.Add(exception));

        await MainWindowViewModel.RunShutdownStepAsync("stop-mcp", async () =>
        {
            await Task.Yield();
            throw new IOException("late failure");
        }, TimeSpan.FromSeconds(5), sink);

        reports.Should().ContainSingle().Which.Should().BeOfType<IOException>();
    }

    [Fact]
    public async Task A_hung_step_times_out_and_is_reported()
    {
        List<(Exception Exception, string? Operation)> reports = [];
        RecordingUnexpectedExceptionSink sink = new((exception, _, operation) => reports.Add((exception, operation)));
        TaskCompletionSource never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task step = MainWindowViewModel.RunShutdownStepAsync("dispose-library-session", () => never.Task,
            TimeSpan.FromMilliseconds(50), sink);
        Task finished = await Task.WhenAny(step, Task.Delay(TimeSpan.FromSeconds(10)));

        finished.Should().BeSameAs(step, "a hung step must not block the rest of shutdown");
        reports.Should().ContainSingle();
        reports[0].Exception.Should().BeOfType<TimeoutException>();
        reports[0].Operation.Should().Be("dispose-library-session");
    }
}
