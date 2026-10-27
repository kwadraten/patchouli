using System.Collections.Concurrent;
using System.Text.Json;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.UI;

namespace Patchouli.Host;

/// <summary>
///     Device-local OCR preset selections persisted next to the user settings file as
///     <c>&lt;settingsPath&gt;.ocr-presets.json</c>, namespaced by library id so a choice in one
///     library can never alias another library's presets. Writes are atomic (temp file + replace)
///     and scoped to this file only — unrelated settings are untouched. The read-modify-write
///     gate is process-wide per canonical file path: every view-model creates its own store
///     instance, so a per-instance semaphore would let parallel saves read the same base file
///     and silently lose each other's scope entries.
/// </summary>
public sealed class OcrPresetSelectionStore(string settingsPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _filePath = settingsPath + ".ocr-presets.json";
    private readonly SemaphoreSlim _gate = GateFor(settingsPath + ".ocr-presets.json");

    private static SemaphoreSlim GateFor(string filePath)
    {
        string canonical;
        try
        {
            canonical = Path.GetFullPath(filePath);
        }
        catch (Exception)
        {
            canonical = filePath;
        }

        return Gates.GetOrAdd(canonical, static _ => new SemaphoreSlim(1, 1));
    }

    public async Task<Result<OcrPresetSelections>> ReadAsync(LibraryId libraryId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return Result<OcrPresetSelections>.Success(OcrPresetSelections.Empty);
            }

            await _gate.WaitAsync(cancellationToken);
            try
            {
                await using FileStream stream = new(_filePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 4096, FileOptions.Asynchronous);
                OcrPresetSelectionFile? file =
                    await JsonSerializer.DeserializeAsync<OcrPresetSelectionFile>(stream, JsonOptions,
                        cancellationToken);
                return Result<OcrPresetSelections>.Success(
                    file?.Libraries is not null &&
                    file.Libraries.TryGetValue(libraryId.ToString(), out OcrPresetSelections? selections)
                        ? selections ?? OcrPresetSelections.Empty
                        : OcrPresetSelections.Empty);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Result<OcrPresetSelections>.Failure(AppErrorCodes.DatabaseError,
                $"OCR preset selections could not be read: {exception.Message}");
        }
    }

    public Task<Result> WriteAsync(LibraryId libraryId, OcrPresetSelections selections,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selections);
        return MutateAsync(libraryId, _ => selections, cancellationToken);
    }

    /// <summary>
    ///     Reads this library's selections, applies <paramref name="transform" />, and persists —
    ///     all under ONE hold of the shared file gate. Read-then-write through the separate
    ///     <see cref="ReadAsync" />/<see cref="WriteAsync" /> pair leaves an interleaving window
    ///     where a concurrent save on another store instance silently drops this one's scopes.
    /// </summary>
    public async Task<Result> MutateAsync(LibraryId libraryId,
        Func<OcrPresetSelections, OcrPresetSelections> transform,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transform);
        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                OcrPresetSelectionFile file = await ReadFileAsync(cancellationToken);
                file.Libraries ??= new Dictionary<string, OcrPresetSelections>(StringComparer.Ordinal);
                file.Libraries.TryGetValue(libraryId.ToString(), out OcrPresetSelections? current);
                file.Libraries[libraryId.ToString()] =
                    transform(current ?? OcrPresetSelections.Empty);

                string directory = Path.GetDirectoryName(_filePath) ?? ".";
                string temporary = Path.Combine(directory,
                    $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
                await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write,
                                 FileShare.None, 4096, FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(output, file, JsonOptions, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }

                File.Move(temporary, _filePath, true);
                return Result.Success();
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Result.Failure(AppErrorCodes.DatabaseError,
                $"OCR preset selections could not be saved: {exception.Message}");
        }
    }

    private async Task<OcrPresetSelectionFile> ReadFileAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new OcrPresetSelectionFile();
        }

        await using FileStream existing = new(_filePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<OcrPresetSelectionFile>(existing,
            JsonOptions, cancellationToken) ?? new OcrPresetSelectionFile();
    }

    private sealed class OcrPresetSelectionFile
    {
        public Dictionary<string, OcrPresetSelections>? Libraries { get; set; }
    }
}
