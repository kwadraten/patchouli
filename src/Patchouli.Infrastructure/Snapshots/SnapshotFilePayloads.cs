using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Snapshots;

/// <summary>
/// The wire-format payload kinds for library file trees carried by a snapshot. Each kind maps to one
/// fixed auxiliary directory next to the runtime database. The volatile <c>runs/</c> projection is
/// deliberately absent: it is rebuildable runtime observation and never enters a snapshot (ADR 0036).
/// </summary>
public static class SnapshotFilePayloadKinds
{
    public const string AgentSessions = "agent-sessions";
    public const string Workflows = "workflows";

    /// <summary>The auxiliary library directory archived for one kind (a single safe path segment).</summary>
    public static string LibraryDirectoryName(string kind)
    {
        return kind switch
        {
            AgentSessions => "agent-sessions",
            Workflows => "workflows",
            _ => kind
        };
    }
}

/// <summary>
/// Creates, extracts and merges snapshot file payloads: the <c>agent-sessions/</c> session directories
/// and the <c>workflows/</c> definitions/scripts that the two independent sync switches opt into.
/// </summary>
/// <remarks>
///     Imported session material never transfers run ownership: every session still recorded as
///     <c>Running</c> or <c>AwaitingEffect</c> is persisted as <c>Stopped</c> before it reaches the
///     staging area, so the S5-A startup auto-resume (which only resumes those two statuses) naturally
///     skips imported sessions. Imported workflow definitions never overwrite built-in or locked
///     definitions: built-ins are provided by the application assembly, and a locked local definition
///     is left untouched.
/// </remarks>
public static class SnapshotFilePayloads
{
    private const string StateFileName = "snapshot.json";
    private const string ScriptsDirectoryName = "scripts";
    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    /// <summary>The staging directory that holds the extracted file payloads of one imported branch.</summary>
    public static string StagingPayloadsRoot(string stagingDatabasePath, string snapshotId)
    {
        string? stagingRoot = Path.GetDirectoryName(Path.GetFullPath(stagingDatabasePath));
        return Path.Combine(stagingRoot ?? Path.GetTempPath(), $"{snapshotId}.payloads");
    }

    /// <summary>
    /// Archives one auxiliary library directory into the sync root when the matching switch is on and
    /// the directory actually contains files. Returns <c>null</c> when there is nothing to carry.
    /// </summary>
    public static async Task<SnapshotFilePayload?> CreateArchiveAsync(
        string libraryDatabasePath,
        string syncRoot,
        string snapshotId,
        string kind,
        CancellationToken cancellationToken)
    {
        string? libraryRoot = Path.GetDirectoryName(Path.GetFullPath(libraryDatabasePath));
        if (libraryRoot is null)
        {
            return null;
        }

        string sourceRoot = Path.Combine(libraryRoot, SnapshotFilePayloadKinds.LibraryDirectoryName(kind));
        if (!Directory.Exists(sourceRoot))
        {
            return null;
        }

        List<string> files = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories).ToList();
        if (files.Count == 0)
        {
            return null;
        }

