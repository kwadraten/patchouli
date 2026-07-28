using FluentAssertions;
using Patchouli.UI.Diagnostics;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

public sealed class AsyncCommandExceptionTests
{
    [Fact]
    public async Task ICommand_execute_reports_unexpected_exception()
    {
        TaskCompletionSource<(Exception Exception, string Boundary, string? Operation)> reported =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingUnexpectedExceptionSink sink = new((exception, boundary, operation) =>
            reported.TrySetResult((exception, boundary, operation)));
        AsyncCommand command = new(() => Task.FromException(new InvalidOperationException("boom")), sink,
            "test-command");

        command.Execute(null);

        (Exception Exception, string Boundary, string? Operation) result =
            await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        result.Exception.Should().BeOfType<InvalidOperationException>();
        result.Boundary.Should().Be("ui-command");
        result.Operation.Should().Be("test-command");
    }

    [Fact]
    public async Task ExecuteAsync_preserves_exception_for_awaiting_caller()
    {
        int reports = 0;
        RecordingUnexpectedExceptionSink sink = new((_, _, _) => reports++);
        AsyncCommand command = new(() => Task.FromException(new InvalidOperationException("boom")), sink,
            "test-command");

        Func<Task> action = command.ExecuteAsync;
        await action.Should().ThrowAsync<InvalidOperationException>();
        reports.Should().Be(0);
    }

    [Fact]
    public async Task ICommand_execute_swallows_operation_canceled()
    {
        int reports = 0;
        RecordingUnexpectedExceptionSink sink = new((_, _, _) => reports++);
        AsyncCommand command = new(() => Task.FromCanceled(new CancellationToken(true)), sink, "cancel-command");

        command.Execute(null);
        await Task.Delay(50);

        reports.Should().Be(0);
    }

    [Fact]
    public async Task ICommand_execute_reports_operation_canceled_without_a_cancelled_token()
    {
        TaskCompletionSource<Exception> reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingUnexpectedExceptionSink sink = new((exception, _, _) => reported.TrySetResult(exception));
        AsyncCommand command = new(
            () => Task.FromException(new OperationCanceledException("unexpected")),
            sink,
            "unexpected-cancel-command");

        command.Execute(null);

        (await reported.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeOfType<OperationCanceledException>();
    }
}
