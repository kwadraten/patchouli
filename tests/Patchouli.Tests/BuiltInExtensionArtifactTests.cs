using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.FSharp.Core;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Bibliography.MetadataLookup;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Translation;

namespace Patchouli.Tests;

public sealed class BuiltInExtensionArtifactTests
{
    private static IReadOnlyList<ExtensionPackageSnapshot> Packages =>
        BuiltInTranslationExtensions.Packages
            .Concat(BuiltInMetadataExtensions.Packages)
            .Concat(BuiltInOcrExtensions.Packages)
            .Concat(BuiltInLayoutRecognitionExtensions.Packages)
            .Concat(BuiltInPdfTextLayerExtensions.Packages)
            .ToArray();

    public static TheoryData<string> PackageIds => new(Packages.Select(package => package.Id));

    [Fact]
    public void Runtime_packages_match_the_build_time_schema_and_embedded_archives()
    {
        Assembly assembly = typeof(BuiltInExtensionArtifacts).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(
            "Patchouli.Infrastructure.Extensions.BuiltInExtensionSchemas.json")!;
        using JsonDocument index = JsonDocument.Parse(stream);
        Dictionary<string, string> fingerprints = index.RootElement.GetProperty("packages")
            .EnumerateArray().ToDictionary(entry => entry.GetProperty("id").GetString()!,
                entry => entry.GetProperty("fingerprint").GetString()!, StringComparer.Ordinal);

        fingerprints.Keys.Should().BeEquivalentTo(Packages.Select(package => package.Id));
        foreach (ExtensionPackageSnapshot package in Packages)
        {
            fingerprints[package.Id].Should().Be(package.Fingerprint,
                $"{package.Id} must have the same identity during build and after deployment");
            assembly.GetManifestResourceNames().Should().Contain(
                $"Patchouli.Infrastructure.Extensions.Compiled.{package.Fingerprint}.zip",
                $"{package.Id} must ship the artifact requested by its runtime snapshot");
        }
    }

    [Theory]
    [MemberData(nameof(PackageIds))]
    public async Task Every_bundled_package_installs_a_valid_cache_hit_without_compilation(string id)
    {
        ExtensionPackageSnapshot package = Packages.Single(candidate => candidate.Id == id);
        string runtimeDirectory = Path.Combine(Path.GetTempPath(), $"patchouli-bundle-{Guid.NewGuid():N}");
        try
        {
            await BuiltInExtensionArtifacts.InstallAsync(runtimeDirectory, package);
            FSharpOption<ExtensionCompilationResult>? artifact = ExtensionArtifact.tryReadCached(package,
                Path.Combine(runtimeDirectory, ".compiled"), ExtensionArtifact.runtimeToolchainIdentity());
            artifact.Should().NotBeNull($"{id} must be usable on a machine without an extension compiler");
            artifact!.Value.CacheHit.Should().BeTrue();
            File.Exists(artifact.Value.AssemblyPath).Should().BeTrue();

            // Repeated acquisition must reuse the same installed artifact.
            await BuiltInExtensionArtifacts.InstallAsync(runtimeDirectory, package);
        }
        finally
        {
            if (Directory.Exists(runtimeDirectory))
            {
                Directory.Delete(runtimeDirectory, true);
            }
        }
    }
}
