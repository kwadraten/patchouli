using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Extensions;
using Patchouli.Llm;

namespace Patchouli.Tests;

public sealed class ExtensionDomainRunTests
{
    [Fact]
    public async Task Real_fsx_ocr_run_keeps_its_kind_and_pinned_generation_after_activation_changes()
    {
        string runtimeDirectory = Path.Combine(Path.GetTempPath(), $"patchouli-domain-run-{Guid.NewGuid():N}");
        try
        {
            await using TranslationExtensionCatalog catalog = new(runtimeDirectory,
                () => LlmAppSettings.Default(), new TestCapabilities());
            await catalog.InitializeAsync();

            ExtensionPackageSnapshot first = OcrPackage("first-generation");
            ExtensionPackageSnapshot second = OcrPackage("second-generation");
            ExtensionCheckResult firstActivation = await catalog.ActivateAsync(first, CancellationToken.None);
            firstActivation.Succeeded.Should().BeTrue(string.Join(Environment.NewLine,
                firstActivation.Diagnostics.Select(issue =>
                    $"{issue.Code} {issue.Location.FileName}:{issue.Location.Line}:{issue.Location.Column}: {issue.Message}")));

            await using ExtensionDomainRun run = await catalog.AcquireDomainAsync(first.Id);
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

                          let extension : IOcrExtension =
                              { new IOcrExtension with
                                  member _.Descriptor = ExtensionDescriptor("test.ocr", "Test OCR", "1.0", "", ExtensionKind.Ocr)
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
            ["extension.yml"] =
                "id: test.ocr\nversion: 1.0\nhost_api: patchouli.extensions/1\nkind: ocr\nentry: main.fsx",
            ["main.fsx"] = source
        });
    }

    private sealed class TestCapabilities : IExtensionCapabilities
    {
        public ILlmCapability Llm { get; } = new TestLlmCapability();
        public IHttpCapability Http { get; } = new TestHttpCapability();

        public IExtensionCapabilities ForRun(ExtensionConfigSnapshot config)
        {
            return this;
        }

        public Task WaitToProceedAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class TestLlmCapability : ILlmCapability
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LlmResponse(string.Empty));
        }
    }

    private sealed class TestHttpCapability : IHttpCapability
    {
        public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new HttpResponse(200, new Dictionary<string, string>(), "{}"));
        }
    }
}