        string payloadsDirectory = Path.Combine(syncRoot, "payloads");
        Directory.CreateDirectory(payloadsDirectory);
        string archiveName = $"{snapshotId}-{kind}.zip";
        string archivePath = Path.Combine(payloadsDirectory, archiveName);
        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }

        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            foreach (string file in files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string fullPath = Path.GetFullPath(file);
                if (!SnapshotPublisher.IsPathInside(fullPath, sourceRoot))
                {
                    continue;
                }

                string relative = Path.GetRelativePath(sourceRoot, fullPath);
                archive.CreateEntryFromFile(fullPath, relative.Replace(Path.DirectorySeparatorChar, '/'),
                    CompressionLevel.Optimal);
            }
        }

        FileInfo info = new(archivePath);
        return new SnapshotFilePayload(
            Guid.NewGuid().ToString("D"),
            kind,
            Path.Combine("payloads", archiveName),
            info.Length,
            await SnapshotPublisher.Blake3FileAsync(archivePath));
    }

    /// <summary>
    /// Verifies and extracts every file payload of one manifest into the staging payloads root, then
    /// neutralizes imported session run ownership. <paramref name="resolvePayloadPath" /> returns the
    /// archive path for one root-relative file name, or <c>null</c> when the path is unsafe.
    /// Returns the extraction warnings.
    /// </summary>
    public static async Task<Result<IReadOnlyList<string>>> ExtractIntoStagingAsync(
        SnapshotManifest manifest,
        string syncRoot,
        string stagingPayloadsRoot,
        Func<string, string, string?> resolvePayloadPath,
        CancellationToken cancellationToken)
    {
        List<string> warnings = new();
        foreach (SnapshotFilePayload payload in manifest.FilePayloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? archivePath = resolvePayloadPath(syncRoot, payload.FileName);
            if (archivePath is null)
            {
                return Result<IReadOnlyList<string>>.Failure(AppErrorCodes.ValidationFailed,
                    $"Snapshot contains an unsafe file payload path: {payload.FileName}");
            }

            if (!File.Exists(archivePath))
            {
                return Result<IReadOnlyList<string>>.Failure(AppErrorCodes.NotFound,
                    $"Snapshot file payload is missing: {payload.FileName}");
            }

            FileInfo info = new(archivePath);
            if (info.Length != payload.SizeBytes ||
                !string.Equals(await SnapshotPublisher.Blake3FileAsync(archivePath), payload.Blake3,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Result<IReadOnlyList<string>>.Failure(AppErrorCodes.ValidationFailed,
                    $"Snapshot file payload failed content verification: {payload.FileName}");
            }

            string extractRoot = Path.Combine(stagingPayloadsRoot, payload.Kind);
            Directory.CreateDirectory(extractRoot);
            ExtractArchive(archivePath, extractRoot);
            if (payload.Kind == SnapshotFilePayloadKinds.AgentSessions)
            {
                NeutralizeImportedSessions(extractRoot);
            }
        }

        return Result<IReadOnlyList<string>>.Success(warnings);
    }

    /// <summary>
    /// Merges staged file payloads into a library directory (the parent of its runtime database). Used
    /// both when applying a snapshot to the active library and when keeping an incoming branch as a
    /// separate library copy. Returns the per-definition skip warnings.
    /// </summary>
    public static async Task<Result<IReadOnlyList<string>>> MergeStagedPayloadsAsync(
        string stagingPayloadsRoot,
        string targetLibraryDatabasePath,
        CancellationToken cancellationToken)
    {
        string? libraryRoot = Path.GetDirectoryName(Path.GetFullPath(targetLibraryDatabasePath));
        if (libraryRoot is null)
        {
            return Result<IReadOnlyList<string>>.Failure(AppErrorCodes.ValidationFailed,
                "The target library path must have a parent directory.");
        }

        List<string> warnings = new();
        string sessionsRoot = Path.Combine(stagingPayloadsRoot, SnapshotFilePayloadKinds.AgentSessions);
        if (Directory.Exists(sessionsRoot))
        {
            await CopyTreeAsync(sessionsRoot, Path.Combine(libraryRoot, "agent-sessions"), cancellationToken);
        }

        string workflowsRoot = Path.Combine(stagingPayloadsRoot, SnapshotFilePayloadKinds.Workflows);
        if (Directory.Exists(workflowsRoot))
        {
            warnings.AddRange(await MergeWorkflowsAsync(workflowsRoot,
                Path.Combine(libraryRoot, "workflows"),
                cancellationToken));
        }

        return Result<IReadOnlyList<string>>.Success(warnings);
    }

    private static async Task<IReadOnlyList<string>> MergeWorkflowsAsync(
        string stagedWorkflowsRoot,
        string targetWorkflowsRoot,
        CancellationToken cancellationToken)
    {
        List<string> warnings = new();
        foreach (string stagedDefinitionPath in Directory
                     .EnumerateFiles(stagedWorkflowsRoot, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string id = Path.GetFileNameWithoutExtension(stagedDefinitionPath);
            if (!IsSafeFileName(id))
            {
                warnings.Add($"Skipped workflow definition '{Path.GetFileName(stagedDefinitionPath)}': " +
                             "the file name is not a safe workflow id.");
                continue;
            }

            string json = await File.ReadAllTextAsync(stagedDefinitionPath, cancellationToken);
            if (IsLockedOrBuiltIn(json))
            {
                warnings.Add($"Skipped workflow definition '{id}': built-in and locked definitions are " +
                             "provided by the application and are never overwritten by a snapshot import.");
                continue;
            }

            string targetDefinitionPath = Path.Combine(targetWorkflowsRoot, id + ".json");
            if (File.Exists(targetDefinitionPath) && IsLockedOrBuiltIn(await File.ReadAllTextAsync(
                    targetDefinitionPath, cancellationToken)))
            {
                warnings.Add($"Skipped workflow definition '{id}': a locked definition with the same id " +
                             "already exists in this library.");
                continue;
            }

            Directory.CreateDirectory(targetWorkflowsRoot);
            await CopyFileAtomicAsync(stagedDefinitionPath, targetDefinitionPath, cancellationToken);

            string stagedScriptPath = Path.Combine(stagedWorkflowsRoot, ScriptsDirectoryName, id + ".fsx");
            if (File.Exists(stagedScriptPath))
            {
                string targetScriptsRoot = Path.Combine(targetWorkflowsRoot, ScriptsDirectoryName);
                Directory.CreateDirectory(targetScriptsRoot);
                await CopyFileAtomicAsync(stagedScriptPath, Path.Combine(targetScriptsRoot, id + ".fsx"),
                    cancellationToken);
            }
        }

        return warnings;
    }

    private static async Task CopyTreeAsync(string sourceRoot, string targetRoot, CancellationToken cancellationToken)
    {
        foreach (string sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fullPath = Path.GetFullPath(sourcePath);
            if (!SnapshotPublisher.IsPathInside(fullPath, sourceRoot))
            {
                continue;
            }

            string relative = Path.GetRelativePath(sourceRoot, fullPath);
            string targetPath = Path.GetFullPath(Path.Combine(targetRoot, relative));
            if (!SnapshotPublisher.IsPathInside(targetPath, Path.GetFullPath(targetRoot)))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await CopyFileAtomicAsync(fullPath, targetPath, cancellationToken);
        }
    }

    private static async Task CopyFileAtomicAsync(string sourcePath, string targetPath,
        CancellationToken cancellationToken)
    {
        string temporary = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (FileStream source = File.OpenRead(sourcePath))
            await using (FileStream target = File.Create(temporary))
            {
                await source.CopyToAsync(target, cancellationToken);
            }

            File.Move(temporary, targetPath, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void ExtractArchive(string archivePath, string extractRoot)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        string fullExtractRoot = Path.GetFullPath(extractRoot);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            string targetPath = Path.GetFullPath(Path.Combine(fullExtractRoot, entry.FullName));
            if (!SnapshotPublisher.IsPathInside(targetPath, fullExtractRoot))
            {
                throw new InvalidDataException(
                    $"Snapshot payload entry escapes its target directory: {entry.FullName}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, true);
        }
    }

    /// <summary>
    /// Rewrites every imported session still recorded as in flight to <c>Stopped</c>. Run ownership does
    /// not transfer across devices (ADR 0036), and the S5-A startup auto-resume only resumes
    /// <c>Running</c>/<c>AwaitingEffect</c> sessions, so neutralized imported sessions are never
    /// automatically re-executed. Unreadable state documents are left untouched.
    /// </summary>
    private static void NeutralizeImportedSessions(string extractedSessionsRoot)
    {
        foreach (string sessionDirectory in Directory.EnumerateDirectories(extractedSessionsRoot))
        {
            string statePath = Path.Combine(sessionDirectory, StateFileName);
            if (!File.Exists(statePath))
            {
                continue;
            }

            try
            {
                JsonNode? node = JsonNode.Parse(File.ReadAllText(statePath));
                if (node is not JsonObject state || !TryGetStatus(state, out string status) ||
                    status is not ("Running" or "AwaitingEffect"))
                {
                    continue;
                }

                state["status"] = "Stopped";
                File.WriteAllText(statePath, state.ToJsonString(CompactJson));
            }
            catch (Exception exception) when
                (exception is IOException or JsonException or UnauthorizedAccessException &&
                 UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.snapshot-file-payloads"))
            {
                // A corrupt imported session is skipped by the host store on open anyway; neutralization
                // must never break the import of the remaining library content.
            }
        }
    }

    private static bool TryGetStatus(JsonObject state, out string status)
    {
        status = string.Empty;
        JsonNode? node = state["status"];
        if (node is JsonValue value && value.TryGetValue<string>(out string? text) && text is not null)
        {
            status = text;
            return true;
        }

        return false;
    }

    private static bool IsLockedOrBuiltIn(string definitionJson)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(definitionJson);
            if (node is not JsonObject definition)
            {
                return false;
            }

            return IsTrue(definition, "builtIn") || IsTrue(definition, "locked");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsTrue(JsonObject definition, string property)
    {
        JsonNode? node = definition[property];
        return node is JsonValue value && value.TryGetValue(out bool flag) && flag;
    }

    private static bool IsSafeFileName(string name)
    {
        return name.Length > 0 && name.Length <= 128 && name is not ("." or "..") &&
               name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.Contains('/') &&
               !name.Contains('\\');
    }
}
