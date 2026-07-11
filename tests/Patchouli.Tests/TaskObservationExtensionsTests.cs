using FluentAssertions;
using Patchouli.UI.Diagnostics;

namespace Patchouli.Tests;

public sealed class TaskObservationExtensionsTests
{
    [Fact]
    public async Task Observe_reports_fault_with_context()
    {
        var reported = new TaskCompletionSource<(Exception Exception, string Boundary, string? Operation)>();
        var sink = new RecordingUnexpectedExceptionSink((exception, boundary, operation) =>
            reported.SetResult((exception, boundary, operation)));

        Task.FromException(new InvalidOperationException("failure"))
            .Observe("test-boundary", "test-operation", sink: sink);

        var report = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        report.Exception.Should().BeOfType<InvalidOperationException>();
        report.Boundary.Should().Be("test-boundary");
        report.Operation.Should().Be("test-operation");
    }

    [Fact]
    public async Task Observe_ignores_cancellation_when_supplied_token_is_canceled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var reported = false;
        var sink = new RecordingUnexpectedExceptionSink((_, _, _) => reported = true);

        Task.FromCanceled(cancellation.Token).Observe("test", cancellationToken: cancellation.Token, sink: sink);
        await Task.Delay(50);

        reported.Should().BeFalse();
    }

    [Fact]
    public async Task Observe_reports_cancellation_when_supplied_token_is_not_canceled()
    {
        using var taskCancellation = new CancellationTokenSource();
        taskCancellation.Cancel();
        var reported = new TaskCompletionSource<Exception>();
        var sink = new RecordingUnexpectedExceptionSink((exception, _, _) => reported.SetResult(exception));

        Task.FromCanceled(taskCancellation.Token).Observe("test", sink: sink);

        (await reported.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeOfType<TaskCanceledException>();
    }
}
