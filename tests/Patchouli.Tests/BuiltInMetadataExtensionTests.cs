using FluentAssertions;
using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Bibliography.MetadataLookup;
using Patchouli.Infrastructure.Translation;

namespace Patchouli.Tests;

/// <summary>
/// The activation theory stays on the real path because the asserted
/// <see cref="MetadataSourceDefinition"/> is produced by the script at runtime — a descriptor a fake
/// could return would not prove the package declares it. The declarative-coverage fact below is the
/// fast-layer counterpart: it asserts only what the bundled manifest lists, without activation.
/// </summary>
public sealed class BuiltInMetadataExtensionTests
{
    public static TheoryData<string> PackageIds
    {
        get
        {
            TheoryData<string> data = new();
            foreach (ExtensionPackageSnapshot package in BuiltInMetadataExtensions.Packages)
            {
                data.Add(package.Id);
            }

            return data;
        }
    }

    [Theory]
    [Trait("Category", "compiled-extension")]
    [MemberData(nameof(PackageIds))]
    public async Task Built_in_metadata_packages_activate_with_their_declared_source(string id)
    {
        ExtensionPackageSnapshot package = BuiltInMetadataExtensions.Packages.Single(item => item.Id == id);
        (string ExtensionId, MetadataSourceDefinition Definition) expected =
            BuiltInMetadataExtensions.SourceDefinitions.Single(item => item.ExtensionId == id);
        string directory = Path.Combine(Path.GetTempPath(), $"patchouli-metadata-activate-{Guid.NewGuid():N}");
        try
        {
            TestExtensionResources resources = TestExtensionResources.Unavailable();
            await using IExtensionManager manager = new ExtensionManager(directory, resources,
                new FileExtensionStore(directory), new CompiledExtensionGenerationLoader(directory));
            await BuiltInExtensionArtifacts.InstallAsync(directory, package, CancellationToken.None);
            ExtensionCheckResult activated = await manager.ActivateAsync(id, package, CancellationToken.None);
            activated.Succeeded.Should().BeTrue(string.Join(Environment.NewLine,
                activated.Diagnostics.Select(issue =>
                    $"{issue.Code} {issue.Location.FileName}:{issue.Location.Line}:{issue.Location.Column}: {issue.Message}")));

            await using IExtensionLease lease = (await manager.AcquireAsync(id, CancellationToken.None))!;
            lease.Metadata.Should().NotBeNull();
            lease.Metadata!.SourceDefinition.Id.Should().Be(expected.Definition.Id);
            lease.Metadata.SourceDefinition.SupportedSchemes.Should()
                .BeEquivalentTo(expected.Definition.SupportedSchemes);
            lease.Metadata.SourceDefinition.DefaultPriority.Should().Be(expected.Definition.DefaultPriority);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public void Built_in_metadata_definitions_declare_every_supported_identifier_scheme()
    {
        string[] expected =
        [
            "doi", "isbn", "pmid", "pmcid", "mid", "arxiv", "openalex", "mag", "semantic_scholar", "oclc", "lccn",
            "olid", "google_books", "jpno", "ndlbibid", "ndljp", "ncid", "naid", "crid", "dnb", "gnd",
            "ark", "frbnf"
        ];

        string[] declared = BuiltInMetadataExtensions.SourceDefinitions
            .SelectMany(entry => entry.Definition.SupportedSchemes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        declared.Should().BeEquivalentTo(expected);
    }
}
