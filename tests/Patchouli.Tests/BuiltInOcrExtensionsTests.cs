using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using FluentAssertions;
using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Ocr.Engines;
using Xunit;

namespace Patchouli.Tests;

/// <summary>
/// Built-in OCR package registry coverage: the four engine packages ship real embedded FSX
/// sources plus the genuine support binary closure (managed dependencies, ONNX Runtime and
/// SkiaSharp native libraries) under <c>lib/</c>, content-fingerprinted for the collectible
/// artifact loader. These tests assert delivery shape only — compiled-package activation is a
/// schema-generator/runtime gate, and real-weight quality stays behind the model gates.
/// </summary>
public sealed class BuiltInOcrExtensionsTests
{
    private static readonly string[] ExpectedPackageIds =
    [
        "ocr.rapidocr",
        "ocr.ndlocr-lite",
        "ocr.ndlkotenocr-lite",
        "ocr.multimodal"
    ];

    [Fact]
    public void Packages_exposes_all_four_ocr_packages()
    {
        BuiltInOcrExtensions.Packages.Select(static package => package.Id)
            .Should().BeEquivalentTo(ExpectedPackageIds);
    }

    [Fact]
    public void Each_package_ships_main_support_and_the_support_assembly()
    {
        foreach (ExtensionPackageSnapshot package in BuiltInOcrExtensions.Packages)
        {
            // The three native ONNX packages load the shared helper; the self-contained
            // multimodal package ships only its vision-owned main.fsx.
            string[] expectedSources = package.Id == "ocr.multimodal"
                ? ["main.fsx"]
                : ["main.fsx", "Support.fsx"];
            package.Sources.Keys.Should().BeEquivalentTo(expectedSources);
            package.BinaryFiles.Keys.Should().AllSatisfy(
                key => key.Should().StartWith("lib/"),
                $"{package.Id} ships every dependency package-locally under lib/");
            package.BinaryFiles.Keys.Should().AllSatisfy(key => ExtensionPackageSnapshot.IsTextSourcePath(key).Should()
                .BeFalse(
                    $"{package.Id} ships '{key}' as a binary payload, but textual paths must stay in Sources so the persisted generation round-trips the same partition"));
            package.BinaryFiles.Keys.Should().Contain("lib/" + NativeOcrSupportFiles.SupportAssemblyFileName,
                $"{package.Id}'s #r \"lib/{NativeOcrSupportFiles.SupportAssemblyFileName}\" must resolve package-locally");
            // The GapTree MIT notice travels as a comment in every package's main.fsx — the
            // support assembly carries the ported code, so the license stays package-local.
            package.Sources["main.fsx"].Should().Contain("MIT License").And.Contain("hiroi-sora");
        }
    }

    [Fact]
    public void The_support_binary_closure_is_real_and_dependency_complete()
    {
        ExtensionPackageSnapshot package = BuiltInOcrExtensions.Packages
            .Single(static p => p.Id == "ocr.rapidocr");

        // The support assembly and its non-framework managed dependencies are genuine PE images.
        package.BinaryFiles["lib/" + NativeOcrSupportFiles.SupportAssemblyFileName]
            .ToArray().Take(2).Should().Equal(0x4D, 0x5A);
        package.BinaryFiles.Keys.Should().Contain("lib/SkiaSharp.dll");
        package.BinaryFiles.Keys.Should().Contain("lib/Microsoft.ML.OnnxRuntime.dll");
        package.BinaryFiles.Should().AllSatisfy(pair => pair.Value.Length.Should().BeGreaterThan(0));

        // Host-resolved shared assemblies never ship: they would break collectible isolation.
        package.BinaryFiles.Keys.Should().NotContainMatch("*FSharp.Core*");
        package.BinaryFiles.Keys.Should().NotContainMatch("*Patchouli.Core*");
        package.BinaryFiles.Keys.Should().NotContainMatch("*Extensions.Contracts*");

        // The current platform's native runtimes ride along when the build produced them.
        package.BinaryFiles.Keys.Should().Contain(
            key => key.StartsWith("lib/onnxruntime", StringComparison.OrdinalIgnoreCase) ||
                   key.StartsWith("lib/libSkiaSharp", StringComparison.OrdinalIgnoreCase),
            "the OCR support runtime needs ONNX Runtime/Skia native libraries in the artifact");
    }

