using System.Text.Json;
using FluentAssertions;
using Patchouli.Core.Diagnostics;
using Patchouli.Host.Extensions;

namespace Patchouli.Tests;

/// <summary>
/// Issue #8 §6 device-scope adoption: the first library opened after upgrade donates its
/// <c>extension-runtime/</c> content to <c>&lt;DataDirectory&gt;/extensions/</c>, a marker records
/// completion, and later libraries' legacy directories stay untouched and unread.
/// </summary>
public sealed class DeviceExtensionRuntimeMigrationTests
{
    [Fact]
    public void First_library_runtime_is_adopted_as_device_scope_and_marked()
    {
        using TempDir temp = new();
        string dataDir = Path.Combine(temp.Path, "data");
        string databasePath = WriteLibraryRuntime(temp, "library-a",
        [
            ("extensions/ocr.demo/main.fsx", "let demo = 1"),
            ("extensions/ocr.demo/.generations/abcd/snapshot.fsx", "snapshot"),
            (".compiled/key-1/artifact.dll", "binary")
        ]);

        string root = DeviceExtensionRuntimeMigration.MigrateRuntime(databasePath, dataDir,
            new NoOpAppLogger());

        root.Should().Be(Path.Combine(dataDir, "extensions"));
        File.ReadAllText(Path.Combine(root, "extensions", "ocr.demo", "main.fsx")).Should()
            .Be("let demo = 1");
        File.ReadAllText(Path.Combine(root, "extensions", "ocr.demo", ".generations", "abcd",
            "snapshot.fsx")).Should().Be("snapshot");
        File.ReadAllText(Path.Combine(root, ".compiled", "key-1", "artifact.dll")).Should()
            .Be("binary");

        string marker = File.ReadAllText(Path.Combine(root, ".device-migration.json"));
        using JsonDocument markerJson = JsonDocument.Parse(marker);
        markerJson.RootElement.GetProperty("migratedFrom").GetString().Should()
            .Be(Path.Combine(Path.GetDirectoryName(databasePath)!, "extension-runtime"));
        markerJson.RootElement.GetProperty("filesCopied").GetInt32().Should().Be(3);
    }

