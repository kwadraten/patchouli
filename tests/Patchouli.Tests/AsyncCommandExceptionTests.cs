using FluentAssertions;
using Patchouli.UI.Diagnostics;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

public sealed class AsyncCommandExceptionTests
{
    [Fact]
    public async Task ICommand_execute_reports_unexpected_exception()
    {
        var reported = new TaskCompletionSource<(Exception Exception, string Boundary, string? Operation)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingUnexpectedExceptionSink((exception, boundary, operation) => reported.TrySetResult((exception, boundary, operation)));
        var command = new AsyncCommand(() => Task.FromException(new InvalidOperationException("boom")), sink, "test-command");

        command.Execute(null);

        var result = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        result.Exception.Should().BeOfType<InvalidOperationException>();
        result.Boundary.Should().Be("ui-command");
        result.Operation.Should().Be("test-command");
    }

    [Fact]
    public async Task ExecuteAsync_preserves_exception_for_awaiting_caller()
    {
        var reports = 0;
        var sink = new RecordingUnexpectedExceptionSink((_, _, _) => reports++);
        var command = new AsyncCommand(() => Task.FromException(new InvalidOperationException("boom")), sink, "test-command");

        var action = command.ExecuteAsync;
        await action.Should().ThrowAsync<InvalidOperationException>();
        reports.Should().Be(0);
    }
}
