using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Patchouli.Host.Lifecycle;

public enum RuntimeHostKind
{
    Desktop,
    Headless
}

public sealed record RuntimeHostDiscoveryRecord(
    int Version,
    string PathKey,
    string DatabasePath,
    string LibraryId,
    RuntimeHostKind HostKind,
    int ProcessId,
    DateTime ProcessStartedUtc,
    string InstanceId,
    string Endpoint,
    string ProtocolVersion,
    string ControlPipe,
    string ControlToken);

/// <summary>
/// Coordinates the one-runtime-host-per-database invariant before any SQLite connection is opened.
/// The lifetime lock and discovery record live in device-local application data, never beside a
/// database that may itself be synchronized or read-only.
/// </summary>
public static class RuntimeHostCoordinator
{
    public const int DiscoveryVersion = 1;
    public const string ProtocolVersion = "2025-06-18";
    private const int MaximumControlMessageBytes = 1024;
    internal static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static string GetDefaultDirectory()
    {
        return Path.Combine(new UI.PlatformAppPaths().Resolve().DataDirectory, "runtime-hosts");
    }

    public static int ReserveEphemeralLoopbackPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public static RuntimeHostLease? TryAcquire(
        string databasePath,
        RuntimeHostKind hostKind,
        string? directory = null)
    {
        string canonicalPath = CanonicalizeDatabasePath(databasePath);
        string pathKey = ComputePathKey(canonicalPath);
        string hostDirectory = Path.GetFullPath(directory ?? GetDefaultDirectory());
        Directory.CreateDirectory(hostDirectory);
        TryRestrictDirectory(hostDirectory);

        string lockPath = Path.Combine(hostDirectory, pathKey + ".lock");
        FileStream lockStream;
        try
        {
            lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                1, FileOptions.WriteThrough);
        }
        catch (IOException)
        {
            return null;
        }

