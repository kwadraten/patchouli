using System.Text.Json;
using System.Text.Json.Nodes;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Results;
using Patchouli.Core.Settings;
using Patchouli.Core.Time;

namespace Patchouli.Infrastructure.Settings;

/// <summary>
/// Issue #11: device-local persistence for <see cref="FileHandlingBehaviorSettings"/>.
/// Stored in the shared device settings file under the "FileHandlingBehavior" root
/// section (same file as MCP settings, credentials and device root bindings), guarded
/// by <see cref="SettingsFileWriteCoordinator"/>; never synced with Library snapshots.
/// Desktop and headless hosts read by the same rules.
/// </summary>
public sealed class FileHandlingBehaviorSettingsService : IFileHandlingBehaviorSettingsService
{
    private const string SectionName = "FileHandlingBehavior";

    private readonly string _path;
    private readonly IClock _clock;

    public FileHandlingBehaviorSettingsService(string path, IClock clock)
    {
        _path = path;
        _clock = clock;
    }

    public async Task<Result<FileHandlingBehaviorSettings>> GetSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Result<FileHandlingBehaviorSettings>.Success(await ReadAsync(cancellationToken));
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.file-handling-behavior-settings"))
        {
            return Result<FileHandlingBehaviorSettings>.Failure(AppErrorCodes.DatabaseError,
                $"File handling behavior settings load failed: {exception.Message}");
        }
    }

    public async Task<Result<FileHandlingBehaviorSettings>> SaveSettingsAsync(
        FileHandlingBehaviorSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        SemaphoreSlim gate = SettingsFileWriteCoordinator.ForPath(_path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            JsonObject root = await ReadRootAsync(cancellationToken);
            root[SectionName] = new JsonObject
            {
                ["McpAddPdfBehavior"] = settings.McpAddPdfBehavior.ToString(),
                ["PdfDragDropBehavior"] = settings.PdfDragDropBehavior.ToString(),
                ["UpdatedAt"] = _clock.UtcNow.ToUniversalTime().ToString("O")
            };
            await WriteRootAsync(root, cancellationToken);
            return Result<FileHandlingBehaviorSettings>.Success(settings);
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.file-handling-behavior-settings"))
        {
            return Result<FileHandlingBehaviorSettings>.Failure(AppErrorCodes.DatabaseError,
                $"File handling behavior settings save failed: {exception.Message}");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<FileHandlingBehaviorSettings> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return FileHandlingBehaviorSettings.Default();
        }

        await using FileStream stream = File.OpenRead(_path);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty(SectionName, out JsonElement section))
        {
            return FileHandlingBehaviorSettings.Default();
        }

        return new FileHandlingBehaviorSettings(
            ParseBehavior(section, "McpAddPdfBehavior"),
            ParseBehavior(section, "PdfDragDropBehavior"));
    }

    private static FileHandlingBehavior ParseBehavior(JsonElement section, string name)
    {
        if (section.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String &&
            Enum.TryParse<FileHandlingBehavior>(value.GetString(), out FileHandlingBehavior parsed))
        {
            return parsed;
        }

        return FileHandlingBehavior.KeepInPlace;
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
