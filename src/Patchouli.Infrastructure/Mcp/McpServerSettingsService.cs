using System.Text.Json;
using Dapper;
using Patchouli.Core.Mcp;
using Patchouli.Core.Operations;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Mcp;

namespace Patchouli.Infrastructure.Mcp;

public sealed class McpServerSettingsService : IMcpServerSettingsService
{
    public const int DefaultPort = 4536;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly IBlockingOperationService? _blockingOperations;

    public McpServerSettingsService(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        IBlockingOperationService? blockingOperations = null)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _blockingOperations = blockingOperations;
    }

    public async Task<Result<McpServerSettings>> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var row = await connection.QuerySingleOrDefaultAsync<Row>(
                """
                select port as Port,
                       bind_address as BindAddress,
                       cors_enabled as CorsEnabled,
                       allowed_origins_json as AllowedOriginsJson,
                       auth_required as AuthRequired,
                       token as Token,
                       updated_at as UpdatedAt
                from mcp_server_settings
                order by updated_at desc
                limit 1;
                """);
            if (row is null)
            {
                return Result<McpServerSettings>.Success(DefaultSettings(_clock.UtcNow));
            }

            var overrides = await connection.QueryAsync<OverrideRow>(
                """
                select tool_name as ToolName,
                       enabled as Enabled,
                       disabled_reason as DisabledReason
                from mcp_tool_overrides
                order by tool_name;
                """);
            return Result<McpServerSettings>.Success(row.ToModel(overrides.Select(value => value.ToModel()).ToArray()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.mcp-server-settings"))
        {
            return Result<McpServerSettings>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<McpServerSettings>> SaveSettingsAsync(McpServerSettings settings, CancellationToken cancellationToken = default)
    {
        var validation = await ValidateSettingsAsync(settings, cancellationToken);
        if (validation.IsFailure)
        {
            return Result<McpServerSettings>.Failure(validation.ErrorCode!, validation.ErrorMessage!);
        }

        try
        {
            var saved = settings with { UpdatedAt = _clock.UtcNow.ToUniversalTime() };
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await connection.ExecuteAsync("delete from mcp_server_settings;", transaction: transaction);
            await connection.ExecuteAsync(
                """
                insert into mcp_server_settings (
                    settings_id, port, bind_address, cors_enabled, allowed_origins_json, auth_required, token, updated_at
                )
                values (
                    @SettingsId, @Port, @BindAddress, @CorsEnabled, @AllowedOriginsJson, @AuthRequired, @Token, @UpdatedAt
                );
                """,
                new
                {
                    SettingsId = "default",
                    saved.Port,
                    saved.BindAddress,
                    CorsEnabled = saved.CorsEnabled ? 1 : 0,
                    AllowedOriginsJson = JsonSerializer.Serialize(saved.AllowedOrigins),
                    AuthRequired = saved.AuthRequired ? 1 : 0,
                    saved.Token,
                    UpdatedAt = saved.UpdatedAt.ToString("O")
                },
                transaction);

            await connection.ExecuteAsync("delete from mcp_tool_overrides;", transaction: transaction);
            foreach (var toolOverride in saved.ToolOverrides)
            {
                await connection.ExecuteAsync(
                    """
                    insert into mcp_tool_overrides (tool_name, enabled, disabled_reason)
                    values (@ToolName, @Enabled, @DisabledReason);
                    """,
                    new
                    {
                        ToolName = toolOverride.ToolName.Trim(),
                        Enabled = toolOverride.Enabled ? 1 : 0,
                        toolOverride.DisabledReason
                    },
                    transaction);
            }

            await transaction.CommitAsync(cancellationToken);
            return Result<McpServerSettings>.Success(saved);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.mcp-server-settings"))
        {
            return Result<McpServerSettings>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result> ValidateSettingsAsync(McpServerSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings.Port is < 1 or > 65535)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "MCP port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(settings.BindAddress))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "MCP bind address is required.");
        }

        var bindAddress = settings.BindAddress.Trim();
        if (string.Equals(bindAddress, "0.0.0.0", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(settings.Token))
        {
            const string message = "Binding MCP to 0.0.0.0 requires a bearer token.";
            await RecordUnsafeBindFailureAsync(message, cancellationToken);
            return Result.Failure(McpErrorCodes.UnsafeConfiguration, message);
        }

        if (settings.AuthRequired && string.IsNullOrWhiteSpace(settings.Token))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "Auth-required MCP settings must include a token.");
        }

        return Result.Success();
    }

    public async Task<Result<bool>> IsToolEnabledAsync(string toolName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return Result<bool>.Failure(AppErrorCodes.ValidationFailed, "Tool name is required.");
        }

        var settings = await GetSettingsAsync(cancellationToken);
        if (settings.IsFailure)
        {
            return Result<bool>.Failure(settings.ErrorCode!, settings.ErrorMessage!);
        }

        var toolOverride = settings.Value.ToolOverrides.FirstOrDefault(value => string.Equals(value.ToolName, toolName.Trim(), StringComparison.Ordinal));
        return Result<bool>.Success(toolOverride?.Enabled ?? true);
    }

    public static McpServerSettings DefaultSettings(DateTimeOffset now) => new(
        DefaultPort,
        "127.0.0.1",
        false,
        [],
        false,
        null,
        [],
        now.ToUniversalTime());

    private async Task RecordUnsafeBindFailureAsync(string failureMessage, CancellationToken cancellationToken)
    {
        if (_blockingOperations is null)
        {
            return;
        }

        try
        {
            var started = await _blockingOperations.StartAsync(
                BlockingOperationTypes.McpStartValidation,
                BlockingOperationScopeTypes.McpServerSettings,
                "default",
                canCancel: false,
                progressLabel: "Validating MCP startup configuration.",
                nextActions: ["Bind to 127.0.0.1", "Configure a bearer token"],
                cancellationToken: cancellationToken);
            if (started.IsSuccess)
            {
                await _blockingOperations.FailAsync(
                    started.Value.OperationId,
                    McpErrorCodes.UnsafeConfiguration,
                    failureMessage,
                    "Unsafe MCP startup configuration blocked.",
                    ["Bind to 127.0.0.1", "Configure a bearer token"],
                    cancellationToken);
            }
        }
        catch
        {
        }
    }

    private sealed class Row
    {
        public int Port { get; set; }
        public string BindAddress { get; set; } = "";
        public int CorsEnabled { get; set; }
        public string AllowedOriginsJson { get; set; } = "[]";
        public int AuthRequired { get; set; }
        public string? Token { get; set; }
        public string UpdatedAt { get; set; } = "";

        public McpServerSettings ToModel(IReadOnlyList<McpToolOverride> overrides)
            => new(
                Port,
                BindAddress,
                CorsEnabled != 0,
                ParseAllowedOrigins(AllowedOriginsJson),
                AuthRequired != 0,
                Token,
                overrides,
                DateTimeOffset.Parse(UpdatedAt));
    }

    private sealed class OverrideRow
    {
        public string ToolName { get; set; } = "";
        public int Enabled { get; set; }
        public string? DisabledReason { get; set; }

        public McpToolOverride ToModel() => new(ToolName, Enabled != 0, DisabledReason);
    }

    private static IReadOnlyList<string> ParseAllowedOrigins(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
