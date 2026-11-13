using FluentAssertions;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Extensions;
using Patchouli.Infrastructure.Bibliography.MetadataLookup;
using Patchouli.Infrastructure.Translation;
using Patchouli.Llm;

namespace Patchouli.Tests;

/// <summary>
///     Built-in extension packages change between releases; a working copy or persisted generation
///     that is byte-identical to a previously shipped package carries no user work, so the catalog
///     adopts the current bundled package instead of re-activating the retired implementation. The
///     fixtures under <c>Fixtures/Extensions/translation.tencent-free-0.3.15</c> are the exact
///     sources shipped with release 0.3.15.
/// </summary>
[Trait("Category", "compiled-extension")]
public sealed class BuiltInExtensionUpgradeTests
{
    private const string EngineId = "translation.tencent-free";

    private const string MetadataEngineId = "metadata.semantic-scholar";

    private const string LegacyFingerprint =
        "9cb4bc5d19c6664462e859b2662d86c48f7be994bd750bf52351cab9daec26a8";

    private static readonly string FixtureDirectory =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Extensions", "translation.tencent-free-0.3.15");

    private static ExtensionPackageSnapshot Bundled =>
        BuiltInTranslationExtensions.Packages.Single(package => package.Id == EngineId);

    private static ExtensionPackageSnapshot MetadataBundled =>
        BuiltInMetadataExtensions.Packages.Single(package => package.Id == MetadataEngineId);

    /// <summary>
    ///     The semantic-scholar package as shipped with release 0.3.16: version 1.1.0 with the
    ///     pre-fix Support.fsx (no "journalarticle" arm). Built from the current bundled sources
    ///     by reversing the two changes, so the fixture stays in sync with the bundled package
    ///     shape instead of freezing a copy of the old sources.
    /// </summary>
    private static ExtensionPackageSnapshot LegacyMetadataPackage()
    {
        ExtensionPackageSnapshot bundled = MetadataBundled;
        string main = bundled.Sources["main.fsx"].Replace("version=\"1.1.1\"", "version=\"1.1.0\"");
        string support = bundled.Sources["Support.fsx"]
            .Replace("| \"journal-article\" | \"article\" | \"article-journal\" | \"journalarticle\"",
                "| \"journal-article\" | \"article\" | \"article-journal\"");
        return new ExtensionPackageSnapshot(MetadataEngineId, new Dictionary<string, string>
        {
            ["main.fsx"] = main,
            ["Support.fsx"] = support
        });
    }

