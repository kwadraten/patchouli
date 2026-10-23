using FluentAssertions;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Tests;

public sealed class ExtensionBinarySnapshotTests
{
    [Theory]
    [InlineData("main.fsx", true)]
    [InlineData("fixtures/response.json", true)]
    [InlineData("lib/library.dll", false)]
    [InlineData("models/model.onnx", false)]
    [InlineData("data/archive.zip", false)]
    [InlineData("data/payload.bin", false)]
    [InlineData("data/unknown.format", false)]
    public void Unknown_package_formats_are_preserved_as_binary(string path, bool text)
    {
        ExtensionPackageSnapshot.IsTextSourcePath(path).Should().Be(text);
    }

    [Fact]
    public void Runtime_sdk_has_no_binary_reference_to_the_compiler_service()
    {
        typeof(CompiledExtensionGenerationLoader).Assembly.GetReferencedAssemblies()
            .Select(assembly => assembly.Name).Should().NotContain("FSharp.Compiler.Service");
    }

    [Fact]
    public async Task Working_packages_round_trip_binary_dependencies_without_text_decoding()
    {
        string root = Path.Combine(Path.GetTempPath(), "extension-binary-" + Guid.NewGuid().ToString("N"));
        try
        {
            ExtensionPackageSnapshot package = new("test.binary", new Dictionary<string, string>
            {
                ["main.fsx"] = "let extension = ()"
            }, new Dictionary<string, ReadOnlyMemory<byte>>
            {
                ["lib/library.dll"] = new byte[] { 0, 255, 128, 42 },
                ["models/model.onnx"] = new byte[] { 0, 255, 128, 42 },
                ["data/archive.zip"] = new byte[] { 0, 255, 128, 42 },
                ["data/payload.bin"] = new byte[] { 0, 255, 128, 42 }
            });
            IPackageExtensionFileStore store = new FileExtensionStore(root);

            await store.WritePackageAsync(package, CancellationToken.None);
            ExtensionPackageSnapshot loaded = await store.ReadPackageAsync(package.Id, CancellationToken.None);

            loaded.Fingerprint.Should().Be(package.Fingerprint);
            foreach (string path in package.BinaryFiles.Keys)
            {
                loaded.BinaryFiles[path].ToArray().Should().Equal(0, 255, 128, 42);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void Binary_dependencies_are_copied_and_participate_in_package_identity()
    {
        Dictionary<string, string> sources = new() { ["main.fsx"] = "let extension = ()" };
        byte[] bytes = [0, 255, 128, 42];
        ExtensionPackageSnapshot first = new("test.binary", sources,
            new Dictionary<string, ReadOnlyMemory<byte>> { ["lib/library.dll"] = bytes });
        bytes[0] = 99;
        ExtensionPackageSnapshot changed = new("test.binary", sources,
            new Dictionary<string, ReadOnlyMemory<byte>> { ["lib/library.dll"] = bytes });

        first.BinaryFiles["lib/library.dll"].ToArray().Should().Equal(0, 255, 128, 42);
        first.Fingerprint.Should().NotBe(changed.Fingerprint);
        first.Sources.Should().NotContainKey("lib/library.dll");
    }

    [Fact]
    public void Source_only_fingerprints_keep_the_existing_content_identity()
    {
        Dictionary<string, string> sources = new() { ["main.fsx"] = "let extension = ()" };
        ExtensionPackageSnapshot package = new("test.binary", sources);

        package.Fingerprint.Should().Be(PackageFingerprint.Compute(sources));
        package.BinaryFiles.Should().BeEmpty();
    }

    [Fact]
    public void Sources_and_binaries_cannot_claim_the_same_normalized_path()
    {
        Action create = () => _ = new ExtensionPackageSnapshot("test.binary",
            new Dictionary<string, string> { ["lib/library.dll"] = "not a binary" },
            new Dictionary<string, ReadOnlyMemory<byte>> { ["lib\\library.dll"] = new byte[] { 1 } });

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Copying_an_extension_keeps_its_fixed_binary_dependencies()
    {
        ExtensionPackageSnapshot original = new("test.binary", new Dictionary<string, string>
        {
            ["main.fsx"] = """
                           /// <summary>A package with a binary dependency.</summary>
                           /// <patchouli-extension id="test.binary" kind="translation" version="1.0.0" host-api="patchouli.extensions/3">
                           /// <name>Binary fixture</name>
                           /// </patchouli-extension>
                           let extension = ()
                           """
        }, new Dictionary<string, ReadOnlyMemory<byte>> { ["lib/library.dll"] = new byte[] { 0, 255 } });

        ExtensionPackageSnapshot copy = ExtensionEntryMetadata.RewriteId(original, "test.binary-copy");

        copy.BinaryFiles["lib/library.dll"].ToArray().Should().Equal(0, 255);
        ExtensionEntryMetadata.Parse(copy).Id.Should().Be("test.binary-copy");
    }
}
