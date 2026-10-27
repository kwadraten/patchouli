using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Mcp;
using Patchouli.Core.Operations;
using Patchouli.Core.Results;
using Patchouli.Host.Mcp;
using Patchouli.McpServer;

namespace Patchouli.Tests;

/// <summary>
/// Fake-driven characterization of the injected-dependency <see cref="McpServerHost"/>
/// surface: start/stop, validation and error paths without HostServices, SQLite or real
/// sockets. Socket-level integration coverage stays in <see cref="McpServerHostTests"/>.
/// </summary>
public sealed class McpHostRefactorTests
{
    [Fact]
    public async Task Initial_status_projection_is_stopped_with_the_default_endpoint()
    {
        (McpServerHost host, _, _, _, _) = CreateHost();
        await using (host)
        {
            host.Status.Should().Be(McpServerHostStatus.Stopped);
            host.StatusDetail.Should().Be("MCP HTTP 服务未启动。");
            host.Endpoint.Should().Be($"http://localhost:{McpHttpServer.DefaultPort}/mcp");
            host.IsRunning.Should().BeFalse();
            host.Server.Should().BeNull();
            host.RunningSettings.Should().BeNull();
            host.RunningSettingsRevision.Should().BeNull();
        }
    }

    [Fact]
    public async Task Explicit_start_normalizes_settings_validates_and_runs_the_listener()
    {
        (McpServerHost host, FakeMcpServerSettingsService settings, FakeBlockingOperationService operations,
            FakeMcpServerInstanceFactory factory, _) = CreateHost();
        FakeMcpServerInstance listener = new("http://localhost:49321/mcp");
        factory.Enqueue(listener);
        List<(McpServerHostStatus Status, string Detail)> changes = new();
        host.StatusChanged += (_, change) => changes.Add((change.Status, change.Detail));

        await host.StartAsync(ValidSettings(11));

        host.Status.Should().Be(McpServerHostStatus.Running);
        host.IsRunning.Should().BeTrue();
        host.Endpoint.Should().Be("http://localhost:49321/mcp");
        host.RunningSettingsRevision.Should().Be(11);
        listener.StartCalls.Should().Be(1);
        operations.StartCalls.Should().Be(0,
            "the explicit-settings path never opens a blocking-operation record");
        settings.ValidatedSettings.Should().ContainSingle();
        McpServerSettings validated = settings.ValidatedSettings[0];
        validated.ToolOverrides.Should().Contain(item => item.ToolName == "patchouli.add" && !item.Enabled,
            "explicit settings pass through legacy normalization before validation");
        factory.SettingsSeen.Should().ContainSingle().Which.Should().BeSameAs(validated,
            "the listener is built from the normalized and validated settings");
        host.RunningSettings.Should().BeSameAs(validated);
        changes.Should().ContainInOrder(
            (McpServerHostStatus.Stopped, "MCP HTTP 服务正在启动。"),
            (McpServerHostStatus.Starting, "正在监听 http://127.0.0.1:4537/mcp"),
            (McpServerHostStatus.Running, "连接数: 0 / 0"));
    }

