using Microsoft.Data.Sqlite;
using FluentAssertions;
using Patchouli.UI;

namespace Patchouli.Tests;

/// <summary>
/// Provides one fully isolated persisted-settings profile for a UI test.
/// </summary>
public sealed class TemporaryAppSettingsFile : IDisposable
{
    private readonly string _root;

    public TemporaryAppSettingsFile()
    {
        _root = Directory.CreateDirectory(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"patchouli-test-{Guid.NewGuid():N}"))
            .FullName;
        Path = System.IO.Path.Combine(_root, "settings.json");

        AppRuntimeOptions runtime = PatchouliAppSettings.Default().Runtime with
        {
            RuntimeDatabasePath = System.IO.Path.Combine(_root, "runtime.sqlite"),
            DefaultSyncRoot = System.IO.Path.Combine(_root, "sync"),
            DefaultStagingRoot = System.IO.Path.Combine(_root, "staging"),
            LogDirectory = System.IO.Path.Combine(_root, "logs"),
            FileSearchRoot = System.IO.Path.Combine(_root, "search"),
            UseMockOcrOnly = true
        };
        (PatchouliAppSettings.Default() with { Runtime = runtime }).Save(Path).IsSuccess.Should().BeTrue();
    }

    public string Path { get; }

    public string CreateDatabasePath(string prefix)
    {
        return System.IO.Path.Combine(_root, $"{prefix}-{Guid.NewGuid():N}.sqlite");
    }

    public void Dispose()
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, true);
                }

                return;
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(50);
            }
        }
    }
}
