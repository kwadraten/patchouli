using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Patchouli.UI;

namespace Patchouli.Tests;

public sealed class DesktopInstanceCoordinatorTests
{
    [Fact]
    public async Task Same_name_yields_exactly_one_primary()
    {
        string mutexName = $"net.patchouli.test.mutex.{Guid.NewGuid():N}";
        DesktopInstanceCoordinator coordinator1 = new(new DesktopInstanceCoordinatorOptions(mutexName));
        DesktopInstanceCoordinator coordinator2 = new(new DesktopInstanceCoordinatorOptions(mutexName));

        try
        {
            coordinator1.IsPrimary.Should().BeTrue();
            coordinator2.IsPrimary.Should().BeFalse();
        }
        finally
        {
            await coordinator1.DisposeAsync();
            await coordinator2.DisposeAsync();
        }
    }

    [Fact]
    public async Task After_release_another_coordinator_can_become_primary()
    {
        string mutexName = $"net.patchouli.test.mutex.{Guid.NewGuid():N}";
        DesktopInstanceCoordinator coordinator1 = new(new DesktopInstanceCoordinatorOptions(mutexName));
        coordinator1.IsPrimary.Should().BeTrue();
        await coordinator1.DisposeAsync();

        DesktopInstanceCoordinator coordinator2 = new(new DesktopInstanceCoordinatorOptions(mutexName));
        try
        {
            coordinator2.IsPrimary.Should().BeTrue();
        }
        finally
        {
            await coordinator2.DisposeAsync();
        }
    }

