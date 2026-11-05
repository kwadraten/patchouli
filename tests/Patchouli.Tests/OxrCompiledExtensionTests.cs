using FluentAssertions;
using Patchouli.Core.Credentials;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Extensions;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Llm;
using Xunit;

namespace Patchouli.Tests;

public sealed class OxrCompiledExtensionTests
{
    [Fact]
    [Trait("Category", "compiled-extension")]
    public async Task Built_in_oxr_package_runs_local_ocr_job_without_network()
    {
        string runtimeDirectory = Path.Combine(
            TestPaths.FromRepositoryRoot(".tmp", "tests", "oxr-compiled"),
            Guid.NewGuid().ToString("N"));
        try
        {
            ExtensionPackageSnapshot package = BuiltInOcrExtensions.Packages.Single(p => p.Id == "ocr.oxr");
            HostExtensionResources resources = new(() => LlmAppSettings.Default(), new DenyCredentials(),
                new HttpMessageInvoker(new DenyNetwork()));
            await using IExtensionManager manager = new ExtensionManager(runtimeDirectory, resources,
                new FileExtensionStore(runtimeDirectory), new CompiledExtensionGenerationLoader(runtimeDirectory));
            ExtensionCheckResult activated = await manager.ActivateAsync(package.Id, package, CancellationToken.None);
            activated.Succeeded.Should().BeTrue(string.Join(Environment.NewLine,
                activated.Diagnostics.Select(issue =>
                    $"{issue.Code} {issue.Location.FileName}:{issue.Location.Line}:{issue.Location.Column}: {issue.Message}")));

            await using IExtensionLease lease = (await manager.AcquireAsync(package.Id, CancellationToken.None))!;
            IOcrExtension ocr = lease.Ocr!;
            ocr.EngineDefinition.EngineId.Should().Be("oxr");
            string? modelPath = Environment.GetEnvironmentVariable("PATCHOULI_OXR_TEST_MODEL_PATH");
            string? imagePath = Environment.GetEnvironmentVariable("PATCHOULI_OXR_TEST_IMAGE");
            if (string.IsNullOrWhiteSpace(modelPath) || string.IsNullOrWhiteSpace(imagePath))
            {
                ExtensionConfigSnapshot missing = Config("");
                (await ocr.CheckReadinessAsync(missing, resources.ForRun(missing), CancellationToken.None))
                    .IsFailure.Should().BeTrue();
                return;
            }

            ExtensionConfigSnapshot config = Config(modelPath);
            (await ocr.CheckReadinessAsync(config, resources.ForRun(config), CancellationToken.None))
                .IsSuccess.Should().BeTrue();
            OcrJobInput input = new(new OcrInputId("oxr-compiled-sample"),
                new NormalizedBBox(0, 0, 1, 1), "image/png");
            OcrJobPage page = new(new ExtensionPageIdentity(
                DocumentInstanceId.Parse("00000000-0000-0000-0000-000000000001"),
                PageId.Parse("00000000-0000-0000-0000-000000000002"),
                DocumentTreeRevisionId.Parse("00000000-0000-0000-0000-000000000003"), 1), [input]);
            RecordingJob job = new(config, page, File.ReadAllBytes(imagePath));
            await ocr.RunAsync(job, resources.ForRun(config), CancellationToken.None);

            job.Failures.Should().BeEmpty();
            OcrExtensionCandidate committed = job.Committed.Should().ContainSingle().Subject;
            committed.Inputs.Should().ContainSingle().Which.State.Should().Be(OcrInputSettlementState.Recognized);
            committed.Boxes.Select(box => box.Candidate.Payload).OfType<TextBoxPayload>()
                .Select(payload => payload.Markdown).Should().Contain(text => text.Contains("HELLO OCR 123"));
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryForcingExtensionUnload(runtimeDirectory);
        }
    }

    private static ExtensionConfigSnapshot Config(string modelPath)
    {
        return new ExtensionConfigSnapshot(
            new Dictionary<string, string> { ["model-path"] = modelPath, ["max-new-tokens"] = "64" }, 1);
    }

    private sealed class RecordingJob(ExtensionConfigSnapshot config, OcrJobPage page, byte[] image) : IOcrJob
    {
        private bool _served;

        public OcrJobInfo Info => new("oxr-compiled-job",
            page.Page.DocumentId, new OcrJobOptions(), config);

        public IProgress<ExtensionProgress>? Progress => null;
        public List<OcrExtensionCandidate> Committed { get; } = [];
        public List<string> Failures { get; } = [];

        public ValueTask<OcrJobPage?> NextPageAsync(CancellationToken cancellationToken = default)
        {
            if (_served)
            {
                return ValueTask.FromResult<OcrJobPage?>(null);
            }

            _served = true;
            return ValueTask.FromResult<OcrJobPage?>(page);
        }

        public ValueTask<OcrInputImage> ReadInputImageAsync(OcrJobInput input,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new OcrInputImage(input.Id, input.ContentType, image));
        }

        public ValueTask<OcrSubmissionReceipt> SubmitCandidatesAsync(OcrExtensionCandidate candidate,
            CancellationToken cancellationToken = default)
        {
            Committed.Add(candidate);
            return ValueTask.FromResult(OcrSubmissionReceipt.Accepted(candidate.Boxes.Count));
        }

        public ValueTask<OcrSubmissionReceipt> FailPageAsync(ExtensionPageIdentity identity,
            string errorCode, string? errorMessage = null, CancellationToken cancellationToken = default)
        {
            Failures.Add($"{errorCode}: {errorMessage}");
            return ValueTask.FromResult(OcrSubmissionReceipt.Accepted(0));
        }
    }

    private sealed class DenyCredentials : ICredentialStore
    {
        public Task<Result<ProviderCredentialMetadata>> SaveAsync(string providerId, string displayName,
            string secretValue, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<ProviderCredentialMetadata>.Failure(AppErrorCodes.UnsupportedOperation,
                "OXR never uses credentials."));
        }

        public Task<Result<string>> GetActiveSecretForProviderAsync(string providerId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<string>.Failure(AppErrorCodes.UnsupportedOperation,
                "OXR never uses credentials."));
        }

        public Task<Result> RemoveAsync(string providerId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Failure(AppErrorCodes.UnsupportedOperation,
                "OXR never uses credentials."));
        }

        public Task<Result<IReadOnlyList<ProviderCredentialMetadata>>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<ProviderCredentialMetadata>>.Success([]));
        }
    }

    private sealed class DenyNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("OXR must not access the network.");
        }
    }
}
