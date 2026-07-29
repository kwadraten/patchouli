using System.Collections.Concurrent;

namespace Patchouli.Core.Settings;

/// <summary>Process-wide serialization boundary for read-modify-write updates to one device settings file.</summary>
public static class SettingsFileWriteCoordinator
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    public static SemaphoreSlim ForPath(string path)
    {
        return Gates.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
    }
}
