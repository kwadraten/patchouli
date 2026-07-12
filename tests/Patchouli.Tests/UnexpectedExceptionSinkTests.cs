using FluentAssertions;
using Patchouli.UI.Diagnostics;

namespace Patchouli.Tests;

public sealed class UnexpectedExceptionSinkTests
{
    [Fact]
    public void Bootstrap_sink_does_not_create_platform_storage()
    {
        UnexpectedExceptions.Sink.Should().BeOfType<RecordingUnexpectedExceptionSink>();
    }

    [Fact]
    public void File_sink_writes_context_stack_and_redacts_secrets()
    {
        string root = Path.Combine(Path.GetTempPath(), $"patchouli-crash-{Guid.NewGuid():N}");
        try
        {
            FileUnexpectedExceptionSink sink = new(root);
            ThrowAndReport(sink);

            string log = File.ReadAllText(Path.Combine(root, "patchouli-crash.log"));
            log.Should().Contain("Boundary: test-boundary")
                .And.Contain("Operation: test-operation")
                .And.Contain(nameof(InvalidOperationException))
                .And.Contain(nameof(ThrowAndReport))
                .And.Contain("[redacted]")
                .And.NotContain("secret-token")
                .And.NotContain("bearer-value");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void File_sink_serializes_concurrent_records()
    {
        string root = Path.Combine(Path.GetTempPath(), $"patchouli-crash-{Guid.NewGuid():N}");
        try
        {
            FileUnexpectedExceptionSink sink = new(root);
            Parallel.For(0, 20, i => sink.Report(new Exception($"failure-{i}"), "parallel"));

            string log = File.ReadAllText(Path.Combine(root, "patchouli-crash.log"));
            Enumerable.Range(0, 20).Should().OnlyContain(i => log.Contains($"failure-{i}", StringComparison.Ordinal));
            log.Split("ErrorId:", StringSplitOptions.None).Length.Should().Be(21);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void ThrowAndReport(IUnexpectedExceptionSink sink)
    {
        try
        {
            throw new InvalidOperationException("token=secret-token Authorization: Bearer bearer-value");
        }
        catch (Exception exception)
        {
            sink.Report(exception, "test-boundary", "test-operation");
        }
    }
}