    [Fact]
    public void The_packaged_managed_closure_satisfies_every_transitive_reference()
    {
        ExtensionPackageSnapshot package = BuiltInOcrExtensions.Packages
            .Single(static p => p.Id == "ocr.rapidocr");
        HashSet<string> shipped = package.BinaryFiles.Keys
            .Where(static k => k.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(static k => Path.GetFileNameWithoutExtension(k["lib/".Length..]))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        string hostDirectory = Path.GetDirectoryName(typeof(BuiltInOcrExtensions).Assembly.Location)!;

        foreach (KeyValuePair<string, ReadOnlyMemory<byte>> file in package.BinaryFiles)
        {
            if (!file.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Read the packaged PE's real reference table: nothing may dangle onto an ambient
            // host binary the collectible loader could not resolve.
            using MemoryStream stream = new(file.Value.ToArray());
            using PEReader reader = new(stream);
            if (!reader.HasMetadata)
            {
                Path.GetFileName(file.Key).Should().Match(name =>
                    name.StartsWith("onnxruntime", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("libSkiaSharp", StringComparison.OrdinalIgnoreCase));
                continue;
            }

            MetadataReader metadata = reader.GetMetadataReader();
            foreach (AssemblyReferenceHandle handle in metadata.AssemblyReferences)
            {
                string name = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
                bool resolved = shipped.Contains(name)
                                || File.Exists(Path.Combine(runtimeDirectory, name + ".dll"))
                                || File.Exists(Path.Combine(hostDirectory, name + ".dll"));
                resolved.Should().BeTrue(
                    $"package member '{file.Key}' references '{name}', which must be packaged, framework-provided, or host-provided — never absent");
            }
        }
    }

    [Fact]
    public void Support_binaries_exist_before_packages_are_constructed()
    {
        // Regression pin for the static-init defect: CreatePackage must never observe an empty
        // binary map. Every package snapshot must already carry the closure at first access.
        BuiltInOcrExtensions.Packages.Should().AllSatisfy(package => package.BinaryFiles.Should().NotBeEmpty(
            $"{package.Id} was constructed after the support binaries were collected"));
    }

    [Fact]
    public void Native_main_scripts_reference_the_packaged_support_assembly()
    {
        foreach (string id in ExpectedPackageIds)
        {
            ExtensionPackageSnapshot package = BuiltInOcrExtensions.Packages.Single(p => p.Id == id);
            package.Sources["main.fsx"].Should().Contain(
                "#r \"lib/" + NativeOcrSupportFiles.SupportAssemblyFileName + "\"",
                $"{id} must compile against the shipped support assembly, not the host's");
        }
    }

    [Fact]
    public void Every_package_is_content_fingerprinted_and_per_package_distinct()
    {
        BuiltInOcrExtensions.Packages.Should()
            .AllSatisfy(package => package.Fingerprint.Should().MatchRegex("^[0-9a-f]{64}$"));
        BuiltInOcrExtensions.Packages.Select(static p => p.Fingerprint)
            .Should().OnlyHaveUniqueItems(
                "each package's main.fsx differs, so identical fingerprints would mean content is not hashed");
    }

    [Fact]
    public void Definitions_is_a_package_keyed_dictionary_with_deterministic_engine_ids()
    {
        IReadOnlyDictionary<string, OcrEngineDefinition> definitions = BuiltInOcrExtensions.Definitions;

        definitions.Keys.Should().BeEquivalentTo(ExpectedPackageIds);
        definitions["ocr.rapidocr"].EngineId.Should().Be(NativeOcrSession.RapidOcrEngineId);
        definitions["ocr.ndlocr-lite"].EngineId.Should().Be(NativeOcrSession.NdlOcrLiteEngineId);
        definitions["ocr.ndlkotenocr-lite"].EngineId.Should().Be(NativeOcrSession.NdlKotenLiteEngineId);
        definitions["ocr.multimodal"].EngineId.Should().Be("multimodal-llm");
        definitions.Values.Should().AllSatisfy(d => d.DisplayName.Should().NotBeNullOrWhiteSpace());

        // The native three come straight from the support facade; no duplicated metadata.
        definitions["ocr.rapidocr"]
            .Should().BeEquivalentTo(NativeOcrSession.GetDefinition(NativeOcrSession.RapidOcrEngineId));
    }
}
