using FluentAssertions;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Extensions;
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

    private const string LegacyFingerprint =
        "9cb4bc5d19c6664462e859b2662d86c48f7be994bd750bf52351cab9daec26a8";

    private static readonly string FixtureDirectory =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Extensions", "translation.tencent-free-0.3.15");

    private static ExtensionPackageSnapshot Bundled =>
        BuiltInTranslationExtensions.Packages.Single(package => package.Id == EngineId);

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
    public async Task Ensure_preserves_a_user_modified_working_copy()
    {
        string runtimeDirectory = NewRuntimeDirectory();
        try
        {
            WriteLegacyWorkingCopy(runtimeDirectory, "main.fsx", content =>
                content + "\n// user customization marker\n");

            await using TranslationExtensionCatalog catalog = new(runtimeDirectory,
                () => LlmAppSettings.Default(), TestExtensionResources.Unavailable());
            await catalog.EnsureExtensionAsync(EngineId);

            ExtensionGenerationInfo active = catalog.Manager.List().Single(item => item.Id == EngineId);
            active.Fingerprint.Should().NotBe(Bundled.Fingerprint,
                "a fingerprint outside the legacy manifest is user work and must be preserved");
            File.ReadAllText(Path.Combine(runtimeDirectory, "extensions", EngineId, "main.fsx"))
                .Should().Contain("user customization marker");
        }
        finally
        {
            DeleteRuntimeDirectory(runtimeDirectory);
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
