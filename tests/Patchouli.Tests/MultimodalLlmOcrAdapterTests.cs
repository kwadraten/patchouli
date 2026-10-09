using Dapper;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Credentials;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Llm;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class MultimodalLlmOcrAdapterTests
{
    private const string TwoBoxesJson =
        """
        {"boxes":[
          {"text":"Hello","x":0.1,"y":0.2,"width":0.3,"height":0.05,"confidence":0.98},
          {"text":"World","x":0.5,"y":0.9,"width":0.6,"height":0.2}
        ]}
        """;

    [Fact]
    public async Task RunPageAsync_normalizes_vision_json_into_engine_text_boxes()
    {
        string imagePath = WriteTempPng();
        try
        {
            StubLlmChatClient client = new();
            client.Enqueue(Completion(TwoBoxesJson));
            StubRuntime runtime = new() { Client = client };
            MultimodalLlmOcrAdapter adapter = new(runtime);

            Result<OcrEnginePageResult> result =
                await adapter.RunPageAsync(PageInput(imagePath), Preset("custom-model"));

            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            OcrEnginePageResult page = result.Value;
            page.Succeeded.Should().BeTrue();
            page.TextBoxes.Should().HaveCount(2);
            page.TextBoxes![0].Text.Should().Be("Hello");
            page.TextBoxes[0].BBox.X.Should().BeApproximately(0.1, 1e-9);
            page.TextBoxes[0].BBox.Width.Should().BeApproximately(0.3, 1e-9);
            page.TextBoxes[0].Confidence.Should().BeApproximately(0.98, 1e-9);
            page.TextBoxes[1].Text.Should().Be("World");
            page.TextBoxes[1].Confidence.Should().BeNull();
            page.Text.Should().Be("Hello\nWorld");
            page.BBox.Should().Be(new NormalizedBBox(0, 0, 1, 1));
            runtime.ClientRequests.Should().Equal(("openai", "custom-model"));
            client.VisionCalls.Should().HaveCount(1);
            client.VisionCalls[0].Vision.MimeType.Should().Be("image/png");
            client.VisionCalls[0].Vision.DataUriOrUrl.Should().StartWith("data:image/png;base64,");
            client.VisionCalls[0].Vision.Detail.Should().Be("high");
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task RunPageAsync_clamps_out_of_range_box_coordinates()
    {
        string imagePath = WriteTempPng();
        try
        {
            StubLlmChatClient client = new();
            client.Enqueue(Completion("""{"boxes":[{"text":"edge","x":-0.5,"y":0.9,"width":3.0,"height":1.0}]}"""));
            MultimodalLlmOcrAdapter adapter = new(new StubRuntime { Client = client });

            Result<OcrEnginePageResult> result =
                await adapter.RunPageAsync(PageInput(imagePath), Preset("custom-model"));

            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            NormalizedBBox bbox = result.Value.TextBoxes!.Single().BBox;
            bbox.X.Should().Be(0.0);
            bbox.Y.Should().BeApproximately(0.9, 1e-9);
            bbox.Width.Should().BeApproximately(1.0, 1e-9);
            bbox.Height.Should().BeApproximately(0.1, 1e-9);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task RunPageAsync_restricts_the_prompt_to_the_region_and_reports_the_region_bbox()
    {
        string imagePath = WriteTempPng();
        try
        {
            StubLlmChatClient client = new();
            client.Enqueue(Completion("""{"boxes":[{"text":"in region","x":0.2,"y":0.3,"width":0.1,"height":0.1}]}"""));
            MultimodalLlmOcrAdapter adapter = new(new StubRuntime { Client = client });
            NormalizedBBox region = new(0.1, 0.2, 0.3, 0.4);
            OcrInputDescriptor input = new(PageId.New(), DocumentInstanceId.New(), OcrInputKinds.RegionImage,
                imagePath, null, region, "available", null);

            Result<OcrEnginePageResult> result = await adapter.RunPageAsync(input, Preset("custom-model"));

            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            result.Value.BBox.Should().Be(region);
            client.VisionCalls.Should().HaveCount(1);
            client.VisionCalls[0].Vision.Text.Should().Contain("x=0.1");
            client.VisionCalls[0].Vision.Text.Should().Contain("width=0.3");
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task RunPageAsync_succeeds_with_empty_boxes_when_the_model_finds_no_text()
    {
        string imagePath = WriteTempPng();
        try
        {
            StubLlmChatClient client = new();
            client.Enqueue(Completion("""{"boxes":[]}"""));
            MultimodalLlmOcrAdapter adapter = new(new StubRuntime { Client = client });

            Result<OcrEnginePageResult> result =
                await adapter.RunPageAsync(PageInput(imagePath), Preset("custom-model"));

            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            result.Value.Succeeded.Should().BeTrue();
            result.Value.TextBoxes.Should().BeEmpty();
            result.Value.Text.Should().BeNull();
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task RunPageAsync_fails_with_a_stable_code_when_the_response_is_not_parseable()
    {
        string imagePath = WriteTempPng();
        try
        {
            StubLlmChatClient client = new();
            client.Enqueue(Completion("no JSON here"));
            MultimodalLlmOcrAdapter adapter = new(new StubRuntime { Client = client });

            Result<OcrEnginePageResult> result =
                await adapter.RunPageAsync(PageInput(imagePath), Preset("custom-model"));

            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should().Be(AppErrorCodes.InvalidState);
            new OcrRetryPolicy().Classify(result.ErrorCode)
                .Should().Be(OcrRetryClassification.NonRetryable);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task RunPageAsync_surfaces_llm_failure_codes_and_the_retry_policy_classifies_them_identically()
    {
        string[] codes = typeof(LlmFailureCodes).GetFields()
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();
        codes.Should().NotBeEmpty();

        foreach (string code in codes)
        {
            string imagePath = WriteTempPng();
            try
            {
                StubLlmChatClient client = new();
                client.Enqueue(Result<LlmChatCompletion>.Failure(code, "stubbed failure"));
                MultimodalLlmOcrAdapter adapter = new(new StubRuntime { Client = client });

                Result<OcrEnginePageResult> result =
                    await adapter.RunPageAsync(PageInput(imagePath), Preset("custom-model"));

                result.IsFailure.Should().BeTrue();
                result.ErrorCode.Should().Be(code, $"the adapter must pass failure code '{code}' through unchanged");
                new OcrRetryPolicy().Classify(code)
                    .Should().Be(LlmFailureClassifier.Classify(code),
                        $"OcrRetryPolicy must classify LLM code '{code}' exactly like LlmFailureClassifier");
            }
            finally
            {
                File.Delete(imagePath);
            }
        }
    }

    [Fact]
    public async Task RunPageAsync_propagates_client_creation_failures_with_llm_codes()
    {
        string imagePath = WriteTempPng();
        try
        {
            StubRuntime runtime = new()
            {
                ClientError = Result<ILlmChatClient>.Failure(LlmFailureCodes.AuthFailed, "no key")
            };
            MultimodalLlmOcrAdapter adapter = new(runtime);

            Result<OcrEnginePageResult> result =
                await adapter.RunPageAsync(PageInput(imagePath), Preset("custom-model"));

            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should().Be(LlmFailureCodes.AuthFailed);
            new OcrRetryPolicy().Classify(result.ErrorCode)
                .Should().Be(OcrRetryClassification.ManualRepairRequired);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task RunPageAsync_fails_when_no_model_is_configured()
    {
        string imagePath = WriteTempPng();
        try
        {
            StubRuntime runtime = new() { Selection = ("openai", ""), Client = new StubLlmChatClient() };
            MultimodalLlmOcrAdapter adapter = new(runtime);

            Result<OcrEnginePageResult> result = await adapter.RunPageAsync(PageInput(imagePath), Preset(""));

            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should().Be(LlmFailureCodes.ModelNotFound);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task CheckEnvironmentAsync_is_ready_when_provider_has_key_and_model()
    {
        MultimodalLlmOcrAdapter adapter = new(RealRuntime(new StubCredentialStore("openai")));

        OcrEnvironmentCheckResult result = await adapter.CheckEnvironmentAsync(Preset("gpt-4o-mini"));

        result.EngineId.Should().Be(OcrEngineIds.MultimodalLlm);
        result.IsReady.Should().BeTrue();
        result.Status.Should().Be(OcrEnvironmentStatus.Ready);
        result.RequiredAction.Should().Be(OcrRequiredAction.None);
        result.Message.Should().Contain("openai");
    }

    [Fact]
    public async Task CheckEnvironmentAsync_reports_a_missing_key_as_not_ready()
    {
        MultimodalLlmOcrAdapter adapter = new(RealRuntime(new StubCredentialStore()));

        OcrEnvironmentCheckResult result = await adapter.CheckEnvironmentAsync(Preset("gpt-4o-mini"));

        result.IsReady.Should().BeFalse();
        result.Status.Should().Be(OcrEnvironmentStatus.MissingCredential);
        result.RequiredAction.Should().Be(OcrRequiredAction.ConfigureCredential);
    }

    [Fact]
    public async Task CheckEnvironmentAsync_reports_an_unknown_provider_as_an_invalid_endpoint()
    {
        StubRuntime runtime = new()
        {
            Selection = ("not-a-llm-provider", "some-model"),
            Readiness = []
        };
        MultimodalLlmOcrAdapter adapter = new(runtime);

        OcrEnvironmentCheckResult result = await adapter.CheckEnvironmentAsync(Preset("some-model"));

        result.IsReady.Should().BeFalse();
        result.Status.Should().Be(OcrEnvironmentStatus.InvalidEndpoint);
        result.RequiredAction.Should().Be(OcrRequiredAction.ConfigureEndpoint);
    }

    [Fact]
    public async Task CheckEnvironmentAsync_reports_a_missing_model_as_not_ready()
    {
        LlmAppSettings settings = LlmAppSettings.Default().WithProvider(
                LlmProviderAppSettings.FromCatalog(LlmProviderCatalog.Find("openai")!)) with
            {
                OcrModel = ""
            };
        MultimodalLlmOcrAdapter adapter =
            new(new MultimodalLlmOcrRuntime(() => settings, new StubCredentialStore("openai")));

        OcrEnvironmentCheckResult result = await adapter.CheckEnvironmentAsync(Preset(""));

        result.IsReady.Should().BeFalse();
        result.Status.Should().Be(OcrEnvironmentStatus.NotConfigured);
    }

    [Fact]
    public async Task The_real_runtime_rejects_unknown_providers_with_bad_endpoint_config()
    {
        MultimodalLlmOcrRuntime runtime = RealRuntime(new StubCredentialStore("openai"));

        Result<ILlmChatClient> client = await runtime.CreateClientAsync("not-a-llm-provider", "some-model");

        client.IsFailure.Should().BeTrue();
        client.ErrorCode.Should().Be(LlmFailureCodes.BadEndpointConfig);
    }

    [Fact]
    public async Task The_real_runtime_reflects_missing_keys_in_the_readiness_snapshot()
    {
        MultimodalLlmOcrRuntime withKey = RealRuntime(new StubCredentialStore("openai"));
        IReadOnlyList<LlmProviderReadiness> ready = await withKey.InspectProvidersAsync();
        ready.Single(provider => provider.ProviderId == "openai").IsConfigured.Should().BeTrue();

        MultimodalLlmOcrRuntime withoutKey = RealRuntime(new StubCredentialStore());
        IReadOnlyList<LlmProviderReadiness> missing = await withoutKey.InspectProvidersAsync();
        LlmProviderReadiness openAi = missing.Single(provider => provider.ProviderId == "openai");
        openAi.HasCredential.Should().BeFalse();
        openAi.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task ValidatePresetAsync_fails_until_the_environment_is_ready()
    {
        MultimodalLlmOcrAdapter missingKey = new(RealRuntime(new StubCredentialStore()));
        (await missingKey.ValidatePresetAsync(Preset("gpt-4o-mini"))).IsFailure.Should().BeTrue();

        MultimodalLlmOcrAdapter ready = new(RealRuntime(new StubCredentialStore("openai")));
        (await ready.ValidatePresetAsync(Preset("gpt-4o-mini"))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Document_run_commits_llm_output_through_the_tree_importer_as_a_document_tree_candidate()
    {
        await using Context context = await Context.CreateAsync();

        Result<OcrRun> run = await context.Engine.RunPresetOnDocumentAsync(
            context.Document.DocumentInstanceId, context.OcrPreset.PresetId);

        run.IsSuccess.Should().BeTrue(run.ErrorMessage);
        run.Value.State.Should().Be(OcrRunState.Completed);
        context.StubClient.VisionCalls.Should().HaveCount(1);

        RecordingTreeImporter importer = (RecordingTreeImporter)context.TreeImporter;
        importer.Candidates.Should().HaveCount(1);
        OcrDocumentTreeCandidate candidate = importer.Candidates.Single();
        candidate.Pages.Should().HaveCount(1);
        candidate.Pages[0].PageId.Should().Be(context.Pages[0].PageId);
        candidate.Pages[0].Boxes.Should().HaveCount(2);
        candidate.Pages[0].Boxes.Select(box => box.BoxType).Should().AllBe(DocumentBoxType.Text);
        candidate.Pages[0].Boxes[0].Payload.Should().BeOfType<TextBoxPayload>();
        candidate.Pages[0].Boxes[0].Payload.ToString().Should().Contain("Hello");

        int boxCount = await context.CountAsync("select count(*) from document_boxes;");
        boxCount.Should().Be(2, "boxes exist only because the unified importer created them");
    }

    [Fact]
    public async Task Document_run_writes_nothing_when_the_tree_importer_rejects_the_candidate()
    {
        await using Context context = await Context.CreateAsync(true);

        Result<OcrRun> run = await context.Engine.RunPresetOnDocumentAsync(
            context.Document.DocumentInstanceId, context.OcrPreset.PresetId);

        run.IsSuccess.Should().BeTrue(run.ErrorMessage);
        run.Value.State.Should().Be(OcrRunState.Failed);
        IReadOnlyList<OcrPageResult>
            pageResults = (await context.Engine.ListPageResultsAsync(run.Value.OcrRunId)).Value;
        pageResults.Should().HaveCount(1);
        pageResults[0].State.Should().Be(OcrPageResultState.Failed);
        pageResults[0].ErrorCode.Should().Be(BlockingTreeImporter.ErrorCode);

        int boxCount = await context.CountAsync("select count(*) from document_boxes;");
        boxCount.Should().Be(0,
            "a failed import must not leave boxes behind; the engine must never write document_boxes directly");
    }

    private static MultimodalLlmOcrRuntime RealRuntime(ICredentialStore credentialStore)
    {
        return new MultimodalLlmOcrRuntime(() => LlmAppSettings.Default(), credentialStore);
    }

    private static OcrPresetVersion Preset(string modelId)
    {
        return new OcrPresetVersion(OcrPresetVersionId.New(), OcrPresetId.New(), OcrEngineIds.MultimodalLlm,
            modelId, null, "{}", false, DateTimeOffset.UtcNow);
    }

    private static OcrInputDescriptor PageInput(string imagePath)
    {
        return new OcrInputDescriptor(PageId.New(), DocumentInstanceId.New(), OcrInputKinds.PageImage,
            imagePath, null, null, "available", null);
    }

    private static Result<LlmChatCompletion> Completion(string text)
    {
        return Result<LlmChatCompletion>.Success(new LlmChatCompletion(
            text, "gpt-4o-mini", "openai", "stop", LlmUsage.Empty, false, null, "signature"));
    }

    private static string WriteTempPng()
    {
        string path = Path.Combine(Path.GetTempPath(), $"patchouli-llm-ocr-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        return path;
    }

    private sealed class StubRuntime : IMultimodalLlmOcrRuntime
    {
        public (string ProviderId, string Model) Selection { get; set; } = ("openai", "gpt-4o-mini");
        public ILlmChatClient? Client { get; set; }
        public Result<ILlmChatClient>? ClientError { get; set; }

        public IReadOnlyList<LlmProviderReadiness> Readiness { get; set; } =
            [new("openai", "OpenAI", true, true, true, "Ready.")];

        public List<(string ProviderId, string Model)> ClientRequests { get; } = [];

        public (string ProviderId, string Model) ResolveOcrSelection()
        {
            return Selection;
        }

        public Task<Result<ILlmChatClient>> CreateClientAsync(string providerId, string model,
            CancellationToken cancellationToken = default)
        {
            ClientRequests.Add((providerId, model));
            return Task.FromResult(ClientError ?? (Client is null
                ? Result<ILlmChatClient>.Failure(LlmFailureCodes.BadEndpointConfig, "no stubbed client")
                : Result<ILlmChatClient>.Success(Client)));
        }

        public Task<IReadOnlyList<LlmProviderReadiness>> InspectProvidersAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Readiness);
        }
    }

    private sealed class StubLlmChatClient : ILlmChatClient
    {
        private readonly Queue<Result<LlmChatCompletion>> _completions = new();

        public List<(string Key, LlmVisionInput Vision)> VisionCalls { get; } = [];

        public void Enqueue(Result<LlmChatCompletion> completion)
        {
            _completions.Enqueue(completion);
        }

        public Task<Result<LlmChatCompletion>> CompleteAsync(string conversationKey, LlmChatRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("The OCR adapter only uses vision calls.");
        }

        public Task<Result<LlmChatCompletion>> CompleteVisionAsync(string conversationKey, LlmChatRequest request,
            LlmVisionInput vision, CancellationToken cancellationToken = default)
        {
            VisionCalls.Add((conversationKey, vision));
            return Task.FromResult(_completions.Count > 0
                ? _completions.Dequeue()
                : Result<LlmChatCompletion>.Failure(LlmFailureCodes.EmptyResponse, "no stubbed completion"));
        }
    }

    private sealed class StubCredentialStore : ICredentialStore
    {
        private readonly HashSet<string> _providersWithKey;

        public StubCredentialStore(params string[] providersWithKey)
        {
            _providersWithKey = providersWithKey.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public Task<Result<ProviderCredentialMetadata>> SaveAsync(string providerId, string displayName,
            string secretValue, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<ProviderCredentialMetadata>.Failure(AppErrorCodes.UnsupportedOperation,
                "The stub credential store is read-only."));
        }

        public Task<Result<string>> GetActiveSecretForProviderAsync(string providerId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_providersWithKey.Contains(providerId)
                ? Result<string>.Success("stub-secret")
                : Result<string>.Failure(AppErrorCodes.NotFound, $"No key stored for provider '{providerId}'."));
        }

        public Task<Result> RemoveAsync(string providerId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Failure(AppErrorCodes.UnsupportedOperation,
                "The stub credential store is read-only."));
        }

        public Task<Result<IReadOnlyList<ProviderCredentialMetadata>>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            IReadOnlyList<ProviderCredentialMetadata> metadata = _providersWithKey
                .Select(providerId => new ProviderCredentialMetadata(
                    CredentialId.New(), providerId, providerId, ProviderCredentialStatus.Active, now, now))
                .ToArray();
            return Task.FromResult(Result<IReadOnlyList<ProviderCredentialMetadata>>.Success(metadata));
        }
    }

    private sealed class RecordingTreeImporter : IOcrDocumentTreeImporter
    {
        private readonly IOcrDocumentTreeImporter _inner;

        public RecordingTreeImporter(IOcrDocumentTreeImporter inner)
        {
            _inner = inner;
        }

        public List<OcrDocumentTreeCandidate> Candidates { get; } = [];

        public Task<Result<OcrDocumentTreeImportResult>> BeginWorkingAsync(OcrDocumentTreeImportRequest request,
            CancellationToken cancellationToken = default)
        {
            Candidates.Add(request.Candidate);
            return _inner.BeginWorkingAsync(request, cancellationToken);
        }

        public Task<Result<IReadOnlyList<DocumentTreeRevisionId>>> CommitAsync(
            IReadOnlyList<DocumentTreeRevisionId> workingRevisionIds, DocumentCommitId? commitId = null,
            CancellationToken cancellationToken = default)
        {
            return _inner.CommitAsync(workingRevisionIds, commitId, cancellationToken);
        }
    }

    private sealed class BlockingTreeImporter : IOcrDocumentTreeImporter
    {
        public const string ErrorCode = "import_channel_blocked_for_test";

        public Task<Result<OcrDocumentTreeImportResult>> BeginWorkingAsync(OcrDocumentTreeImportRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<OcrDocumentTreeImportResult>.Failure(ErrorCode,
                "The test importer rejects every candidate."));
        }

        public Task<Result<IReadOnlyList<DocumentTreeRevisionId>>> CommitAsync(
            IReadOnlyList<DocumentTreeRevisionId> workingRevisionIds, DocumentCommitId? commitId = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<DocumentTreeRevisionId>>.Failure(ErrorCode,
                "The test importer rejects every commit."));
        }
    }

    private sealed class FakePageRenderService : IPageRenderService
    {
        private readonly string _imagePath;

        public FakePageRenderService(string imagePath)
        {
            _imagePath = imagePath;
        }

        public Task<Result<OcrInputDescriptor>> BuildOcrInputFromRenderedPageAsync(
            DocumentInstanceId documentInstanceId, PageId pageId, int dpi = 200,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<OcrInputDescriptor>.Success(
                new OcrInputDescriptor(pageId, documentInstanceId, OcrInputKinds.PageImage, _imagePath, null, null,
                    "available", null)));
        }

        public Task<Result<PageRenderResult>> RenderPageAsync(PageRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<string?>> GetCachedRenderPathAsync(PageRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<PdfPagePixelBufferLease>> RenderPreviewAsync(PageRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> ClearRenderCacheForDocumentAsync(DocumentInstanceId documentInstanceId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<string>> RenderRegionPngAsync(DocumentInstanceId documentInstanceId, PageId pageId,
            NormalizedBBox region, int dpi = 200, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<PdfRendererAvailability> GetRendererAvailabilityAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task ReleaseDocumentSessionAsync(DocumentInstanceId documentInstanceId,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class Context : IAsyncDisposable
    {
        private readonly TemporarySqliteDatabase _database;
        private readonly string _rootDirectory;

        private Context(TemporarySqliteDatabase database, string rootDirectory, DocumentInstance document, Page[] pages,
            OcrPreset preset, OcrRunEngine engine, IOcrDocumentTreeImporter treeImporter,
            StubLlmChatClient stubClient)
        {
            _database = database;
            _rootDirectory = rootDirectory;
            Document = document;
            Pages = pages;
            OcrPreset = preset;
            Engine = engine;
            TreeImporter = treeImporter;
            StubClient = stubClient;
        }

        public DocumentInstance Document { get; }
        public Page[] Pages { get; }
        public OcrPreset OcrPreset { get; }
        public OcrRunEngine Engine { get; }
        public IOcrDocumentTreeImporter TreeImporter { get; }
        public StubLlmChatClient StubClient { get; }

        public async Task<int> CountAsync(string sql)
        {
            await using Microsoft.Data.Sqlite.SqliteConnection connection =
                _database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<int>(sql);
        }

        public static async Task<Context> CreateAsync(bool blockImport = false)
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
            string rootDirectory = Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), $"patchouli-llm-ocr-runs-{Guid.NewGuid():N}")).FullName;
            string sourcePdfPath = Path.Combine(rootDirectory, "source.pdf");
            File.Copy(TestFixtures.RealThreePagePdf, sourcePdfPath);
            string imagePath = Path.Combine(rootDirectory, "page.png");
            File.WriteAllBytes(imagePath, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));

            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
            await libraries.CreateLibraryAsync("LLM OCR runs");
            ItemMetadata item = (await new ItemService(database.ConnectionFactory, libraries, clock)
                .CreateItemAsync("document", "LLM OCR runs")).Value;
            FileAsset file = (await new FileAssetService(database.ConnectionFactory, libraries, clock)
                .RegisterFileAsync(sourcePdfPath)).Value;
            DocumentInstance document = (await new DocumentInstanceService(database.ConnectionFactory, clock)
                .AttachDocumentInstanceAsync(item.ItemId, file.FileAssetId, DocumentInstanceType.PrimaryScan)).Value;
            Infrastructure.Layout.PageService pages = new(database.ConnectionFactory, clock);
            Page page = (await pages.CreatePageAsync(document.DocumentInstanceId, 0, "1", null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;
            OcrPreset preset = (await new OcrPresetService(database.ConnectionFactory, libraries, clock)
                .CreatePresetAsync("LLM OCR runs", null, OcrEngineIds.MultimodalLlm, "gpt-4o-mini", null, "{}",
                    false)).Value;

            DocumentTreeService trees = BoxTreeTestData.CreateService(database.ConnectionFactory, clock);
            IOcrDocumentTreeImporter treeImporter = blockImport
                ? new BlockingTreeImporter()
                : new RecordingTreeImporter(new OcrDocumentTreeImporter(trees));
            StubLlmChatClient stubClient = new();
            stubClient.Enqueue(Result<LlmChatCompletion>.Success(new LlmChatCompletion(
                TwoBoxesJson, "gpt-4o-mini", "openai", "stop", LlmUsage.Empty, false, null, "signature")));
            StubRuntime runtime = new() { Client = stubClient };
            MultimodalLlmOcrAdapter adapter = new(runtime);
            OcrAdapterRegistry adapterRegistry = new();
            adapterRegistry.RegisterAdapter(adapter);
            OcrRunEngine engine = new(
                database.ConnectionFactory,
                clock,
                (_, _) => Task.FromResult(Result<string>.Success("key")),
                new MockOcrEngine(),
                treeImporter: treeImporter,
                adapterRegistry: adapterRegistry,
                pageRenderService: new FakePageRenderService(imagePath),
                trees: trees);
            return new Context(database, rootDirectory, document, [page], preset, engine, treeImporter, stubClient);
        }

        public async ValueTask DisposeAsync()
        {
            await _database.DisposeAsync();
            if (Directory.Exists(_rootDirectory))
            {
                Directory.Delete(_rootDirectory, true);
            }
        }
    }
}
