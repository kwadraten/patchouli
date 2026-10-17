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
    string ControlToken,
    long? ProcessStartTimeProcTicks = null,
    string? BootId = null);

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
                !PathEquals(record.DatabasePath, canonicalPath) ||
                !await IsHostAliveAsync(record, cancellationToken))
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
            // Preferred: compare raw /proc starttime (clock ticks) exactly.
            // This is the kernel's authoritative process identity, immune to
            // DateTime conversion precision issues and quantization boundaries.
            if (record.ProcessStartTimeProcTicks.HasValue &&
                TryReadProcStartTimeTicks(record.ProcessId, out long procTicks))
            {
                return procTicks == record.ProcessStartTimeProcTicks.Value;
            }

            // Fallback for records written before ProcessStartTimeProcTicks existed,
            // or on non-Linux platforms: normalize DateTime to /proc precision.
            using Process process = Process.GetProcessById(record.ProcessId);
            const long TicksPerProcClockTick = TimeSpan.TicksPerMillisecond * 10;

            static long NormalizeToProcPrecision(DateTime dt)
            {
                return dt.Ticks / TicksPerProcClockTick * TicksPerProcClockTick;
            }

            return NormalizeToProcPrecision(process.StartTime.ToUniversalTime()) ==
                   NormalizeToProcPrecision(record.ProcessStartedUtc);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                              System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies the host is alive, handling cross-namespace scenarios (e.g., Flatpak).
    /// First tries PID-based verification (fast, same-namespace). If the PID does not
    /// exist in this namespace (e.g., different `flatpak run` sandbox instance),
    /// falls back to HTTP health check with InstanceId verification via the shared
    /// network namespace (--share=network).
    /// Library identity (PathKey, DatabasePath) is verified by the caller before this.
    /// </summary>
    internal static async Task<bool> IsHostAliveAsync(
        RuntimeHostDiscoveryRecord record,
        CancellationToken cancellationToken = default)
    {
        // Boot identity: reject stale records from before a reboot.
        // /proc starttime ticks are boot-relative; without this, a rebooted
        // system could have matching PID+ticks for a different process.
        if (record.BootId is not null)
        {
            string? currentBootId = TryReadBootId();
            if (currentBootId is null ||
                !string.Equals(currentBootId, record.BootId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // Fast path: PID verification works within the same namespace
        if (IsCurrentProcess(record))
        {
            return true;
        }

        // Cross-namespace fallback: PID doesn't exist here, but the host may be
        // alive in another sandbox instance with shared network namespace.
        // Verify via /health endpoint with InstanceId + LibraryId binding.
        return await IsHostAliveViaHealthCheckAsync(record, cancellationToken);
    }

    /// <summary>
    /// Reads the system boot ID from /proc/sys/kernel/random/boot_id.
    /// This UUID is generated at each boot; differs across reboots.
    /// </summary>
    internal static string? TryReadBootId()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            string bootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
            return string.IsNullOrWhiteSpace(bootId) ? null : bootId;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks host liveness via HTTP /health endpoint with InstanceId + LibraryId binding.
    /// Used when PID verification fails due to namespace isolation (e.g., Flatpak).
    /// Security: only loopback targets; redirects and proxy disabled; rejects
    /// mismatched instanceId or libraryId; 5s timeout.
    /// </summary>
    internal static async Task<bool> IsHostAliveViaHealthCheckAsync(
        RuntimeHostDiscoveryRecord record,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Derive health URL from endpoint: http://host:port/mcp -> http://host:port/health
            if (!Uri.TryCreate(record.Endpoint, UriKind.Absolute, out Uri? endpointUri))
            {
                return false;
            }

            // Only allow http/https schemes.
            if (!endpointUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
                !endpointUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // SECURITY: only allow loopback targets. Reject any non-loopback endpoint
            // to prevent SSRF via a crafted discovery record.
            string host = endpointUri.Host;
            bool isLoopback = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                              host.Equals("127.0.0.1", StringComparison.Ordinal) ||
                              host.Equals("::1", StringComparison.Ordinal) ||
                              host.Equals("[::1]", StringComparison.Ordinal);
            if (!isLoopback)
            {
                return false;
            }

            string healthUrl = new UriBuilder(endpointUri) { Path = "/health" }.Uri.ToString();

            // SECURITY: disable redirects (prevent InstanceId leak via redirect)
            // and proxy (prevent interception). Direct loopback connection only.
            using SocketsHttpHandler handler = new()
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(5)
            };
            using HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
            using HttpResponseMessage response = await client.GetAsync(healthUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument doc = JsonDocument.Parse(json);

            // InstanceId binding: must match the record's instance.
            // Both expected and response must be non-empty; reject null==null.
            if (string.IsNullOrEmpty(record.InstanceId))
            {
                return false;
            }

            if (!doc.RootElement.TryGetProperty("instanceId", out JsonElement idElement))
            {
                return false;
            }

            string? responseInstanceId = idElement.GetString();
            if (string.IsNullOrEmpty(responseInstanceId))
            {
                return false;
            }

            if (!string.Equals(responseInstanceId, record.InstanceId, StringComparison.Ordinal))
            {
                return false;
            }

            // LibraryId binding: the responding host must serve the expected library.
            // Rejects a different host that happens to listen on the same port.
            // REQUIRED: no compatibility fallback. Hosts old enough to lack libraryId
            // also lack instanceId, so they cannot pass the instanceId check above.
            // Both expected and response must be non-empty; reject null==null.
            if (string.IsNullOrEmpty(record.LibraryId))
            {
                return false;
            }

            if (!doc.RootElement.TryGetProperty("libraryId", out JsonElement libElement))
            {
                return false;
            }

            string? responseLibraryId = libElement.GetString();
            if (string.IsNullOrEmpty(responseLibraryId))
            {
                return false;
            }

            if (!string.Equals(responseLibraryId, record.LibraryId, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            // Preserve caller cancellation: if the caller's token was canceled,
            // rethrow instead of swallowing as "host not alive". Timeout (from
            // HttpClient.Timeout) also throws TaskCanceledException but with
            // a different token; only rethrow for caller cancellation.
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    /// <summary>
    /// Reads the raw starttime field (field 22, in clock ticks) from /proc/[pid]/stat.
    /// This is the kernel's exact process start identifier, stable across processes
    /// within a boot. Returns false if unavailable (non-Linux, PID not found).
    /// </summary>
    internal static bool TryReadProcStartTimeTicks(int pid, out long ticks)
    {
        ticks = 0;
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            // comm (field 2) may contain spaces/parens; find the last ')' then split
            int commEnd = stat.LastIndexOf(')');
            if (commEnd < 0)
            {
                return false;
            }

            string[] fields = stat.Substring(commEnd + 2).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // fields[0] = state (field 3); starttime (field 22) = fields[19]
            if (fields.Length > 19 && long.TryParse(fields[19], out long result))
            {
                ticks = result;
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
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
        // Capture raw /proc starttime for stable cross-process identity verification
        ProcessStartTimeProcTicks =
            RuntimeHostCoordinator.TryReadProcStartTimeTicks(Environment.ProcessId, out long ticks) ? ticks : null;
        // Capture boot ID: /proc starttime ticks are boot-relative, so a stale
        // record from before a reboot could otherwise match PID+ticks.
        BootId = RuntimeHostCoordinator.TryReadBootId();
    }

    public string DatabasePath { get; }
    public string PathKey { get; }
    public RuntimeHostKind HostKind { get; }
    public string ControlPipe { get; }
    public string InstanceId { get; }
    public DateTime ProcessStartedUtc { get; }
    public long? ProcessStartTimeProcTicks { get; }
    public string? BootId { get; }

    internal void ValidateOwnership(string databasePath)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        string canonicalPath = RuntimeHostCoordinator.CanonicalizeDatabasePath(databasePath);
        if (!string.Equals(DatabasePath, canonicalPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The runtime-host lease belongs to a different database.");
        }
    }

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
            _controlToken,
            ProcessStartTimeProcTicks,
            BootId);

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
