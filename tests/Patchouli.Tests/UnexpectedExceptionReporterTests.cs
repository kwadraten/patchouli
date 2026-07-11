using FluentAssertions;
using Patchouli.Core.Diagnostics;

namespace Patchouli.Tests;

public sealed class UnexpectedExceptionReporterTests : IDisposable
{
    public UnexpectedExceptionReporterTests() => UnexpectedExceptionReporter.Reset();

    public void Dispose() => UnexpectedExceptionReporter.Reset();

    [Fact]
    public void Configure_forwards_exception_context_and_reset_restores_no_op()
    {
        var reports = new List<(Exception Exception, string Boundary, string? Operation)>();
        UnexpectedExceptionReporter.Configure((exception, boundary, operation) =>
            reports.Add((exception, boundary, operation)));
        var exception = new InvalidOperationException("failure");

        UnexpectedExceptionReporter.Report(exception, "test-boundary", "test-operation");
        UnexpectedExceptionReporter.Reset();
        UnexpectedExceptionReporter.Report(new Exception("ignored"), "test-boundary");

        reports.Should().ContainSingle().Which.Should().Be((exception, "test-boundary", "test-operation"));
    }

    [Fact]
    public void Report_does_not_forward_cancellation_or_reporter_failures()
    {
        var calls = 0;
        UnexpectedExceptionReporter.Configure((_, _, _) =>
        {
            calls++;
            throw new InvalidOperationException("sink failure");
        });

        UnexpectedExceptionReporter.Report(new OperationCanceledException(), "test-boundary");
        var act = () => UnexpectedExceptionReporter.Report(new Exception("failure"), "test-boundary");

        act.Should().NotThrow();
        calls.Should().Be(1);
    }

    [Fact]
    public void ReportCatch_does_not_consume_cancellation()
    {
        UnexpectedExceptionReporter.Configure((_, _, _) => throw new InvalidOperationException("must not run"));

        UnexpectedExceptionReporter.ReportCatch(new OperationCanceledException(), "test-boundary").Should().BeFalse();
        UnexpectedExceptionReporter.ReportCatch(new InvalidOperationException(), "test-boundary").Should().BeTrue();
    }
}