    [Fact]
    public async Task Concurrent_creators_yield_exactly_one_primary()
    {
        string mutexName = $"net.patchouli.test.mutex.{Guid.NewGuid():N}";
        const int count = 10;
        Task<DesktopInstanceCoordinator>[] tasks = new Task<DesktopInstanceCoordinator>[count];

        for (int i = 0; i < count; i++)
        {
            tasks[i] = Task.Run(() => new DesktopInstanceCoordinator(new DesktopInstanceCoordinatorOptions(mutexName)));
        }

        DesktopInstanceCoordinator[] coordinators = await Task.WhenAll(tasks);
        try
        {
            coordinators.Count(c => c.IsPrimary).Should().Be(1);
            coordinators.Count(c => !c.IsPrimary).Should().Be(count - 1);
        }
        finally
        {
            foreach (DesktopInstanceCoordinator coordinator in coordinators)
            {
                await coordinator.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Secondary_activation_with_matching_ack_fires_primary_instance_exactly_once()
    {
        string mutexName = $"net.patchouli.test.mutex.{Guid.NewGuid():N}";
        string pipeName = $"net.patchouli.test.pipe.{Guid.NewGuid():N}";
        DesktopInstanceCoordinator primary = new(new DesktopInstanceCoordinatorOptions(mutexName, pipeName));
        primary.IsPrimary.Should().BeTrue();
        primary.StartListener();

        TaskCompletionSource<bool> activationTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int activationCount = 0;
        using IDisposable subscription = primary.Subscribe(() =>
        {
            Interlocked.Increment(ref activationCount);
            activationTcs.TrySetResult(true);
        });

        DesktopInstanceCoordinator secondary = new(new DesktopInstanceCoordinatorOptions(mutexName, pipeName));
        secondary.IsPrimary.Should().BeFalse();

        try
        {
            bool success = await secondary.NotifyPrimaryAsync();
            success.Should().BeTrue();

            using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(5));
            await activationTcs.Task.WaitAsync(timeoutCts.Token);
            activationCount.Should().Be(1);
        }
        finally
        {
            await secondary.DisposeAsync();
            await primary.DisposeAsync();
        }
    }

    [Fact]
    public async Task Pre_window_request_retained_and_consumed_exactly_once()
    {
        string mutexName = $"net.patchouli.test.mutex.{Guid.NewGuid():N}";
        string pipeName = $"net.patchouli.test.pipe.{Guid.NewGuid():N}";
        DesktopInstanceCoordinator primary = new(new DesktopInstanceCoordinatorOptions(mutexName, pipeName));
        primary.StartListener();

        DesktopInstanceCoordinator secondary = new(new DesktopInstanceCoordinatorOptions(mutexName, pipeName));

        try
        {
            bool success = await secondary.NotifyPrimaryAsync();
            success.Should().BeTrue();

            int activationCount = 0;
            using (primary.Subscribe(() => Interlocked.Increment(ref activationCount)))
            {
                activationCount.Should().Be(1);
            }

            primary.TryConsumePendingActivation().Should().BeFalse();
        }
        finally
        {
            await secondary.DisposeAsync();
            await primary.DisposeAsync();
        }
    }

    [Fact]
    public async Task Idle_listener_disposal_produces_zero_diagnostics()
    {
        string mutexName = $"net.patchouli.test.mutex.{Guid.NewGuid():N}";
        string pipeName = $"net.patchouli.test.pipe.{Guid.NewGuid():N}";
        List<string> diagnostics = new();

        DesktopInstanceCoordinator primary = new(new DesktopInstanceCoordinatorOptions(
            mutexName,
            pipeName,
            LogDiagnostic: (msg, ex) => diagnostics.Add($"{msg}: {ex?.Message}")));

        primary.StartListener();
        await primary.DisposeAsync();

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task Listener_initialization_failure_surfaced_synchronously_and_leaves_no_leaks()
    {
        string mutexName = $"net.patchouli.test.mutex.{Guid.NewGuid():N}";
        string pipeName = $"net.patchouli.test.pipe.{Guid.NewGuid():N}";

        using NamedPipeServerStream occupyingServer = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        DesktopInstanceCoordinator primary = new(new DesktopInstanceCoordinatorOptions(mutexName, pipeName));
        try
        {
            Action act = () => primary.StartListener();
            act.Should().Throw<IOException>();
        }
        finally
        {
            await primary.DisposeAsync();
        }
    }

    [Fact]
    public async Task Listener_factory_failure_surfaced_synchronously()
    {
        string mutexName = $"net.patchouli.test.mutex.{Guid.NewGuid():N}";
        string pipeName = $"net.patchouli.test.pipe.{Guid.NewGuid():N}";

        DesktopInstanceCoordinator primary = new(new DesktopInstanceCoordinatorOptions(
            mutexName,
            pipeName,
            ServerStreamFactory: _ => throw new InvalidOperationException("Injected server stream failure.")));

        try
        {
            Action act = () => primary.StartListener();
            act.Should().Throw<InvalidOperationException>().WithMessage("*Injected server stream failure*");
        }
        finally
        {
            await primary.DisposeAsync();
        }
    }

    [Fact]
    public async Task Listener_factory_returning_null_throws_synchronously()
    {
        string mutexName = $"net.patchouli.test.mutex.{Guid.NewGuid():N}";
        string pipeName = $"net.patchouli.test.pipe.{Guid.NewGuid():N}";

        DesktopInstanceCoordinator primary = new(new DesktopInstanceCoordinatorOptions(
            mutexName,
            pipeName,
            ServerStreamFactory: _ => null!));

        try
        {
            Action act = () => primary.StartListener();
            act.Should().Throw<InvalidOperationException>().WithMessage("*null*");
        }
        finally
        {
            await primary.DisposeAsync();
        }
    }

    [Fact]
    public async Task Secondary_notification_with_no_listener_fails_gracefully_within_timeout()
    {
        string pipeName = $"net.patchouli.test.pipe.none.{Guid.NewGuid():N}";
        DesktopInstanceCoordinator secondary = new(new DesktopInstanceCoordinatorOptions(
            PipeName: pipeName,
            SecondaryRetryTimeout: TimeSpan.FromMilliseconds(150)));

        try
        {
            bool success = await secondary.NotifyPrimaryAsync();
            success.Should().BeFalse();
        }
        finally
        {
            await secondary.DisposeAsync();
        }
    }

    [Fact]
    public async Task Secondary_notification_cancellation_handled_cleanly()
    {
        string pipeName = $"net.patchouli.test.pipe.none.{Guid.NewGuid():N}";
        DesktopInstanceCoordinator secondary = new(new DesktopInstanceCoordinatorOptions(
            PipeName: pipeName,
            SecondaryRetryTimeout: TimeSpan.FromSeconds(5)));

        using CancellationTokenSource cts = new();
        cts.Cancel();

        try
        {
            bool success = await secondary.NotifyPrimaryAsync(cts.Token);
            success.Should().BeFalse();
        }
        finally
        {
            await secondary.DisposeAsync();
        }
    }

    [Fact]
    public async Task Secondary_notification_midflight_cancellation_returns_false()
    {
        string pipeName = $"net.patchouli.test.pipe.none.{Guid.NewGuid():N}";
        DesktopInstanceCoordinator secondary = new(new DesktopInstanceCoordinatorOptions(
            PipeName: pipeName,
            SecondaryRetryTimeout: TimeSpan.FromSeconds(5)));

        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromMilliseconds(75));

        try
        {
            bool success = await secondary.NotifyPrimaryAsync(cts.Token);
            success.Should().BeFalse();
        }
        finally
        {
            await secondary.DisposeAsync();
        }
    }

    [Fact]
    public async Task Control_listener_rejects_invalid_protocol_version()
    {
        string mutexName = $"net.patchouli.test.mutex.{Guid.NewGuid():N}";
        string pipeName = $"net.patchouli.test.pipe.{Guid.NewGuid():N}";
        DesktopInstanceCoordinator primary = new(new DesktopInstanceCoordinatorOptions(mutexName, pipeName));
        primary.StartListener();

        int activationCount = 0;
        using IDisposable subscription = primary.Subscribe(() => Interlocked.Increment(ref activationCount));

        try
        {
            string payloadJson = JsonSerializer.Serialize(new
            {
                version = 2,
                command = "activate_ui",
                request_id = Guid.NewGuid().ToString("D")
            });
            byte[] payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
            byte[] lengthHeader = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lengthHeader, payloadBytes.Length);

            await SendRawPacketAsync(pipeName, lengthHeader, payloadBytes);

            activationCount.Should().Be(0);
        }
        finally
        {
            await primary.DisposeAsync();
        }
    }

    [Fact]
    public async Task Control_listener_rejects_unknown_command()
    {
        string mutexName = $"net.patchouli.test.mutex.{Guid.NewGuid():N}";
        string pipeName = $"net.patchouli.test.pipe.{Guid.NewGuid():N}";
        DesktopInstanceCoordinator primary = new(new DesktopInstanceCoordinatorOptions(mutexName, pipeName));
        primary.StartListener();

        int activationCount = 0;
        using IDisposable subscription = primary.Subscribe(() => Interlocked.Increment(ref activationCount));

        try
        {
            string payloadJson = JsonSerializer.Serialize(new
            {
                version = 1,
                command = "takeover",
                request_id = Guid.NewGuid().ToString("D")
            });
            byte[] payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
            byte[] lengthHeader = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lengthHeader, payloadBytes.Length);

            await SendRawPacketAsync(pipeName, lengthHeader, payloadBytes);

            activationCount.Should().Be(0);
        }
        finally
        {
            await primary.DisposeAsync();
        }
    }

