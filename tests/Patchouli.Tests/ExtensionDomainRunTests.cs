using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Extensions;
using Patchouli.Infrastructure.Bibliography.MetadataLookup;
using Patchouli.Llm;

namespace Patchouli.Tests;

[Trait("Category", "compiled-extension")]
public sealed class ExtensionDomainRunTests
{
    [Fact]
    public async Task Catalog_keeps_metadata_packages_lazy_and_activates_only_the_requested_source()
    {
        string runtimeDirectory = Path.Combine(Path.GetTempPath(), $"patchouli-metadata-lazy-{Guid.NewGuid():N}");
        try
        {
            await using TranslationExtensionCatalog catalog = new(runtimeDirectory,
                () => LlmAppSettings.Default(), TestExtensionResources.Unavailable());

            await catalog.InitializeAsync();

            catalog.Manager.List().Where(item => item.Id.StartsWith("metadata.", StringComparison.Ordinal))
                .Should().BeEmpty("metadata sources activate only for the identifier scheme being looked up");
            catalog.MetadataSources.Count(source => source.Definition.Id.Length > 0).Should()
                .Be(BuiltInMetadataExtensions.SourceDefinitions.Count,
                    "declarative definitions stay discoverable before first activation");
            catalog.MetadataSources.Select(source => source.Definition.Id).Should()
                .Contain(["crossref", "datacite", "cinii", "ndl", "calis"]);
            catalog.MetadataSources.Count(source => source.Definition.SupportedSchemes.Contains("ncid")).Should()
                .Be(1, "only CiNii declares NCID");

            await catalog.EnsureExtensionAsync("metadata.cinii");

            catalog.Manager.List().Should().Contain(item => item.Id == "metadata.cinii" && item.IsActive);
            catalog.Manager.List().Should().NotContain(item => item.Id == "metadata.datacite" && item.IsActive);
            catalog.MetadataSources.Should().Contain(source => source.Definition.Id == "datacite");
        }
        finally
        {
            if (Directory.Exists(runtimeDirectory))
            {
                Directory.Delete(runtimeDirectory, true);
            }
        }
    }


    [Fact]
    public async Task Compiled_ocr_run_keeps_its_kind_and_pinned_generation_after_activation_changes()
    {
        string runtimeDirectory = Path.Combine(Path.GetTempPath(), $"patchouli-domain-run-{Guid.NewGuid():N}");
        try
        {
            await using TranslationExtensionCatalog catalog = new(runtimeDirectory,
                () => LlmAppSettings.Default(), TestExtensionResources.Unavailable());
            // This case needs two real generations and a pinned old lease, not the bundled fleet.
            // catalog.AcquireDomainAsync would call InitializeAsync, which activates every bundled
            // translation package; the lease/config pinning part is replicated here instead so
            // the test keeps its real two-generation + old-lease pin without unrelated package init.
            // Production still goes through AcquireDomainAsync/ExtensionDomainService with full init.

            ExtensionPackageSnapshot first = OcrPackage("first-generation");
            ExtensionPackageSnapshot second = OcrPackage("second-generation");
            ExtensionCheckResult firstActivation = await catalog.ActivateAsync(first, CancellationToken.None);
            firstActivation.Succeeded.Should().BeTrue(string.Join(Environment.NewLine,
                firstActivation.Diagnostics.Select(issue =>
                    $"{issue.Code} {issue.Location.FileName}:{issue.Location.Line}:{issue.Location.Column}: {issue.Message}")));

            IExtensionLease lease = (await catalog.Manager.AcquireAsync(first.Id, CancellationToken.None))!;
            ExtensionConfigSnapshot resolved = ExtensionConfiguration.Resolve(lease.ConfigFields, lease.Config);
            await using ExtensionDomainRun run = new(lease,
                catalog.Resources.ForRun(resolved), resolved);
            run.Kind.Should().Be(ExtensionKind.Ocr);
            run.Fingerprint.Should().Be(first.Fingerprint);

            ExtensionCheckResult secondActivation = await catalog.ActivateAsync(second, CancellationToken.None);
            secondActivation.Succeeded.Should().BeTrue(string.Join(Environment.NewLine,
                secondActivation.Diagnostics.Select(issue =>
                    $"{issue.Code} {issue.Location.FileName}:{issue.Location.Line}:{issue.Location.Column}: {issue.Message}")));
            ExtensionPageIdentity page = new(DocumentInstanceId.New(), PageId.New(), DocumentTreeRevisionId.New(), 1);
            OcrInputId inputId = new("input-1");
            OcrExtensionContext input = new(page,
            [
                new ExtensionOcrInput(inputId, new NormalizedBBox(0, 0, 1, 1), "image/png",
                    new byte[] { 1, 2, 3 })
            ], new ExtensionConfigSnapshot(new Dictionary<string, string>(), 0));

            Core.Results.Result<OcrExtensionCandidate> result = await run.RecognizeAsync(input);

            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            result.Value.Boxes.Should().ContainSingle();
            result.Value.Boxes[0].Candidate.Payload.Should().Be(new TextBoxPayload("first-generation"));
        }
        finally
        {
            if (Directory.Exists(runtimeDirectory))
            {
                Directory.Delete(runtimeDirectory, true);
            }
        }
    }

    private static ExtensionPackageSnapshot OcrPackage(string marker)
    {
        string source = $$"""
                          open System
                          open System.Threading.Tasks
                          open Patchouli.Extensions.Contracts
                           open Patchouli.Core.Documents

                          /// <summary>Test OCR extension.</summary>
                          /// <patchouli-extension id="test.ocr" kind="ocr" version="1.0.0" host-api="patchouli.extensions/3">
                          /// <name>Test OCR</name>
                          /// </patchouli-extension>
                          let extension : IOcrExtension =
                              { new IOcrExtension with

                                  member _.ConfigFields = [||]
                                  member _.RecognizeAsync(context, _, _) =
                                      let input = context.Inputs.[0]
                                      Task.FromResult(
                                          OcrExtensionCandidate(context.Page,
                                              [| OcrExtensionBoxCandidate(input.Id, input.BBox,
                                                  LocalOcrCandidate("text", TextBoxPayload("{{marker}}"), Nullable<int>()),
                                                  Nullable<double>(0.9)) |])) }
                          """;
        return new ExtensionPackageSnapshot("test.ocr", new Dictionary<string, string>
        {
            ["main.fsx"] = source
        });
    }
}