        return new RuntimeHostLease(
            lockStream,
            Path.Combine(hostDirectory, pathKey + ".json"),
            canonicalPath,
            pathKey,
            hostKind,
            $"patchouli-runtime-{pathKey[..32]}");
    }

    public static async Task<RuntimeHostLease> AcquireDesktopAsync(
        string databasePath,
        TimeSpan timeout,
        string? directory = null,
        CancellationToken cancellationToken = default)
    {
        Stopwatch wait = Stopwatch.StartNew();
        bool takeoverAccepted = false;
        while (wait.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RuntimeHostLease? lease = TryAcquire(databasePath, RuntimeHostKind.Desktop, directory);
            if (lease is not null)
            {
                return lease;
            }

            if (!takeoverAccepted)
            {
                RuntimeHostDiscoveryRecord? current = await ReadAsync(databasePath, directory, cancellationToken);
                if (current?.HostKind == RuntimeHostKind.Desktop)
                {
                    throw new RuntimeHostUnavailableException(
                        "The selected Library is already owned by another desktop runtime host.");
                }

                if (current?.HostKind == RuntimeHostKind.Headless)
                {
                    takeoverAccepted = await RequestTakeoverAsync(current, cancellationToken);
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }

        throw new RuntimeHostUnavailableException(
            takeoverAccepted
                ? "The headless runtime did not release the Library in time."
                : "The selected Library is already owned by an unavailable runtime host.");
    }

    public static async Task<RuntimeHostDiscoveryRecord?> ReadAsync(
        string databasePath,
        string? directory = null,
        CancellationToken cancellationToken = default)
    {
        string canonicalPath = CanonicalizeDatabasePath(databasePath);
        string pathKey = ComputePathKey(canonicalPath);
        string recordPath = Path.Combine(Path.GetFullPath(directory ?? GetDefaultDirectory()), pathKey + ".json");
        try
        {
            await using FileStream stream = new(recordPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            RuntimeHostDiscoveryRecord? record = await JsonSerializer.DeserializeAsync<RuntimeHostDiscoveryRecord>(
                stream, JsonOptions, cancellationToken);
            if (record is null || record.Version != DiscoveryVersion ||
                !string.Equals(record.ProtocolVersion, ProtocolVersion, StringComparison.Ordinal) ||
                record.PathKey != pathKey ||
                !PathEquals(record.DatabasePath, canonicalPath) || !IsCurrentProcess(record))
            {
                return null;
            }

            return record;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static string CanonicalizeDatabasePath(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        return UI.AppPathGuard.ResolveRealPath(databasePath);
    }

    internal static string ComputePathKey(string canonicalPath)
    {
        string identity = OperatingSystem.IsWindows() ? canonicalPath.ToUpperInvariant() : canonicalPath;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    internal static bool IsCurrentProcess(RuntimeHostDiscoveryRecord record)
    {
        try
        {
            using Process process = Process.GetProcessById(record.ProcessId);
            return process.StartTime.ToUniversalTime() == record.ProcessStartedUtc;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                              System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static bool PathEquals(string left, string right)
    {
        return string.Equals(left, right,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static async Task<bool> RequestTakeoverAsync(
        RuntimeHostDiscoveryRecord record,
        CancellationToken cancellationToken)
    {
        try
        {
            await using NamedPipeClientStream pipe = new(".", record.ControlPipe, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(1000, cancellationToken);
            RuntimeHostControlMessage request = new("takeover", record.InstanceId, record.ControlToken);
            await WriteControlMessageAsync(pipe, request, cancellationToken);
            RuntimeHostControlReply? reply = await ReadControlMessageAsync<RuntimeHostControlReply>(pipe,
                cancellationToken);
            return reply is { Accepted: true, InstanceId: var instanceId } && instanceId == record.InstanceId;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or
                                              InvalidDataException or JsonException)
        {
            return false;
        }
    }

    internal static async Task WriteControlMessageAsync<T>(Stream stream, T value,
        CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (payload.Length > MaximumControlMessageBytes)
        {
            throw new InvalidDataException("Runtime-host control message is too large.");
        }

        byte[] length = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(length, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    internal static async Task<T?> ReadControlMessageAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        byte[] lengthBytes = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(lengthBytes, cancellationToken);
        int length = BitConverter.ToInt32(lengthBytes);
        if (length is <= 0 or > MaximumControlMessageBytes)
        {
            throw new InvalidDataException("Invalid runtime-host control message length.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload, JsonOptions);
    }

    private static void TryRestrictDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              PlatformNotSupportedException)
        {
            // Named pipes are additionally current-user-only; inability to tighten an existing directory
            // must not cause a second host to bypass the lifetime lock.
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    internal sealed record RuntimeHostControlMessage(string Command, string InstanceId, string Token);

    internal sealed record RuntimeHostControlReply(bool Accepted, string InstanceId);
}

public sealed class RuntimeHostLease : IAsyncDisposable
{
    private readonly FileStream _lockStream;
    private readonly string _recordPath;
    private readonly string _controlToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private int _disposed;

    internal RuntimeHostLease(
        FileStream lockStream,
        string recordPath,
        string databasePath,
        string pathKey,
        RuntimeHostKind hostKind,
        string controlPipe)
    {
        _lockStream = lockStream;
        _recordPath = recordPath;
        DatabasePath = databasePath;
        PathKey = pathKey;
        HostKind = hostKind;
        ControlPipe = controlPipe;
        InstanceId = Guid.NewGuid().ToString("N");
        ProcessStartedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();
    }

    public string DatabasePath { get; }
    public string PathKey { get; }
    public RuntimeHostKind HostKind { get; }
    public string ControlPipe { get; }
    public string InstanceId { get; }
    public DateTime ProcessStartedUtc { get; }

    public async Task<RuntimeHostDiscoveryRecord> PublishAsync(
        string libraryId,
        string endpoint,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        RuntimeHostDiscoveryRecord record = new(
            RuntimeHostCoordinator.DiscoveryVersion,
            PathKey,
            DatabasePath,
            libraryId,
            HostKind,
            Environment.ProcessId,
            ProcessStartedUtc,
            InstanceId,
            endpoint,
            RuntimeHostCoordinator.ProtocolVersion,
            ControlPipe,
            _controlToken);

        string temporaryPath = _recordPath + "." + InstanceId + ".tmp";
        await using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                         4096, FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, record, RuntimeHostCoordinator.JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        TryRestrictFile(temporaryPath);
        File.Move(temporaryPath, _recordPath, true);
        return record;
    }

    public async Task WaitForTakeoverRequestAsync(CancellationToken cancellationToken = default)
    {
        if (HostKind != RuntimeHostKind.Headless)
        {
            throw new InvalidOperationException("Only a headless runtime accepts desktop takeover requests.");
        }

        while (true)
        {
            await using NamedPipeServerStream pipe = new(ControlPipe, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(cancellationToken);
            try
            {
                RuntimeHostCoordinator.RuntimeHostControlMessage? request =
                    await RuntimeHostCoordinator
                        .ReadControlMessageAsync<RuntimeHostCoordinator.RuntimeHostControlMessage>(
                            pipe, cancellationToken);
                bool accepted = request is { Command: "takeover" } && request.InstanceId == InstanceId &&
                                CryptographicOperations.FixedTimeEquals(
                                    Encoding.UTF8.GetBytes(request.Token), Encoding.UTF8.GetBytes(_controlToken));
                await RuntimeHostCoordinator.WriteControlMessageAsync(
                    pipe, new RuntimeHostCoordinator.RuntimeHostControlReply(accepted, InstanceId), cancellationToken);
                if (accepted)
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
            {
                // Reject malformed local lifecycle messages and continue serving the authenticated command.
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            RuntimeHostDiscoveryRecord? current = ReadRecord();
            if (current?.InstanceId == InstanceId)
            {
                File.Delete(_recordPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A stale record is ignored by PID/start-time validation and cannot grant ownership.
        }

        _lockStream.Dispose();
        return ValueTask.CompletedTask;
    }

    private RuntimeHostDiscoveryRecord? ReadRecord()
    {
        using FileStream stream = new(_recordPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<RuntimeHostDiscoveryRecord>(stream, RuntimeHostCoordinator.JsonOptions);
    }

    private static void TryRestrictFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              PlatformNotSupportedException)
        {
            // The containing directory was already restricted when possible.
        }
    }
}

public sealed class RuntimeHostUnavailableException(string message) : Exception(message);