    [Fact]
    public async Task Control_listener_rejects_extra_unmapped_fields()
    {
        string mutexName = $"net.patchouli.test.mutex.{Guid.NewGuid():N}";
        string pipeName = $"net.patchouli.test.pipe.{Guid.NewGuid():N}";
        DesktopInstanceCoordinator primary = new(new DesktopInstanceCoordinatorOptions(mutexName, pipeName));
        primary.StartListener();

        int activationCount = 0;
        using IDisposable subscription = primary.Subscribe(() => Interlocked.Increment(ref activationCount));

        try
        {
            string payloadJson = JsonSerializer.Serialize(new
            {
                version = 1,
                command = "activate_ui",
                request_id = Guid.NewGuid().ToString("D"),
                path = "/dangerous/path",
                shutdown = true
            });
            byte[] payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
            byte[] lengthHeader = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lengthHeader, payloadBytes.Length);

            await SendRawPacketAsync(pipeName, lengthHeader, payloadBytes);

            activationCount.Should().Be(0);
        }
        finally
        {
            await primary.DisposeAsync();
        }
    }

    [Fact]
    public async Task Control_listener_rejects_invalid_request_id()
    {
        string mutexName = $"net.patchouli.test.mutex.{Guid.NewGuid():N}";
        string pipeName = $"net.patchouli.test.pipe.{Guid.NewGuid():N}";
        DesktopInstanceCoordinator primary = new(new DesktopInstanceCoordinatorOptions(mutexName, pipeName));
        primary.StartListener();

        int activationCount = 0;
        using IDisposable subscription = primary.Subscribe(() => Interlocked.Increment(ref activationCount));

        try
        {
            string[] invalidIds =
                ["", "   ", "not-a-guid", "12345\n6789", "<script>", "NONGUID0000000000000000000000000000"];
            foreach (string invalidId in invalidIds)
            {
                string payloadJson = JsonSerializer.Serialize(new
                {
                    version = 1,
                    command = "activate_ui",
                    request_id = invalidId
                });
                byte[] payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
                byte[] lengthHeader = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(lengthHeader, payloadBytes.Length);

                await SendRawPacketAsync(pipeName, lengthHeader, payloadBytes);
            }

            activationCount.Should().Be(0);
        }
        finally
        {
            await primary.DisposeAsync();
        }
    }

    [Fact]
    public async Task Control_listener_rejects_oversized_payload()
    {
        string mutexName = $"net.patchouli.test.mutex.{Guid.NewGuid():N}";
        string pipeName = $"net.patchouli.test.pipe.{Guid.NewGuid():N}";
        DesktopInstanceCoordinator primary = new(new DesktopInstanceCoordinatorOptions(mutexName, pipeName));
        primary.StartListener();

        int activationCount = 0;
        using IDisposable subscription = primary.Subscribe(() => Interlocked.Increment(ref activationCount));

        try
        {
            byte[] lengthHeader = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lengthHeader, 1025);
            byte[] payloadBytes = new byte[1025];

            await SendRawPacketAsync(pipeName, lengthHeader, payloadBytes);

            activationCount.Should().Be(0);
        }
        finally
        {
            await primary.DisposeAsync();
        }
    }

    [Fact]
    public async Task Control_listener_rejects_invalid_length_prefix()
    {
        string mutexName = $"net.patchouli.test.mutex.{Guid.NewGuid():N}";
        string pipeName = $"net.patchouli.test.pipe.{Guid.NewGuid():N}";
        DesktopInstanceCoordinator primary = new(new DesktopInstanceCoordinatorOptions(mutexName, pipeName));
        primary.StartListener();

        int activationCount = 0;
        using IDisposable subscription = primary.Subscribe(() => Interlocked.Increment(ref activationCount));

        try
        {
            byte[] lengthHeader = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lengthHeader, -1);

            await SendRawPacketAsync(pipeName, lengthHeader, Array.Empty<byte>());

            activationCount.Should().Be(0);
        }
        finally
        {
            await primary.DisposeAsync();
        }
    }

    [Fact]
    public async Task Control_listener_rejects_malformed_json()
    {
        string mutexName = $"net.patchouli.test.mutex.{Guid.NewGuid():N}";
        string pipeName = $"net.patchouli.test.pipe.{Guid.NewGuid():N}";
        DesktopInstanceCoordinator primary = new(new DesktopInstanceCoordinatorOptions(mutexName, pipeName));
        primary.StartListener();

        int activationCount = 0;
        using IDisposable subscription = primary.Subscribe(() => Interlocked.Increment(ref activationCount));

        try
        {
            byte[] payloadBytes = Encoding.UTF8.GetBytes("{ not valid json }");
            byte[] lengthHeader = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lengthHeader, payloadBytes.Length);

            await SendRawPacketAsync(pipeName, lengthHeader, payloadBytes);

            activationCount.Should().Be(0);
        }
        finally
        {
            await primary.DisposeAsync();
        }
    }

    [Fact]
    public async Task Control_listener_client_disconnect_before_sending_handled_cleanly()
    {
        string mutexName = $"net.patchouli.test.mutex.{Guid.NewGuid():N}";
        string pipeName = $"net.patchouli.test.pipe.{Guid.NewGuid():N}";
        DesktopInstanceCoordinator primary = new(new DesktopInstanceCoordinatorOptions(mutexName, pipeName));
        primary.StartListener();

        int activationCount = 0;
        using IDisposable subscription = primary.Subscribe(() => Interlocked.Increment(ref activationCount));

        try
        {
            using (NamedPipeClientStream client = new(".", pipeName, PipeDirection.InOut,
                       PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await client.ConnectAsync(1000);
            }

            await Task.Delay(50);

            DesktopInstanceCoordinator secondary = new(new DesktopInstanceCoordinatorOptions(mutexName, pipeName));
            try
            {
                bool success = await secondary.NotifyPrimaryAsync();
                success.Should().BeTrue();
                activationCount.Should().Be(1);
            }
            finally
            {
                await secondary.DisposeAsync();
            }
        }
        finally
        {
            await primary.DisposeAsync();
        }
    }

    [Fact]
    public async Task Secondary_retries_and_succeeds_when_primary_listener_starts_slightly_later()
    {
        string mutexName = $"net.patchouli.test.mutex.{Guid.NewGuid():N}";
        string pipeName = $"net.patchouli.test.pipe.{Guid.NewGuid():N}";
        DesktopInstanceCoordinator primary = new(new DesktopInstanceCoordinatorOptions(mutexName, pipeName));
        primary.IsPrimary.Should().BeTrue();

        int activationCount = 0;
        using IDisposable subscription = primary.Subscribe(() => Interlocked.Increment(ref activationCount));

        DesktopInstanceCoordinator secondary = new(new DesktopInstanceCoordinatorOptions(
            mutexName,
            pipeName,
            TimeSpan.FromSeconds(2)));

        try
        {
            Task<bool> notifyTask = Task.Run(() => secondary.NotifyPrimaryAsync());
            await Task.Delay(100);

            primary.StartListener();

            bool success = await notifyTask;
            success.Should().BeTrue();
            activationCount.Should().Be(1);
        }
        finally
        {
            await secondary.DisposeAsync();
            await primary.DisposeAsync();
        }
    }

    [Fact]
    public async Task Secondary_fails_closed_when_server_returns_mismatched_or_invalid_ack()
    {
        string pipeName = $"net.patchouli.test.pipe.fake.{Guid.NewGuid():N}";

        Task serverTask = Task.Run(async () =>
        {
            using NamedPipeServerStream server = new(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            await server.WaitForConnectionAsync();
            byte[] header = new byte[4];
            await server.ReadExactlyAsync(header, 0, 4);
            int len = BinaryPrimitives.ReadInt32LittleEndian(header);
            byte[] req = new byte[len];
            await server.ReadExactlyAsync(req, 0, len);

            byte[] respPayload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                version = 1,
                request_id = Guid.NewGuid().ToString("D"),
                ok = true
            }));
            byte[] respHeader = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(respHeader, respPayload.Length);
            await server.WriteAsync(respHeader);
            await server.WriteAsync(respPayload);
            await server.FlushAsync();
        });

        DesktopInstanceCoordinator secondary = new(new DesktopInstanceCoordinatorOptions(
            PipeName: pipeName,
            SecondaryRetryTimeout: TimeSpan.FromMilliseconds(500)));

        try
        {
            bool success = await secondary.NotifyPrimaryAsync();
            success.Should().BeFalse();
            await serverTask;
        }
        finally
        {
            await secondary.DisposeAsync();
        }
    }

    [Fact]
    public async Task Disposal_cancels_listener_and_releases_handles_without_leak()
    {
        string mutexName = $"net.patchouli.test.mutex.{Guid.NewGuid():N}";
        string pipeName = $"net.patchouli.test.pipe.{Guid.NewGuid():N}";

        DesktopInstanceCoordinator primary1 = new(new DesktopInstanceCoordinatorOptions(mutexName, pipeName));
        primary1.IsPrimary.Should().BeTrue();
        primary1.StartListener();
        await primary1.DisposeAsync();

        DesktopInstanceCoordinator primary2 = new(new DesktopInstanceCoordinatorOptions(mutexName, pipeName));
        try
        {
            primary2.IsPrimary.Should().BeTrue();
            primary2.StartListener();
        }
        finally
        {
            await primary2.DisposeAsync();
        }
    }

    private static async Task SendRawPacketAsync(string pipeName, byte[] lengthPrefix, byte[] payload)
    {
        try
        {
            using NamedPipeClientStream client = new(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(1000);
            await client.WriteAsync(lengthPrefix);
            if (payload.Length > 0)
            {
                await client.WriteAsync(payload);
            }

            await client.FlushAsync();
        }
        catch (IOException)
        {
            // Server disconnected immediately due to rejection
        }
    }
}
