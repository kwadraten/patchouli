using System.Text.Json;
using System.Text.Json.Nodes;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Results;
using Patchouli.Core.Settings;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Settings;

namespace Patchouli.Infrastructure.Update;

/// <summary>
/// Issue #15: device-local persistence for <see cref="UpdateCheckSettings"/>.
/// Stored in the shared device settings file under the "UpdateCheck" root
/// section (same file as MCP settings, credentials and file-handling behavior),
/// guarded by <see cref="Settings.SettingsFileWriteCoordinator"/>; never synced
/// with Library snapshots. Mirrors the FileHandlingBehaviorSettingsService pattern.
/// </summary>
public sealed class UpdateCheckSettingsService : IUpdateCheckSettingsService
{
    private const string SectionName = "UpdateCheck";

    private readonly string _path;
    private readonly IClock _clock;

    public UpdateCheckSettingsService(string path, IClock clock)
    {
        _path = path;
        _clock = clock;
    }

    public async Task<Result<UpdateCheckSettings>> GetSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Result<UpdateCheckSettings>.Success(await ReadAsync(cancellationToken));
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.update-check-settings"))
        {
            return Result<UpdateCheckSettings>.Failure(AppErrorCodes.DatabaseError,
                $"更新检查设置读取失败：{exception.Message}");
        }
    }

    public async Task<Result<UpdateCheckSettings>> SaveSettingsAsync(
        UpdateCheckSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            await WriteAsync(settings, cancellationToken);
            return Result<UpdateCheckSettings>.Success(settings);
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.update-check-settings"))
        {
            return Result<UpdateCheckSettings>.Failure(AppErrorCodes.DatabaseError,
                $"更新检查设置保存失败：{exception.Message}");
        }
    }

    public async Task<Result<UpdateCheckSettings>> RecordCheckAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            // The whole read-modify-write holds the gate: a concurrent
            // SaveAutoCheckAsync cannot slip between the read and the write
            // and have its flag change clobbered (or vice versa).
            UpdateCheckSettings updated = await RunUnderWriteGateAsync(cancellationToken,
                async innerToken =>
                {
                    UpdateCheckSettings current = await ReadAsync(innerToken);
                    UpdateCheckSettings next = current with { LastCheckUtc = _clock.UtcNow };
                    await WriteSectionAsync(next, innerToken);
                    return next;
                });
            return Result<UpdateCheckSettings>.Success(updated);
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.update-check-settings"))
        {
            return Result<UpdateCheckSettings>.Failure(AppErrorCodes.DatabaseError,
                $"更新检查设置保存失败：{exception.Message}");
        }
    }

    public async Task<Result<UpdateCheckSettings>> SaveAutoCheckAsync(
        bool autoCheckEnabled,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Same gate as RecordCheckAsync: only the flag is replaced, the
            // last-check timestamp is preserved, and the read-modify-write is
            // atomic with respect to concurrent recorders.
            UpdateCheckSettings updated = await RunUnderWriteGateAsync(cancellationToken,
                async innerToken =>
                {
                    UpdateCheckSettings current = await ReadAsync(innerToken);
                    UpdateCheckSettings next = current with { AutoCheckEnabled = autoCheckEnabled };
                    await WriteSectionAsync(next, innerToken);
                    return next;
                });
            return Result<UpdateCheckSettings>.Success(updated);
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.update-check-settings"))
        {
            return Result<UpdateCheckSettings>.Failure(AppErrorCodes.DatabaseError,
                $"更新检查设置保存失败：{exception.Message}");
        }
    }

    private async Task<UpdateCheckSettings> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return UpdateCheckSettings.Default();
        }

        await using FileStream stream = File.OpenRead(_path);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty(SectionName, out JsonElement section))
        {
            return UpdateCheckSettings.Default();
        }

        bool autoCheck = true;
        if (section.TryGetProperty("AutoCheckEnabled", out JsonElement autoCheckElement))
        {
            autoCheck = autoCheckElement.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => true,
            };
        }

        DateTimeOffset? lastCheck = null;
        if (section.TryGetProperty("LastCheckUtc", out JsonElement lastCheckElement) &&
            lastCheckElement.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(lastCheckElement.GetString(), out DateTimeOffset parsed))
        {
            lastCheck = parsed;
        }

        return new UpdateCheckSettings(autoCheck, lastCheck);
    }

    private async Task WriteAsync(UpdateCheckSettings settings, CancellationToken cancellationToken)
    {
        await RunUnderWriteGateAsync(cancellationToken,
            innerToken => WriteSectionAsync(settings, innerToken));
    }

    /// <summary>
    /// Runs <paramref name="action"/> with the settings-file write gate held.
    /// <see cref="SemaphoreSlim"/> is not reentrant, so the gate is acquired
    /// exactly once here; everything under it (e.g. <see
    /// cref="WriteSectionAsync"/>) must not acquire it again.
    /// </summary>
    private async Task<T> RunUnderWriteGateAsync<T>(
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<T>> action)
    {
        SemaphoreSlim gate = SettingsFileWriteCoordinator.ForPath(_path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await action(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private Task RunUnderWriteGateAsync(
        CancellationToken cancellationToken,
        Func<CancellationToken, Task> action)
    {
        return RunUnderWriteGateAsync<object?>(cancellationToken,
            async innerToken =>
            {
                await action(innerToken);
                return null;
            });
    }

    /// <summary>
    /// Replaces the <c>UpdateCheck</c> section of the device settings file.
    /// The caller must hold the <see cref="SettingsFileWriteCoordinator"/> gate.
    /// </summary>
    private async Task WriteSectionAsync(UpdateCheckSettings settings, CancellationToken cancellationToken)
    {
        JsonObject root = await ReadRootAsync(cancellationToken);
        JsonObject section = new()
        {
            ["AutoCheckEnabled"] = settings.AutoCheckEnabled,
            ["UpdatedAt"] = _clock.UtcNow.ToUniversalTime().ToString("O"),
        };
        if (settings.LastCheckUtc.HasValue)
        {
            section["LastCheckUtc"] = settings.LastCheckUtc.Value.ToUniversalTime().ToString("O");
        }

        root[SectionName] = section;
        await WriteRootAsync(root, cancellationToken);
    }

    private async Task<JsonObject> ReadRootAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new JsonObject();
        }

        await using FileStream input = File.OpenRead(_path);
        return await JsonNode.ParseAsync(input, cancellationToken: cancellationToken) as JsonObject ??
               new JsonObject();
    }

    private async Task WriteRootAsync(JsonObject root, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

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
}
