using System.Text.Json;
using Patchouli.Core.Diagnostics;

namespace Patchouli.Host.Extensions;

/// <summary>
/// Issue #8 §6: one-shot adoption of the legacy per-library extension runtime at device scope.
/// The first library opened after the upgrade provides its <c>extension-runtime/</c> copy as the
/// device-global source under <c>&lt;DataDirectory&gt;/extensions/</c>; a marker file records the
/// completed migration so every later library never reads, merges, or deletes its own legacy
/// directory. The copy is intentionally a single startup pass without staging or an atomic swap:
/// the marker is written last, so an interrupted run simply retries on the next launch.
/// </summary>
internal static class DeviceExtensionRuntimeMigration
{
    internal const string RuntimeDirectoryName = "extensions";

    private const string LegacyDirectoryName = "extension-runtime";
    private const string MarkerFileName = ".device-migration.json";
    private const string LockFileName = ".device-migration.lock";
    private static readonly TimeSpan LockWaitLimit = TimeSpan.FromSeconds(60);

    public static string RuntimeRoot(string dataDirectory)
    {
        return Path.Combine(dataDirectory, RuntimeDirectoryName);
    }

    /// <summary>
    /// Returns the device-level runtime root, first copying the legacy library-scoped content when
    /// this device has not migrated yet. Always returns the device root; failures are logged and
    /// retried on the next launch rather than blocking host composition.
    /// </summary>
    public static string MigrateRuntime(string runtimeDatabasePath, string dataDirectory,
        IAppLogger logger, TimeSpan? lockWaitLimit = null)
    {
        string deviceRoot = RuntimeRoot(dataDirectory);
        try
        {
            string markerPath = Path.Combine(deviceRoot, MarkerFileName);
            if (File.Exists(markerPath))
            {
                return deviceRoot;
            }

            string legacyRoot = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(runtimeDatabasePath))!, LegacyDirectoryName);
            if (!Directory.Exists(legacyRoot) ||
                !Directory.EnumerateFiles(legacyRoot, "*", SearchOption.AllDirectories).Any())
            {
                return deviceRoot;
            }

            Directory.CreateDirectory(deviceRoot);
            using FileStream? migrationLock = TryAcquireLock(Path.Combine(deviceRoot, LockFileName),
                lockWaitLimit ?? LockWaitLimit);
            if (migrationLock is null)
            {
                Log(logger, "extension-migration",
                    $"Device extension migration is still running on another host; continuing with {deviceRoot} as-is.");
                return deviceRoot;
            }

            if (File.Exists(markerPath))
            {
                return deviceRoot;
            }

            int copied = CopyMissingFiles(legacyRoot, deviceRoot);
            File.WriteAllText(markerPath, JsonSerializer.Serialize(new
            {
                migratedFrom = legacyRoot,
                filesCopied = copied,
                migratedAtUtc = DateTimeOffset.UtcNow
            }));
            Log(logger, "migration",
                $"Extension runtime adopted device scope: {legacyRoot} -> {deviceRoot} ({copied} files).");
        }
        catch (Exception exception)
        {
            Log(logger, "extension-migration",
                $"Device extension migration failed and retries on the next launch: {exception.Message}");
        }

        return deviceRoot;
    }

    private static FileStream? TryAcquireLock(string lockPath, TimeSpan waitLimit)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + waitLimit;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    private static int CopyMissingFiles(string legacyRoot, string deviceRoot)
    {
        int copied = 0;
        foreach (string source in Directory.EnumerateFiles(legacyRoot, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(deviceRoot, Path.GetRelativePath(legacyRoot, source));
            if (File.Exists(destination))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
            copied++;
        }

        return copied;
    }

    private static void Log(IAppLogger logger, string operation, string message)
    {
        try
        {
            logger.LogAsync(operation, message).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine(exception);
        }
    }
}