    [Fact]
    public async Task Ensure_upgrades_a_0_3_16_metadata_package_to_the_bundled_1_1_1_package()
    {
        string runtimeDirectory = NewRuntimeDirectory();
        try
        {
            ExtensionPackageSnapshot legacy = LegacyMetadataPackage();
            legacy.Fingerprint.Should().NotBe(MetadataBundled.Fingerprint,
                "the 1.1.0 package must differ from the bundled 1.1.1 package in content");
            legacy.Sources["Support.fsx"].Should().NotContain("journalarticle",
                "the fixture must carry the pre-fix Support.fsx");

            await using (TranslationExtensionCatalog previous = new(runtimeDirectory,
                             () => LlmAppSettings.Default(), TestExtensionResources.Unavailable()))
            {
                ExtensionCheckResult seeded = await previous.ActivateAsync(legacy, CancellationToken.None);
                seeded.Succeeded.Should().BeTrue(string.Join("; ",
                    seeded.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
                previous.Manager.List().Should().Contain(item =>
                    item.Id == MetadataEngineId && item.Fingerprint == legacy.Fingerprint);
            }

            await using TranslationExtensionCatalog catalog = new(runtimeDirectory,
                () => LlmAppSettings.Default(), TestExtensionResources.Unavailable());
            await catalog.EnsureExtensionAsync(MetadataEngineId);

            catalog.Manager.List().Should().Contain(item =>
                    item.Id == MetadataEngineId && item.Fingerprint == MetadataBundled.Fingerprint,
                "a 1.1.0 installation upgrades to the bundled 1.1.1 package instead of " +
                "throwing a same-version content conflict or keeping the old snapshot");
            File.ReadAllText(Path.Combine(runtimeDirectory, "extensions", MetadataEngineId, "main.fsx"))
                .Should().Be(MetadataBundled.Sources["main.fsx"]);
            File.ReadAllText(Path.Combine(runtimeDirectory, "extensions", MetadataEngineId, "Support.fsx"))
                .Should().Contain("journalarticle",
                    "the upgraded working copy carries the mapType fix");
        }
        finally
        {
            DeleteRuntimeDirectory(runtimeDirectory);
        }
    }

    [Fact]
    public async Task Ensure_replaces_a_pristine_legacy_working_copy_with_the_current_bundled_package()
    {
        string runtimeDirectory = NewRuntimeDirectory();
        try
        {
            WriteLegacyWorkingCopy(runtimeDirectory);

            await using TranslationExtensionCatalog catalog = new(runtimeDirectory,
                () => LlmAppSettings.Default(), TestExtensionResources.Unavailable());
            await catalog.EnsureExtensionAsync(EngineId);

            catalog.Manager.List().Should().Contain(item =>
                    item.Id == EngineId && item.Fingerprint == Bundled.Fingerprint,
                "a byte-identical copy of a previously shipped package carries no user work to lose");
            File.ReadAllText(Path.Combine(runtimeDirectory, "extensions", EngineId, "main.fsx"))
                .Should().Be(Bundled.Sources["main.fsx"], "the working copy is resynced to bundled source");
            File.ReadAllText(Path.Combine(runtimeDirectory, "extensions", EngineId,
                "extension-state.json")).Should().Contain(Bundled.Fingerprint);
        }
        finally
        {
            DeleteRuntimeDirectory(runtimeDirectory);
        }
    }

    [Fact]
    public async Task Ensure_replaces_a_restored_legacy_generation_with_the_current_bundled_package()
    {
        string runtimeDirectory = NewRuntimeDirectory();
        try
        {
            ExtensionPackageSnapshot legacy = LegacyPackage();
            legacy.Fingerprint.Should().Be(LegacyFingerprint,
                "the fixture must stay a fingerprint-listed legacy package");

            await using (TranslationExtensionCatalog previous = new(runtimeDirectory,
                             () => LlmAppSettings.Default(), TestExtensionResources.Unavailable()))
            {
                ExtensionCheckResult seeded = await previous.ActivateAsync(legacy, CancellationToken.None);
                seeded.Succeeded.Should().BeTrue(string.Join("; ",
                    seeded.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
                previous.Manager.List().Should().Contain(item =>
                    item.Id == EngineId && item.Fingerprint == LegacyFingerprint);
            }

            await using TranslationExtensionCatalog catalog = new(runtimeDirectory,
                () => LlmAppSettings.Default(), TestExtensionResources.Unavailable());
            await catalog.EnsureExtensionAsync(EngineId);

            catalog.Manager.List().Should().Contain(item =>
                    item.Id == EngineId && item.Fingerprint == Bundled.Fingerprint,
                "a restored legacy generation upgrades to the new bundled package after app update");
            File.ReadAllText(Path.Combine(runtimeDirectory, "extensions", EngineId, "main.fsx"))
                .Should().Be(Bundled.Sources["main.fsx"]);
        }
        finally
        {
            DeleteRuntimeDirectory(runtimeDirectory);
        }
    }

    [Fact]
    public async Task Ensure_upgrades_and_archives_a_user_modified_old_built_in_working_copy()
    {
        string runtimeDirectory = NewRuntimeDirectory();
        try
        {
            WriteLegacyWorkingCopy(runtimeDirectory, "main.fsx", content =>
                content + "\n// user customization marker\n");

            await using TranslationExtensionCatalog catalog = new(runtimeDirectory,
                () => LlmAppSettings.Default(), TestExtensionResources.Unavailable());
            ExtensionPackageSnapshot oldSource = await catalog.Manager.ReadSourceAsync(EngineId);
            await catalog.EnsureExtensionAsync(EngineId);

            ExtensionGenerationInfo active = catalog.Manager.List().Single(item => item.Id == EngineId);
            active.Fingerprint.Should().Be(Bundled.Fingerprint,
                "the higher SemVer built-in replaces an older same-ID implementation");
            File.ReadAllText(Path.Combine(runtimeDirectory, "extensions", EngineId, "main.fsx"))
                .Should().Be(Bundled.Sources["main.fsx"]);
            File.ReadAllText(Path.Combine(runtimeDirectory, "extensions", EngineId, ".generations",
                    oldSource.Fingerprint, "main.fsx"))
                .Should().Contain("user customization marker");
        }
        finally
        {
            DeleteRuntimeDirectory(runtimeDirectory);
        }
    }

    /// <summary>
    ///     Release-1.0.0 working copies predate the XML metadata declaration: they carry
    ///     <c>extension.yml</c>, a <c>main.fsx</c> without <c>&lt;patchouli-extension&gt;</c>,
    ///     and a state file that recorded <c>patchouli.extensions/1</c>. The versioned
    ///     upgrade in <c>EnsureExtensionAsync</c> never reaches them — the metadata read
    ///     throws EXT0014 first — so the startup migration replaces the whole directory
    ///     (keeping only <c>config.json</c>) before any listing or activation runs.
    /// </summary>
    [Fact]
    public async Task Startup_migration_replaces_a_retired_format_working_copy_and_keeps_its_config()
    {
        string runtimeDirectory = NewRuntimeDirectory();
        try
        {
            WriteRetiredFormatWorkingCopy(runtimeDirectory);

            await using TranslationExtensionCatalog catalog = new(runtimeDirectory,
                () => LlmAppSettings.Default(), TestExtensionResources.Unavailable());
            await catalog.MigrateLegacyBuiltInTranslationPackagesAsync();

            string directory = Path.Combine(runtimeDirectory, "extensions", EngineId);
            File.Exists(Path.Combine(directory, "extension.yml")).Should().BeFalse(
                "the retired manifest is removed, not merely ignored");
            File.Exists(Path.Combine(directory, "extension-state.json")).Should().BeFalse(
                "the 1.0.0 state must not resurrect the old generation");
            Directory.Exists(Path.Combine(directory, ".generations")).Should().BeFalse(
                "retired generation snapshots belong to the 1.0.0 format and are discarded");
            File.ReadAllText(Path.Combine(directory, "main.fsx"))
                .Should().Be(Bundled.Sources["main.fsx"]);
            File.ReadAllText(Path.Combine(directory, "Support.fsx"))
                .Should().Be(Bundled.Sources["Support.fsx"]);
            ExtensionEntryMetadata.Parse(
                    await catalog.Manager.ReadSourceAsync(EngineId, CancellationToken.None))
                .Name.Should().Be(
                    ExtensionEntryMetadata.Parse(Bundled).Name,
                    "the repaired copy declares the current XML metadata instead of EXT0014");
            (await catalog.Manager.ReadConfigAsync(EngineId, CancellationToken.None))
                .Values["target-language"].Should().Be("zh-Hant",
                    "user configuration survives the format migration verbatim");

            // The repaired copy is a normal bundled package: the ordinary upgrade path now
            // activates it instead of throwing before the fingerprint comparison.
            await catalog.EnsureExtensionAsync(EngineId);
            catalog.Manager.List().Should().ContainSingle(item =>
                    item.Id == EngineId && item.Fingerprint == Bundled.Fingerprint,
                "after migration the bundled generation activates normally");
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryForcingExtensionUnload(runtimeDirectory);
        }
    }

    [Fact]
    public async Task Startup_migration_detects_a_retired_state_without_extension_yml()
    {
        string runtimeDirectory = NewRuntimeDirectory();
        try
        {
            WriteRetiredFormatWorkingCopy(runtimeDirectory);
            string directory = Path.Combine(runtimeDirectory, "extensions", EngineId);
            File.Delete(Path.Combine(directory, "extension.yml"));

            await using TranslationExtensionCatalog catalog = new(runtimeDirectory,
                () => LlmAppSettings.Default(), TestExtensionResources.Unavailable());
            await catalog.MigrateLegacyBuiltInTranslationPackagesAsync();

            File.ReadAllText(Path.Combine(directory, "main.fsx"))
                .Should().Be(Bundled.Sources["main.fsx"],
                    "the unsupported hostApi in extension-state.json alone flags the copy");
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryForcingExtensionUnload(runtimeDirectory);
        }
    }

    [Fact]
    public async Task Startup_migration_leaves_modern_copies_and_config_overlays_untouched()
    {
        string runtimeDirectory = NewRuntimeDirectory();
        try
        {
            string overlay = Path.Combine(runtimeDirectory, "extensions", "translation.llm");
            Directory.CreateDirectory(overlay);
            File.WriteAllText(Path.Combine(overlay, "config.json"),
                "{\"Values\":{\"target-language\":\"zh-Hans\"},\"Revision\":2}");
            WriteLegacyWorkingCopy(runtimeDirectory);

            await using TranslationExtensionCatalog catalog = new(runtimeDirectory,
                () => LlmAppSettings.Default(), TestExtensionResources.Unavailable());
            await catalog.MigrateLegacyBuiltInTranslationPackagesAsync();

            // The XML-metadata legacy upgrade fixture is not the retired 1.0.0 format: it
            // stays in place for the versioned EnsureExtensionAsync upgrade to handle.
            File.ReadAllText(Path.Combine(runtimeDirectory, "extensions", EngineId, "main.fsx"))
                .Should().Be(File.ReadAllText(Path.Combine(FixtureDirectory, "main.fsx")),
                    "a current-format working copy is never deleted");
            // A config-only overlay keeps its seed; no package files are synthesized for it.
            string[] overlayFiles = Directory.GetFiles(overlay);
            overlayFiles.Should().BeEquivalentTo([Path.Combine(overlay, "config.json")]);
        }
        finally
        {
            DeleteRuntimeDirectory(runtimeDirectory);
        }
    }

    [Fact]
    public async Task Upgrade_keeps_old_leases_configuration_and_explicit_lkg_recovery()
    {
        string root = NewRuntimeDirectory();
        try
        {
            await using TranslationExtensionCatalog catalog = new(root, LlmAppSettings.Default,
                TestExtensionResources.Unavailable());
            ExtensionPackageSnapshot old = LegacyPackage();
            (await catalog.ActivateAsync(old)).Succeeded.Should().BeTrue();
            await catalog.Manager.SaveConfigAsync(EngineId, new ExtensionConfigSnapshot(
                new Dictionary<string, string> { ["retained"] = "user-setting" }, 7), CancellationToken.None);
            (await catalog.Manager.PromoteLastKnownGoodAsync(EngineId, CancellationToken.None)).Should().BeTrue();
            await using IExtensionLease oldLease =
                (await catalog.Manager.AcquireAsync(EngineId, CancellationToken.None))!;

            await catalog.EnsureExtensionAsync(EngineId);

            catalog.Manager.List().Single().Fingerprint.Should().Be(Bundled.Fingerprint);
            oldLease.Fingerprint.Should().Be(old.Fingerprint);
            oldLease.Descriptor.HostApi.Should().Be(ExtensionHostApi.Legacy);
            (await catalog.Manager.ReadConfigAsync(EngineId, CancellationToken.None)).Values["retained"]
                .Should().Be("user-setting");
            (await catalog.RollbackAsync(EngineId)).Should().BeTrue();
            await catalog.EnsureExtensionAsync(EngineId);
            catalog.Manager.List().Single().Fingerprint.Should().Be(old.Fingerprint,
                "automatic admission must not immediately undo an explicit recovery choice");
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryForcingExtensionUnload(root);
        }
    }

    [Theory]
    [InlineData("1.10.0", false)]
    [InlineData("1.1.0", true)]
    [InlineData("1.1.0+alternate", true)]
    public async Task Newer_versions_are_retained_and_equal_precedence_conflicts_are_rejected(string version,
        bool conflict)
    {
        string root = NewRuntimeDirectory();
        try
        {
            await using TranslationExtensionCatalog catalog = new(root, LlmAppSettings.Default,
                TestExtensionResources.Unavailable());
            ExtensionPackageSnapshot installed = new(EngineId, Bundled.Sources.ToDictionary(pair => pair.Key,
                pair => pair.Key == "main.fsx"
                    ? pair.Value.Replace("version=\"1.1.0\"", $"version=\"{version}\"") + "\n// different content"
                    : pair.Value));
            await catalog.Manager.SavePackageAsync(installed, CancellationToken.None);
            if (conflict)
            {
                InvalidOperationException? failure = null;
                try
                {
                    await catalog.EnsureExtensionAsync(EngineId);
                }
                catch (InvalidOperationException error)
                {
                    failure = error;
                }

                failure.Should().NotBeNull();
                failure!.Message.Should().Contain("conflicting");
                catalog.Manager.List().Should().BeEmpty();
            }
            else
            {
                await catalog.EnsureExtensionAsync(EngineId);
                catalog.Manager.List().Single().Fingerprint.Should().Be(installed.Fingerprint);
            }

            (await catalog.Manager.ReadSourceAsync(EngineId, CancellationToken.None)).Fingerprint
                .Should().Be(installed.Fingerprint);
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryForcingExtensionUnload(root);
        }
    }

    private static ExtensionPackageSnapshot LegacyPackage()
    {
        return new ExtensionPackageSnapshot(EngineId, new Dictionary<string, string>
        {
            ["main.fsx"] = File.ReadAllText(Path.Combine(FixtureDirectory, "main.fsx")),
            ["Support.fsx"] = File.ReadAllText(Path.Combine(FixtureDirectory, "Support.fsx"))
        });
    }

    private static void WriteLegacyWorkingCopy(string runtimeDirectory,
        string? rewriteFile = null, Func<string, string>? rewrite = null)
    {
        string target = Path.Combine(runtimeDirectory, "extensions", EngineId);
        Directory.CreateDirectory(target);
        foreach (string name in new[] { "main.fsx", "Support.fsx" })
        {
            string content = File.ReadAllText(Path.Combine(FixtureDirectory, name));
            if (name == rewriteFile && rewrite is not null)
            {
                content = rewrite(content);
            }

            File.WriteAllText(Path.Combine(target, name), content);
        }
    }

    /// <summary>
    ///     Writes the directory shape the 1.0.0 release left behind: the retired
    ///     <c>extension.yml</c> manifest, a <c>main.fsx</c> without the XML declaration, a
    ///     <c>patchouli.extensions/1</c> state record, persisted generation snapshots and a
    ///     user <c>config.json</c>.
    /// </summary>
    private static void WriteRetiredFormatWorkingCopy(string runtimeDirectory)
    {
        string target = Path.Combine(runtimeDirectory, "extensions", EngineId);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "extension.yml"),
            "id: translation.tencent-free\nversion: 1.0.0\nhost_api: patchouli.extensions/1\n");
        File.WriteAllText(Path.Combine(target, "main.fsx"), "let engine = \"legacy\"\n");
        File.WriteAllText(Path.Combine(target, "Support.fsx"), "// retired shared support\n");
        File.WriteAllText(Path.Combine(target, "extension-state.json"),
            "{\"schemaVersion\":\"1\",\"hostApi\":\"patchouli.extensions/1\"," +
            $"\"activeFingerprint\":\"{LegacyFingerprint}\"}}");
        File.WriteAllText(Path.Combine(target, "config.json"),
            "{\"Values\":{\"target-language\":\"zh-Hant\"},\"Revision\":3}");
        string generation = Path.Combine(target, ".generations", LegacyFingerprint);
        Directory.CreateDirectory(generation);
        File.WriteAllText(Path.Combine(generation, "main.fsx"), "let engine = \"legacy\"\n");
    }

    private static string NewRuntimeDirectory()
    {
        return Path.Combine(Path.GetTempPath(), $"patchouli-upgrade-{Guid.NewGuid():N}");
    }

    private static void DeleteRuntimeDirectory(string runtimeDirectory)
    {
        if (Directory.Exists(runtimeDirectory))
        {
            Directory.Delete(runtimeDirectory, true);
        }
    }
}