    [Fact]
    public async Task Explicit_start_validation_failure_enters_error_without_progress_or_listener()
    {
        (McpServerHost host, FakeMcpServerSettingsService settings, FakeBlockingOperationService operations,
            FakeMcpServerInstanceFactory factory, _) = CreateHost();
        settings.NextValidation = Result.Failure(AppErrorCodes.ValidationFailed, "bad mcp settings");

        await host.StartAsync(ValidSettings());

        host.Status.Should().Be(McpServerHostStatus.Error);
        host.StatusDetail.Should().Be("bad mcp settings");
        host.IsRunning.Should().BeFalse();
        host.Server.Should().BeNull();
        factory.SettingsSeen.Should().BeEmpty();
        operations.StartCalls.Should().Be(0);
        operations.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task Persisted_start_records_progress_and_completes_it_when_the_listener_runs()
    {
        (McpServerHost host, FakeMcpServerSettingsService settings, FakeBlockingOperationService operations,
            FakeMcpServerInstanceFactory factory, _) = CreateHost();
        settings.NextGetSettings = Result<McpServerSettings>.Success(ValidSettings(11));

        await host.StartAsync();

        operations.Started.Should().ContainSingle().Which.Should().Be(
            (BlockingOperationTypes.McpStartValidation, BlockingOperationScopeTypes.McpServerSettings, "default"));
        operations.Completions.Should().ContainSingle();
        operations.Failures.Should().BeEmpty();
        factory.SettingsSeen.Should().ContainSingle();
        host.Status.Should().Be(McpServerHostStatus.Running);
        host.RunningSettingsRevision.Should().Be(11);
    }

    [Fact]
    public async Task Persisted_start_load_failure_fails_the_progress_record_and_enters_error()
    {
        (McpServerHost host, FakeMcpServerSettingsService settings, FakeBlockingOperationService operations,
            FakeMcpServerInstanceFactory factory, _) = CreateHost();
        settings.NextGetSettings = Result<McpServerSettings>.Failure(
            AppErrorCodes.DatabaseError, "cannot read mcp settings");

        await host.StartAsync();

        host.Status.Should().Be(McpServerHostStatus.Error);
        host.StatusDetail.Should().Be("cannot read mcp settings");
        operations.Failures.Should().ContainSingle().Which.FailureCode.Should().Be(AppErrorCodes.DatabaseError);
        operations.Failures[0].FailureMessage.Should().Be("cannot read mcp settings");
        operations.Completions.Should().BeEmpty();
        factory.SettingsSeen.Should().BeEmpty();
    }

    [Fact]
    public async Task Persisted_start_validation_failure_fails_the_progress_record_and_enters_error()
    {
        (McpServerHost host, FakeMcpServerSettingsService settings, FakeBlockingOperationService operations,
            FakeMcpServerInstanceFactory factory, _) = CreateHost();
        settings.NextValidation = Result.Failure(AppErrorCodes.ValidationFailed, "invalid bind");

        await host.StartAsync();

        host.Status.Should().Be(McpServerHostStatus.Error);
        host.StatusDetail.Should().Be("invalid bind");
        operations.Failures.Should().ContainSingle().Which.FailureCode.Should().Be(AppErrorCodes.ValidationFailed);
        operations.Completions.Should().BeEmpty();
        factory.SettingsSeen.Should().BeEmpty();
    }

    [Fact]
    public async Task Unavailable_progress_store_does_not_block_error_reporting()
    {
        (McpServerHost host, FakeMcpServerSettingsService settings, FakeBlockingOperationService operations,
            _, _) = CreateHost();
        operations.StartSucceeds = false;
        settings.NextGetSettings = Result<McpServerSettings>.Failure(
            AppErrorCodes.DatabaseError, "cannot read mcp settings");

        await host.StartAsync();

        host.Status.Should().Be(McpServerHostStatus.Error);
        host.StatusDetail.Should().Be("cannot read mcp settings");
        operations.Started.Should().ContainSingle();
        operations.Failures.Should().BeEmpty(
            "no progress record means nothing to fail; the error still reaches the status surface");
    }

    [Fact]
    public async Task Listener_start_failure_disposes_the_instance_reports_and_enters_error()
    {
        (McpServerHost host, _, _, FakeMcpServerInstanceFactory factory,
            List<(Exception Exception, string Boundary, string? Operation)> reported) = CreateHost();
        FakeMcpServerInstance listener = new(startException: new InvalidOperationException("port in use"));
        factory.Enqueue(listener);
        List<McpServerHostExceptionEventArgs> exceptions = new();
        host.ExceptionReported += (_, exception) => exceptions.Add(exception);

        await host.StartAsync(ValidSettings());

        host.Status.Should().Be(McpServerHostStatus.Error);
        host.StatusDetail.Should().Be("port in use");
        host.IsRunning.Should().BeFalse();
        host.Server.Should().BeNull();
        listener.StartCalls.Should().Be(1);
        listener.DisposeCalls.Should().Be(1, "a failed listener is disposed after the event is detached");
        exceptions.Should().ContainSingle().Which.Operation.Should().Be("start-listener");
        reported.Should().ContainSingle().Which.Should().Be((exceptions[0].Exception, "mcp-server", "start-listener"));
    }

    [Fact]
    public async Task Persisted_listener_failure_marks_the_progress_record_failed()
    {
        (McpServerHost host, _, FakeBlockingOperationService operations, FakeMcpServerInstanceFactory factory, _) =
            CreateHost();
        factory.Enqueue(new FakeMcpServerInstance(startException: new InvalidOperationException("bind failed")));

        await host.StartAsync();

        host.Status.Should().Be(McpServerHostStatus.Error);
        operations.Failures.Should().ContainSingle().Which.FailureCode.Should().Be(AppErrorCodes.InvalidState);
        operations.Completions.Should().BeEmpty();
    }

    [Fact]
    public async Task Listener_failure_detail_is_sanitized_before_reaching_the_status_surface()
    {
        (McpServerHost host, _, _, FakeMcpServerInstanceFactory factory, _) = CreateHost();
        factory.Enqueue(new FakeMcpServerInstance(
            startException: new InvalidOperationException("bind failed at D:\\secrets\\runtime.sqlite")));

        await host.StartAsync(ValidSettings());

        host.Status.Should().Be(McpServerHostStatus.Error);
        host.StatusDetail.Should().Be("bind failed at [redacted]");
    }

    [Fact]
    public async Task Start_while_already_running_reuses_the_listener_and_refreshes_counts()
    {
        (McpServerHost host, FakeMcpServerSettingsService settings, _, FakeMcpServerInstanceFactory factory, _) =
            CreateHost();
        FakeMcpServerInstance listener = new();
        factory.Enqueue(listener);
        await host.StartAsync(ValidSettings());
        listener.ActiveConnectionCount = 2;
        listener.TotalConnectionCount = 5;

        await host.StartAsync(ValidSettings(99));

        factory.SettingsSeen.Should().ContainSingle("a running listener is reused, not recreated");
        listener.StartCalls.Should().Be(1);
        settings.ValidatedSettings.Should().ContainSingle();
        host.Status.Should().Be(McpServerHostStatus.Running);
        host.StatusDetail.Should().Be("连接数: 2 / 5");
        host.RunningSettingsRevision.Should().Be(3);
    }

    [Fact]
    public async Task Stop_disposes_the_listener_and_clears_the_running_projection()
    {
        (McpServerHost host, _, _, FakeMcpServerInstanceFactory factory, _) = CreateHost();
        await host.StartAsync(ValidSettings());
        FakeMcpServerInstance listener = factory.Created[0];

        await host.StopAsync();

        host.Status.Should().Be(McpServerHostStatus.Stopped);
        host.StatusDetail.Should().Be("MCP HTTP 服务已停止。");
        host.IsRunning.Should().BeFalse();
        host.Server.Should().BeNull();
        host.RunningSettings.Should().BeNull();
        host.RunningSettingsRevision.Should().BeNull();
        listener.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task Stop_awaits_an_inflight_background_start()
    {
        (McpServerHost host, _, _, FakeMcpServerInstanceFactory factory, _) = CreateHost();
        TaskCompletionSource startInvoked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeMcpServerInstance listener = new()
        {
            StartHook = _ =>
            {
                startInvoked.TrySetResult();
                return releaseStart.Task;
            }
        };
        factory.Enqueue(listener);

        host.StartInBackground();
        await startInvoked.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task stop = host.StopAsync();
        stop.IsCompleted.Should().BeFalse("StopAsync awaits the in-flight background start");

        releaseStart.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));

        host.Status.Should().Be(McpServerHostStatus.Stopped);
        listener.StartCalls.Should().Be(1);
        listener.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task Background_start_is_deduplicated_while_inflight()
    {
        (McpServerHost host, _, _, FakeMcpServerInstanceFactory factory, _) = CreateHost();
        TaskCompletionSource startInvoked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeMcpServerInstance listener = new()
        {
            StartHook = _ =>
            {
                startInvoked.TrySetResult();
                return releaseStart.Task;
            }
        };
        factory.Enqueue(listener);

        host.StartInBackground();
        host.StartInBackground();
        await startInvoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        releaseStart.SetResult();
        await host.StopAsync();

        listener.StartCalls.Should().Be(1, "a second StartInBackground returns while a start is in flight");
    }

    [Fact]
    public async Task Connection_count_changes_rebroadcast_only_from_the_current_listener()
    {
        (McpServerHost host, _, _, FakeMcpServerInstanceFactory factory, _) = CreateHost();
        await host.StartAsync(ValidSettings());
        FakeMcpServerInstance listener = factory.Created[0];
        int notifications = 0;
        object? seenSender = null;
        host.ConnectionCountsChanged += (sender, _) =>
        {
            notifications++;
            seenSender = sender;
        };

        listener.RaiseConnectionCountsChanged();
        notifications.Should().Be(1);
        seenSender.Should().BeSameAs(host);

        await host.StopAsync();
        listener.RaiseConnectionCountsChanged();
        notifications.Should().Be(1, "a detached listener no longer drives host notifications");
    }

    [Fact]
    public async Task Exception_reporting_failures_do_not_escape_the_lifecycle()
    {
        FakeMcpServerSettingsService settings = new();
        FakeBlockingOperationService operations = new();
        FakeMcpServerInstanceFactory factory = new();
        factory.Enqueue(new FakeMcpServerInstance(startException: new InvalidOperationException("bind failed")));
        McpServerHost host = new(new McpServerHostDependencies(settings, operations, factory),
            (_, _, _) => throw new InvalidOperationException("reporter down"));
        host.ExceptionReported += (_, _) => throw new InvalidOperationException("subscriber down");

        await host.StartAsync(ValidSettings());

        host.Status.Should().Be(McpServerHostStatus.Error);
        host.StatusDetail.Should().Be("bind failed");
    }

    [Fact]
    public async Task Restart_stops_the_old_listener_and_starts_a_fresh_one()
    {
        (McpServerHost host, FakeMcpServerSettingsService settings, FakeBlockingOperationService operations,
            FakeMcpServerInstanceFactory factory, _) = CreateHost();
        FakeMcpServerInstance first = new("http://localhost:40001/mcp");
        FakeMcpServerInstance second = new("http://localhost:40002/mcp");
        factory.Enqueue(first);
        factory.Enqueue(second);
        await host.StartAsync(ValidSettings());

        await host.RestartAsync();

        factory.Created.Should().HaveCount(2);
        first.DisposeCalls.Should().Be(1);
        second.StartCalls.Should().Be(1);
        host.Status.Should().Be(McpServerHostStatus.Running);
        host.Endpoint.Should().Be("http://localhost:40002/mcp");
        settings.ValidatedSettings.Should().HaveCount(2, "restart reloads and revalidates persisted settings");
        operations.Completions.Should().ContainSingle("the restart takes the progress-recorded path");
    }

    [Fact]
    public async Task Wait_for_shutdown_delegates_to_the_running_listener()
    {
        (McpServerHost host, _, _, FakeMcpServerInstanceFactory factory, _) = CreateHost();
        await host.StartAsync(ValidSettings());
        FakeMcpServerInstance listener = factory.Created[0];

        await host.WaitForShutdownAsync();

        listener.RunCalls.Should().Be(1);
    }

    [Fact]
    public async Task Dispose_stops_the_running_listener()
    {
        (McpServerHost host, _, _, FakeMcpServerInstanceFactory factory, _) = CreateHost();
        await host.StartAsync(ValidSettings());
        FakeMcpServerInstance listener = factory.Created[0];

        await host.DisposeAsync();

        host.Status.Should().Be(McpServerHostStatus.Stopped);
        host.IsRunning.Should().BeFalse();
        listener.DisposeCalls.Should().Be(1);
    }

    private static McpServerSettings ValidSettings(long revision = 3)
    {
        return new McpServerSettings(4537, "127.0.0.1", false, [], false, null, [],
            DateTimeOffset.UtcNow, revision);
    }

    private static (McpServerHost Host, FakeMcpServerSettingsService Settings,
        FakeBlockingOperationService Operations, FakeMcpServerInstanceFactory Factory,
        List<(Exception Exception, string Boundary, string? Operation)> Reported) CreateHost()
    {
        FakeMcpServerSettingsService settings = new();
        FakeBlockingOperationService operations = new();
        FakeMcpServerInstanceFactory factory = new();
        List<(Exception Exception, string Boundary, string? Operation)> reported = new();
        McpServerHost host = new(new McpServerHostDependencies(settings, operations, factory),
            (exception, boundary, operation) => reported.Add((exception, boundary, operation)));
        return (host, settings, operations, factory, reported);
    }

    private sealed class FakeMcpServerInstance : IMcpServerInstance
    {
        public FakeMcpServerInstance(string endpoint = "http://localhost:4537/mcp",
            Exception? startException = null)
        {
            Endpoint = endpoint;
            StartException = startException;
        }

        public Exception? StartException { get; }
        public Func<CancellationToken, Task>? StartHook { get; init; }
        public string Endpoint { get; }
        public McpHttpServer? HttpServer => null;
        public bool IsRunning { get; private set; }
        public long ActiveConnectionCount { get; set; }
        public long TotalConnectionCount { get; set; }
        public int StartCalls { get; private set; }
        public int RunCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public event EventHandler? ConnectionCountsChanged;

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCalls++;
            if (StartHook is not null)
            {
                await StartHook(cancellationToken);
            }

            if (StartException is not null)
            {
                throw StartException;
            }

            IsRunning = true;
        }

        public Task RunAsync(CancellationToken cancellationToken = default)
        {
            RunCalls++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            IsRunning = false;
            return ValueTask.CompletedTask;
        }

        public void RaiseConnectionCountsChanged()
        {
            ConnectionCountsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeMcpServerInstanceFactory : IMcpServerInstanceFactory
    {
        private readonly Queue<FakeMcpServerInstance> _queued = new();

        public List<FakeMcpServerInstance> Created { get; } = new();
        public List<McpServerSettings> SettingsSeen { get; } = new();
        public List<Action<Exception, string>> ReportersSeen { get; } = new();

        public void Enqueue(FakeMcpServerInstance instance)
        {
            _queued.Enqueue(instance);
        }

        public IMcpServerInstance Create(McpServerSettings settings,
            Action<Exception, string> reportException)
        {
            SettingsSeen.Add(settings);
            ReportersSeen.Add(reportException);
            FakeMcpServerInstance instance = _queued.Count > 0 ? _queued.Dequeue() : new FakeMcpServerInstance();
            Created.Add(instance);
            return instance;
        }
    }

    private sealed class FakeMcpServerSettingsService : IMcpServerSettingsService
    {
        public Result<McpServerSettings>? NextGetSettings { get; set; }
        public Result? NextValidation { get; set; }
        public List<McpServerSettings> ValidatedSettings { get; } = new();

        public Task<Result<McpServerSettings>> GetSettingsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(NextGetSettings ?? Result<McpServerSettings>.Success(ValidSettings()));
        }

        public Task<Result> ValidateSettingsAsync(McpServerSettings settings,
            CancellationToken cancellationToken = default)
        {
            ValidatedSettings.Add(settings);
            return Task.FromResult(NextValidation ?? Result.Success());
        }

        public Task<Result<McpServerSettings>> SaveSettingsAsync(McpServerSettings settings,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<McpServerSettings>> SaveSettingsAsync(McpServerSettings settings, long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<McpServerSettings>> SaveDraftSettingsAsync(McpServerSettings settings, long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<bool>> IsToolEnabledAsync(string toolName, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeBlockingOperationService : IBlockingOperationService
    {
        public bool StartSucceeds { get; set; } = true;
        public List<(string OperationType, string ScopeType, string? ScopeId)> Started { get; } = new();

        public List<(BlockingOperationId OperationId, string FailureCode, string FailureMessage)> Failures { get; } =
            new();

        public List<BlockingOperationId> Completions { get; } = new();
        public int StartCalls => Started.Count;

        public Task<Result<BlockingOperation>> StartAsync(string operationType, string scopeType,
            string? scopeId = null, bool canCancel = false, string? progressLabel = null, int? progressCurrent = null,
            int? progressTotal = null, IReadOnlyList<string>? nextActions = null,
            CancellationToken cancellationToken = default)
        {
            Started.Add((operationType, scopeType, scopeId));
            BlockingOperationId operationId = new(Guid.NewGuid());
            return Task.FromResult(StartSucceeds
                ? Result<BlockingOperation>.Success(Operation(operationId))
                : Result<BlockingOperation>.Failure(AppErrorCodes.DatabaseError,
                    "blocking operation store unavailable"));
        }

        public Task<Result<BlockingOperation>> CompleteAsync(BlockingOperationId operationId,
            string? progressLabel = null, IReadOnlyList<string>? nextActions = null,
            CancellationToken cancellationToken = default)
        {
            Completions.Add(operationId);
            return Task.FromResult(Result<BlockingOperation>.Success(Operation(operationId)));
        }

        public Task<Result<BlockingOperation>> FailAsync(BlockingOperationId operationId, string failureCode,
            string failureMessage, string? progressLabel = null, IReadOnlyList<string>? nextActions = null,
            CancellationToken cancellationToken = default)
        {
            Failures.Add((operationId, failureCode, failureMessage));
            return Task.FromResult(Result<BlockingOperation>.Success(Operation(operationId)));
        }

        public Task<Result<BlockingOperation>> UpdateProgressAsync(BlockingOperationId operationId,
            int? progressCurrent = null, int? progressTotal = null, string? progressLabel = null,
            IReadOnlyList<string>? nextActions = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<BlockingOperation>> CancelAsync(BlockingOperationId operationId,
            string? progressLabel = null,
            IReadOnlyList<string>? nextActions = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<BlockingOperation>> GetAsync(BlockingOperationId operationId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<IReadOnlyList<BlockingOperation>>> ListAsync(string? status = null,
            string? operationType = null, string? scopeType = null, string? scopeId = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<BlockingOperationLogEntry>> AddLogEntryAsync(BlockingOperationId operationId, string level,
            string message, string? detail = null, string? scopeType = null, string? scopeId = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<IReadOnlyList<BlockingOperationLogEntry>>> GetLogEntriesAsync(
            BlockingOperationId operationId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        private static BlockingOperation Operation(BlockingOperationId operationId)
        {
            return new BlockingOperation(operationId, BlockingOperationTypes.McpStartValidation,
                BlockingOperationScopeTypes.McpServerSettings, "default", BlockingOperationStatus.Running, null, null,
                null, false, null, null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        }
    }
}