    [Fact]
    public void Later_library_legacy_content_is_never_read_after_marker()
    {
        using TempDir temp = new();
        string dataDir = Path.Combine(temp.Path, "data");
        string firstDatabase = WriteLibraryRuntime(temp, "library-a",
            [("extensions/ocr.demo/main.fsx", "let demo = 1")]);
        DeviceExtensionRuntimeMigration.MigrateRuntime(firstDatabase, dataDir, new NoOpAppLogger());

        string secondDatabase = WriteLibraryRuntime(temp, "library-b",
            [("extensions/ocr.other/main.fsx", "let other = 2")]);

        string root = DeviceExtensionRuntimeMigration.MigrateRuntime(secondDatabase, dataDir,
            new NoOpAppLogger());

        root.Should().Be(Path.Combine(dataDir, "extensions"));
        File.Exists(Path.Combine(root, "extensions", "ocr.demo", "main.fsx")).Should().BeTrue();
        Directory.Exists(Path.Combine(root, "extensions", "ocr.other")).Should().BeFalse();
        // The losing library's own copy is left alone rather than merged or deleted.
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(secondDatabase)!, "extension-runtime",
            "extensions", "ocr.other", "main.fsx")).Should().Be("let other = 2");
    }

    [Fact]
    public void Absent_or_empty_legacy_runtime_is_not_marked_as_migrated()
    {
        using TempDir temp = new();
        string dataDir = Path.Combine(temp.Path, "data");
        string databasePath = Path.Combine(temp.Path, "library-a", "runtime.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        string root = DeviceExtensionRuntimeMigration.MigrateRuntime(databasePath, dataDir,
            new NoOpAppLogger());

        root.Should().Be(Path.Combine(dataDir, "extensions"));
        File.Exists(Path.Combine(root, ".device-migration.json")).Should().BeFalse();

        // An empty legacy directory carries nothing worth adopting and stays unmarked too.
        Directory.CreateDirectory(Path.Combine(temp.Path, "library-a", "extension-runtime"));
        DeviceExtensionRuntimeMigration.MigrateRuntime(databasePath, dataDir, new NoOpAppLogger())
            .Should().Be(root);
        File.Exists(Path.Combine(root, ".device-migration.json")).Should().BeFalse();
    }

    [Fact]
    public void Existing_device_files_are_preserved_during_adoption()
    {
        using TempDir temp = new();
        string dataDir = Path.Combine(temp.Path, "data");
        string deviceRoot = Path.Combine(dataDir, "extensions");
        Directory.CreateDirectory(Path.Combine(deviceRoot, "extensions", "ocr.demo"));
        File.WriteAllText(Path.Combine(deviceRoot, "extensions", "ocr.demo", "main.fsx"), "device");

        string databasePath = WriteLibraryRuntime(temp, "library-a",
        [
            ("extensions/ocr.demo/main.fsx", "legacy"),
            ("extensions/ocr.demo/Support.fsx", "shared")
        ]);

        DeviceExtensionRuntimeMigration.MigrateRuntime(databasePath, dataDir, new NoOpAppLogger());

        File.ReadAllText(Path.Combine(deviceRoot, "extensions", "ocr.demo", "main.fsx")).Should()
            .Be("device");
        File.ReadAllText(Path.Combine(deviceRoot, "extensions", "ocr.demo", "Support.fsx")).Should()
            .Be("shared");
        File.Exists(Path.Combine(deviceRoot, ".device-migration.json")).Should().BeTrue();
    }

    /// <summary>
    ///     Retired-format manifests are never adopted: the current entry-metadata reader
    ///     rejects <c>extension.yml</c> outright (EXT0014), so copying it would only break a
    ///     package the startup legacy-migration would then have to replace anyway.
    /// </summary>
    [Fact]
    public void Retired_extension_yml_files_are_never_copied_to_device_scope()
    {
        using TempDir temp = new();
        string dataDir = Path.Combine(temp.Path, "data");
        string databasePath = WriteLibraryRuntime(temp, "library-a",
        [
            ("extensions/translation.tencent-free/main.fsx", "let engine = \"legacy\""),
            ("extensions/translation.tencent-free/extension.yml",
                "id: translation.tencent-free\nversion: 1.0.0\nhost_api: patchouli.extensions/1\n"),
            ("extensions/translation.tencent-free/extension-state.json",
                "{\"schemaVersion\":\"1\",\"hostApi\":\"patchouli.extensions/1\"}")
        ]);

        DeviceExtensionRuntimeMigration.MigrateRuntime(databasePath, dataDir, new NoOpAppLogger())
            .Should().Be(Path.Combine(dataDir, "extensions"));

        string adopted = Path.Combine(dataDir, "extensions", "extensions",
            "translation.tencent-free");
        File.Exists(Path.Combine(adopted, "main.fsx")).Should().BeTrue();
        File.Exists(Path.Combine(adopted, "extension.yml")).Should().BeFalse();
        File.Exists(Path.Combine(adopted, "extension-state.json")).Should().BeTrue(
            "activation state itself is ordinary data; the startup migration filters it");
    }

    [Fact]
    public void Held_migration_lock_skips_adoption_and_leaves_retry_unmarked()
    {
        using TempDir temp = new();
        string dataDir = Path.Combine(temp.Path, "data");
        string deviceRoot = Path.Combine(dataDir, "extensions");
        Directory.CreateDirectory(deviceRoot);
        string databasePath = WriteLibraryRuntime(temp, "library-a",
            [("extensions/ocr.demo/main.fsx", "let demo = 1")]);

        using (new FileStream(Path.Combine(deviceRoot, ".device-migration.lock"), FileMode.OpenOrCreate,
                   FileAccess.ReadWrite, FileShare.None))
        {
            DeviceExtensionRuntimeMigration.MigrateRuntime(databasePath, dataDir, new NoOpAppLogger(),
                    TimeSpan.FromMilliseconds(300))
                .Should().Be(deviceRoot);
        }

        Directory.Exists(Path.Combine(deviceRoot, "extensions", "ocr.demo")).Should().BeFalse();
        File.Exists(Path.Combine(deviceRoot, ".device-migration.json")).Should().BeFalse();

        // Once the other host releases the lock the adoption completes normally.
        DeviceExtensionRuntimeMigration.MigrateRuntime(databasePath, dataDir, new NoOpAppLogger(),
            TimeSpan.FromSeconds(5)).Should().Be(deviceRoot);
        File.Exists(Path.Combine(deviceRoot, "extensions", "ocr.demo", "main.fsx")).Should().BeTrue();
    }

    private static string WriteLibraryRuntime(TempDir temp, string libraryDirectory,
        IReadOnlyList<(string RelativePath, string Content)> files)
    {
        string libraryRoot = Path.Combine(temp.Path, libraryDirectory);
        string runtimeRoot = Path.Combine(libraryRoot, "extension-runtime");
        foreach ((string relativePath, string content) in files)
        {
            string path = Path.Combine(runtimeRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        return Path.Combine(libraryRoot, "runtime.sqlite");
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"patchouli-ext-migration-{Guid.NewGuid():N}")).FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class NoOpAppLogger : IAppLogger
    {
        public Task LogAsync(string operation, string message)
        {
            return Task.CompletedTask;
        }
    }
}
