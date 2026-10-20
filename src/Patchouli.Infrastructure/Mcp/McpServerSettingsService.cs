using System.Text.Json;
using System.Text.Json.Nodes;
using Patchouli.Core.Mcp;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Operations;
using Patchouli.Core.Results;
using Patchouli.Core.Settings;
using Patchouli.Core.Time;
using Patchouli.Mcp;

namespace Patchouli.Infrastructure.Mcp;

public sealed class McpServerSettingsService : IMcpServerSettingsService
{
    public const int DefaultPort = 4536;
    private readonly string _path;
    private readonly IClock _clock;
    private readonly IBlockingOperationService? _blockingOperations;

    public McpServerSettingsService(string path, IClock clock, IBlockingOperationService? blockingOperations = null)
    {
        _path = path;
        _clock = clock;
        _blockingOperations = blockingOperations;
    }

    public async Task<Result<McpServerSettings>> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return Result<McpServerSettings>.Success(await ReadAsync(cancellationToken));
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.mcp-server-settings"))
        {
            return Result<McpServerSettings>.Failure(AppErrorCodes.DatabaseError,
                $"MCP settings load failed: {exception.Message}");
        }
    }

    public async Task<Result<McpServerSettings>> SaveSettingsAsync(McpServerSettings settings,
        CancellationToken cancellationToken = default)
    {
        return await SaveSettingsAsync(settings, settings.Revision, cancellationToken);
    }

    public async Task<Result<McpServerSettings>> SaveSettingsAsync(
        McpServerSettings settings,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        return await SaveCoreAsync(settings, expectedRevision, true, cancellationToken);
    }

    public Task<Result<McpServerSettings>> SaveDraftSettingsAsync(McpServerSettings settings, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        return SaveCoreAsync(settings, expectedRevision, false, cancellationToken);
    }

    private async Task<Result<McpServerSettings>> SaveCoreAsync(McpServerSettings settings, long expectedRevision,
        bool validateRuntime, CancellationToken cancellationToken)
    {
        McpServerSettings draft = Freeze(settings);
        SemaphoreSlim gate = SettingsFileWriteCoordinator.ForPath(_path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            Result validation = validateRuntime
                ? await ValidateSettingsAsync(draft, cancellationToken)
                : ValidatePermissionStructure(draft);
            if (validation.IsFailure)
            {
                return Result<McpServerSettings>.Failure(validation.ErrorCode!, validation.ErrorMessage!);
            }

            McpServerSettings current = await ReadAsync(cancellationToken);
            if (current.Revision != expectedRevision)
            {
                return Result<McpServerSettings>.Failure(AppErrorCodes.StaleSettingsRevision,
                    $"MCP settings revision {expectedRevision} is stale; current revision is {current.Revision}.");
            }

            McpServerSettings saved = draft with
            {
                UpdatedAt = _clock.UtcNow.ToUniversalTime(),
                Revision = current.Revision + 1
            };
            await WriteAsync(saved, cancellationToken);
            return Result<McpServerSettings>.Success(saved);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<Result> ValidateSettingsAsync(McpServerSettings settings,
        CancellationToken cancellationToken = default)
    {
        Result permissions = ValidatePermissionStructure(settings);
        if (permissions.IsFailure)
        {
            return permissions;
        }

        return await ValidateRuntimeSettingsAsync(settings, cancellationToken);
    }

    private static Result ValidatePermissionStructure(McpServerSettings settings)
    {
        if (settings.DomainPermissions.Any(value => !McpPermissionPolicy.Domains.Contains(value.Domain) ||
                                                    !McpPermissionPolicy.Verbs.Contains(value.Verb)) ||
            settings.DomainPermissions.Select(value => (value.Domain, value.Verb)).Distinct().Count() !=
            settings.DomainPermissions.Count)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "Invalid or duplicate MCP domain permission.");
        }

        return Result.Success();
    }

    private async Task<Result> ValidateRuntimeSettingsAsync(McpServerSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.Port is < 1 or > 65535)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "MCP port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(settings.BindAddress))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "MCP bind address is required.");
        }

        if (settings.ShellCommandTimeoutSeconds is < 1 or > 60)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed,
                "Shell command timeout must be between 1 and 60 seconds.");
        }

        if (settings.BindAddress.Trim() == "0.0.0.0" && string.IsNullOrWhiteSpace(settings.Token))
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
        Result<McpServerSettings> settings = await GetSettingsAsync(cancellationToken);
        if (settings.IsFailure)
        {
            return Result<bool>.Failure(settings.ErrorCode!, settings.ErrorMessage!);
        }

        McpToolOverride? item = settings.Value.ToolOverrides.FirstOrDefault(value => value.ToolName == toolName.Trim());
        return Result<bool>.Success(item?.Enabled ?? true);
    }

    public static McpServerSettings DefaultSettings(DateTimeOffset now)
    {
        return new McpServerSettings(DefaultPort, "127.0.0.1", false, [], false, null, [], now.ToUniversalTime());
    }

    private async Task<McpServerSettings> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return DefaultSettings(_clock.UtcNow);
        }

        await using FileStream stream = File.OpenRead(_path);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("Mcp", out JsonElement mcp))
        {
            return DefaultSettings(_clock.UtcNow);
        }

        int port = mcp.TryGetProperty("Port", out JsonElement p) ? p.GetInt32() : DefaultPort;
        string bindAddress = mcp.TryGetProperty("BindAddress", out JsonElement bind)
            ? bind.GetString() ?? "127.0.0.1"
            : "127.0.0.1";
        bool cors = mcp.TryGetProperty("CorsEnabled", out JsonElement c) && c.GetBoolean();
        bool auth = mcp.TryGetProperty("AuthRequired", out JsonElement a) && a.GetBoolean();
        string[] origins = mcp.TryGetProperty("AllowedOrigins", out JsonElement o) && o.ValueKind == JsonValueKind.Array
            ? o.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!).ToArray()
            : [];
        string token = mcp.TryGetProperty("Token", out JsonElement t) ? t.GetString() ?? "" : "";
        IReadOnlyList<McpToolOverride> tools =
            mcp.TryGetProperty("ToolOverrides", out JsonElement overrides) && overrides.ValueKind == JsonValueKind.Array
                ? overrides.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object)
                    .Select(value => new McpToolOverride(value.GetProperty("ToolName").GetString() ?? "",
                        value.GetProperty("Enabled").GetBoolean(),
                        value.TryGetProperty("DisabledReason", out JsonElement reason) ? reason.GetString() : null))
                    .ToArray()
                : [];
        long revision = mcp.TryGetProperty("Revision", out JsonElement revisionValue)
            ? revisionValue.GetInt64()
            : 0;
        int shellCommandTimeoutSeconds =
            mcp.TryGetProperty("ShellCommandTimeoutSeconds", out JsonElement timeoutValue)
                ? timeoutValue.GetInt32()
                : 15;
        bool exposeLibraryTags =
            !mcp.TryGetProperty("ExposeLibraryTags", out JsonElement exposeTags) || exposeTags.GetBoolean();
        bool exposeLibraryCollections =
            !mcp.TryGetProperty("ExposeLibraryCollections", out JsonElement exposeCollections) ||
            exposeCollections.GetBoolean();
        DateTimeOffset updatedAt =
            mcp.TryGetProperty("UpdatedAt", out JsonElement updatedAtValue) &&
            DateTimeOffset.TryParse(updatedAtValue.GetString(), out DateTimeOffset parsedUpdatedAt)
                ? parsedUpdatedAt.ToUniversalTime()
                : _clock.UtcNow.ToUniversalTime();
        McpServerSettings settings = new(port, bindAddress, cors, origins,
            auth || !string.IsNullOrWhiteSpace(token),
            string.IsNullOrWhiteSpace(token) ? null : token, tools, updatedAt, revision)
        {
            ShellCommandTimeoutSeconds = shellCommandTimeoutSeconds,
            ExposeLibraryTags = exposeLibraryTags,
            ExposeLibraryCollections = exposeLibraryCollections,
            DomainPermissions = mcp.TryGetProperty("DomainPermissions", out JsonElement permissions)
                ? NormalizePermissions(
                    JsonSerializer.Deserialize<McpDomainPermission[]>(permissions.GetRawText()) ?? [])
                : []
        };
        return MigrateAddViewDefaults(settings);
    }

    /// <summary>
    /// Applies the same legacy permission migration used when settings load. Callers that supply an
    /// already-deserialized settings object (the headless host and the standalone server) use this so
    /// retired domains and verbs never fail validation during startup.
    /// </summary>
    public static McpServerSettings NormalizeLegacySettings(McpServerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return MigrateAddViewDefaults(settings);
    }

    /// <summary>
    /// New send subdomains require explicit grants. Retired Agent/action permissions never
    /// promote to endpoint permissions; missing children are materialized as disabled.
    /// </summary>
    private static McpServerSettings MigrateAddViewDefaults(McpServerSettings settings)
    {
        string[] newTools = ["patchouli.add", "patchouli.view"];
        List<McpToolOverride> overrides = settings.ToolOverrides
            .Where(value => !string.Equals(value.ToolName, "patchouli.ocr", StringComparison.Ordinal))
            .ToList();
        bool changed = overrides.Count != settings.ToolOverrides.Count;
        foreach (string tool in newTools)
        {
            if (overrides.All(value => value.ToolName != tool))
            {
                overrides.Add(new McpToolOverride(tool, false,
                    "Added by migration: explicitly enable to use this tool."));
                changed = true;
            }
        }

        List<McpDomainPermission> permissions = settings.DomainPermissions
            .Where(value => !string.Equals(value.Verb, "ocr", StringComparison.Ordinal) &&
                            McpPermissionPolicy.Domains.Contains(value.Domain) &&
                            McpPermissionPolicy.Verbs.Contains(value.Verb))
            .ToList();
        if (permissions.Count != settings.DomainPermissions.Count)
        {
            changed = true;
        }

        // New send domains are opt-in. A parent or retired action grant cannot promote them.
        if (permissions.RemoveAll(value =>
                value.Verb == "send" && !McpPermissionPolicy.RunSendDomains.Contains(value.Domain)) > 0)
        {
            changed = true;
        }

        foreach (string domain in McpPermissionPolicy.RunSendDomains)
        {
            if (permissions.All(value => value.Domain != domain || value.Verb != "send"))
            {
                permissions.Add(new McpDomainPermission(domain, "send", false));
                changed = true;
            }
        }

        return changed ? settings with { ToolOverrides = overrides, DomainPermissions = permissions } : settings;
    }

    private async Task WriteAsync(McpServerSettings settings, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        JsonObject root;
        if (File.Exists(_path))
        {
            await using FileStream input = File.OpenRead(_path);
            root = await JsonNode.ParseAsync(input, cancellationToken: cancellationToken) as JsonObject ??
                   new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        root["Mcp"] = new JsonObject
        {
            ["Port"] = settings.Port,
            ["BindAddress"] = settings.BindAddress,
            ["CorsEnabled"] = settings.CorsEnabled,
            ["AllowedOrigins"] = JsonSerializer.SerializeToNode(settings.AllowedOrigins),
            ["AuthRequired"] = settings.AuthRequired,
            ["Token"] = settings.Token ?? "",
            ["ToolOverrides"] = JsonSerializer.SerializeToNode(settings.ToolOverrides),
            ["DomainPermissions"] = JsonSerializer.SerializeToNode(settings.DomainPermissions),
            ["ShellCommandTimeoutSeconds"] = settings.ShellCommandTimeoutSeconds,
            ["ExposeLibraryTags"] = settings.ExposeLibraryTags,
            ["ExposeLibraryCollections"] = settings.ExposeLibraryCollections,
            ["UpdatedAt"] = settings.UpdatedAt.ToUniversalTime().ToString("O"),
            ["Revision"] = settings.Revision
        };
        string temporary = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
            File.Move(temporary, _path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _ = exception;
                }
            }
        }
    }

    private static McpDomainPermission[] NormalizePermissions(IEnumerable<McpDomainPermission> permissions)
    {
        return permissions.Select(value =>
                value with { Domain = value.Domain == "library.toon" ? "library.yml" : value.Domain })
            .GroupBy(value => (value.Domain, value.Verb))
            .Select(group =>
                new McpDomainPermission(group.Key.Domain, group.Key.Verb, group.All(value => value.Enabled))).ToArray();
    }

    private static McpServerSettings Freeze(McpServerSettings settings)
    {
        return settings with
        {
            BindAddress = settings.BindAddress.Trim(),
            AllowedOrigins = settings.AllowedOrigins.ToArray(),
            DomainPermissions = NormalizePermissions(settings.DomainPermissions),
            ToolOverrides = settings.ToolOverrides
                .Select(item => item with { })
                .ToArray()
        };
    }

    private async Task RecordUnsafeBindFailureAsync(string message, CancellationToken cancellationToken)
    {
        if (_blockingOperations is null)
        {
            return;
        }

        try
        {
            Result<BlockingOperation> started = await _blockingOperations.StartAsync(
                BlockingOperationTypes.McpStartValidation, BlockingOperationScopeTypes.McpServerSettings, "default",
                false,
                "Validating MCP startup configuration.", nextActions: ["Bind to 127.0.0.1", "Configure a bearer token"],
                cancellationToken: cancellationToken);
            if (started.IsSuccess)
            {
                await _blockingOperations.FailAsync(started.Value.OperationId,
                    McpErrorCodes.UnsafeConfiguration, message, "Unsafe MCP startup configuration blocked.",
                    ["Bind to 127.0.0.1", "Configure a bearer token"], cancellationToken);
            }
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.mcp-server-settings", "record-unsafe-bind-failure"))
        {
        }
    }
}
