using System.Text.Json;
using FluentAssertions;
using Patchouli.Infrastructure.Shell;

namespace Patchouli.Tests;

public sealed class ShellRpcFramingTests
{
    [Fact]
    public async Task Frame_roundtrip_preserves_envelope()
    {
        await using MemoryStream stream = new();
        ShellRpcEnvelope original = new()
        {
            ProtocolVersion = ShellRpcProtocol.Version,
            MessageType = "request",
            RequestId = 1,
            ExecutionId = 42,
            Method = "vfs.resolve",
            Payload = JsonSerializer.SerializeToElement(new { path = "/" }, ShellRpcFraming.JsonOptions)
        };

        await ShellRpcFraming.WriteFrameAsync(stream, original, 8 * 1024 * 1024, CancellationToken.None);
        stream.Position = 0;
        ShellRpcEnvelope? read = await ShellRpcFraming.ReadFrameAsync(stream, 8 * 1024 * 1024, CancellationToken.None);

        read.Should().NotBeNull();
        read!.ProtocolVersion.Should().Be(ShellRpcProtocol.Version);
        read.MessageType.Should().Be("request");
        read.RequestId.Should().Be(1);
        read.ExecutionId.Should().Be(42);
        read.Method.Should().Be("vfs.resolve");
        read.Payload.Should().NotBeNull();
        read.Payload!.Value.GetProperty("path").GetString().Should().Be("/");
    }

    [Fact]
    public async Task ReadFrame_returns_null_on_empty_stream()
    {
        await using MemoryStream stream = new();
        ShellRpcEnvelope? read = await ShellRpcFraming.ReadFrameAsync(stream, 1024, CancellationToken.None);
        read.Should().BeNull();
    }

    [Fact]
    public async Task WriteFrame_rejects_oversized_payload()
    {
        ShellRpcEnvelope envelope = new()
        {
            ProtocolVersion = ShellRpcProtocol.Version,
            MessageType = "notification",
            Method = "hello",
            Payload = JsonSerializer.SerializeToElement(new { blob = new string('x', 100) },
                ShellRpcFraming.JsonOptions)
        };

        Func<Task> act = async () =>
        {
            await using MemoryStream stream = new();
            await ShellRpcFraming.WriteFrameAsync(stream, envelope, 16, CancellationToken.None);
        };
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
