using FluentAssertions;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Extensions;
using Patchouli.Llm;

namespace Patchouli.Tests;

public sealed class ExtensionTemplateCreationTests
{
    [Theory]
    [InlineData("translation.google-free")]
    [InlineData("metadata.crossref")]
    [InlineData("ocr.ndlocr-lite")]
    [InlineData("pdf-text.native-xycut")]
    [InlineData("layout.pp-doclayout-v3")]
    public async Task Creation_copies_the_complete_bundled_template_without_activation_or_configuration(string id)
    {
        string root = Path.Combine(Path.GetTempPath(), $"patchouli-template-{Guid.NewGuid():N}");
        try
        {
            await using TranslationExtensionCatalog catalog = new(root, LlmAppSettings.Default,
                TestExtensionResources.Unavailable());
            ExtensionPackageSnapshot bundled = catalog.BundledPackageSnapshots.Single(package => package.Id == id);
            await catalog.Manager.SaveConfigAsync(id, new ExtensionConfigSnapshot(
                new Dictionary<string, string> { ["custom-setting"] = "original" }, 1));
            string newId = id + ".custom";

            ExtensionPackageSnapshot copy = await catalog.CreateCustomAsync(id, newId);

            ExtensionEntryMetadata.Parse(copy).Id.Should().Be(newId);
            copy.Sources.Keys.Should().BeEquivalentTo(bundled.Sources.Keys);
            copy.BinaryFiles.Keys.Should().BeEquivalentTo(bundled.BinaryFiles.Keys);
            copy.Sources["main.fsx"].Should().Be(
                ExtensionEntryMetadata.RewriteId(bundled, newId).Sources["main.fsx"]);
            foreach (string helper in bundled.Sources.Keys.Where(file => file != "main.fsx"))
            {
                copy.Sources[helper].Should().Be(bundled.Sources[helper]);
            }

            (await catalog.Manager.ReadSourceAsync(newId)).Fingerprint.Should().Be(copy.Fingerprint);
            (await catalog.Manager.ReadConfigAsync(newId)).Values.Should().BeEmpty();
            catalog.Manager.List().Should().BeEmpty("creation is a draft, not an activation");
            Directory.Exists(Path.Combine(root, ".compiled")).Should().BeFalse();
            File.ReadAllText(Path.Combine(root, ".drafts", newId)).Should().Be(copy.Fingerprint);

            await AssertIdRejectedAsync(catalog, id, newId);
            await AssertIdRejectedAsync(catalog, id, id.ToUpperInvariant());
            (await catalog.Manager.ReadSourceAsync(newId)).Fingerprint.Should().Be(copy.Fingerprint);
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryWithRetry(root);
        }
    }

    [Fact]
    public async Task Custom_template_keeps_helper_and_binary_payload_and_rejects_path_ids()
    {
        string root = Path.Combine(Path.GetTempPath(), $"patchouli-template-{Guid.NewGuid():N}");
        try
        {
            await using TranslationExtensionCatalog catalog = new(root, LlmAppSettings.Default,
                TestExtensionResources.Unavailable());
            ExtensionPackageSnapshot bundled = catalog.BundledPackageSnapshots.First();
            ExtensionPackageSnapshot template = ExtensionEntryMetadata.RewriteId(bundled, "custom.template");
            template = new ExtensionPackageSnapshot(template.Id, template.Sources,
                new Dictionary<string, ReadOnlyMemory<byte>> { ["lib/resource.bin"] = new byte[] { 1, 2, 3 } });
            await catalog.Manager.SavePackageAsync(template);

            ExtensionPackageSnapshot copy = await catalog.CreateCustomAsync(template.Id, "custom.copy");

            copy.BinaryFiles["lib/resource.bin"].ToArray().Should().Equal(1, 2, 3);
            await AssertIdRejectedAsync(catalog, template.Id, "../escape");
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryWithRetry(root);
        }
    }

    private static async Task AssertIdRejectedAsync(TranslationExtensionCatalog catalog, string templateId, string id)
    {
        bool rejected = false;
        try
        {
            await catalog.CreateCustomAsync(templateId, id);
        }
        catch (ArgumentException)
        {
            rejected = true;
        }

        rejected.Should().BeTrue();
    }
}
