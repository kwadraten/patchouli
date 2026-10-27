using FluentAssertions;
using Patchouli.Core.Credentials;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Extensions;
using Patchouli.Llm;
using Patchouli.Ocr;
using Patchouli.Ocr.Engines;
using Patchouli.Ocr.Engines.Vision;
using SkiaSharp;

namespace Patchouli.Tests;

/// <summary>
/// Multimodal (hosted vision) OCR extension fixtures: the session resolves its provider through
/// the "ocr" domain on frozen config, sends the issued image verbatim for region inputs, runs the
/// lazy layout detector only for full-page inputs, validates the LLM reading order with exactly
/// one correction attempt, and preserves typed candidates instead of flattening them to text.
/// No live provider, model weights, credentials or network are touched — the recording capability
/// and a stub detector stand in for both.
/// </summary>
public sealed class MultimodalOcrExtensionTests
{
    private static readonly NormalizedBBox FullPage = new(0, 0, 1, 1);

    [Fact]
    public async Task Layout_and_parallel_region_progress_carries_page_and_input_identity()
    {
        FakeDetector detector = new(
        [
            new VisionLayoutRegion("text", new NormalizedBBox(0, 0, 1, 0.5), 0.9),
            new VisionLayoutRegion("text", new NormalizedBBox(0, 0.5, 1, 0.5), 0.9)
        ]);
        RecordingLlm llm = new("""{"order":[0,1]}""", """{"content":[]}""", """{"content":[]}""");
        OcrJobInput input = new(new OcrInputId("page-progress"), FullPage, "image/png");
        OcrJobPage page = Page(input);
        using MultimodalOcrSession session = new(Config(("use-local-layout", "true"), ("concurrency", "2")), detector);
        using OcrInputImage image = new(input.Id, input.ContentType, Png());
        using VisionResources resources = new(llm);
        RecordingProgress progress = new();

        await session.RecognizeAsync(page, input, image, resources, CancellationToken.None, progress);

        progress.Values.Select(value => value.Phase).Should()
            .ContainInOrder("layout-detection", "reading-order", "region-ocr");
        progress.Values.Where(value => value.Phase == "region-ocr").Select(value => value.Completed)
            .Should().Equal(0, 1, 2);
        progress.Values.Should().OnlyContain(value => value.ItemId == page.Page.PageIndex.ToString() &&
                                                      value.ExecutionId == input.Id.Value &&
                                                      value.ParentPhase == "page-ocr" && value.Message == null);
    }

    [Fact]
    public void Engine_definition_is_pure_metadata()
    {
        OcrEngineDefinition definition = MultimodalOcrSession.GetDefinition();
        definition.EngineId.Should().Be("multimodal-llm");
        definition.AdapterKind.Should().Be("cloud_api");
        definition.RequiresCredential.Should().BeTrue();
        definition.SupportedInputKinds.Should().BeEquivalentTo(
            [OcrInputKinds.PageImage, OcrInputKinds.ImageFile, OcrInputKinds.RegionImage]);
    }

    [Fact]
    public void CheckReadiness_is_branch_neutral_and_layout_readiness_probes_weights()
    {
        // Engine readiness never touches layout weights: crop and direct jobs must not be blocked.
        MultimodalOcrSession.CheckReadiness(Config(("use-local-layout", "false")))
            .IsSuccess.Should().BeTrue();
        MultimodalOcrSession.CheckReadiness(
                Config(("use-local-layout", "true"), ("model-path", "")))
            .IsSuccess.Should().BeTrue("missing weights do not block crop/direct execution");

        Result disabled = MultimodalOcrSession.CheckLayoutReadiness(
            Config(("use-local-layout", "false")));
        disabled.IsSuccess.Should().BeTrue("the layout branch is unreachable when disabled");

        Result missing = MultimodalOcrSession.CheckLayoutReadiness(
            Config(("use-local-layout", "true"), ("model-path", "")));
        missing.IsFailure.Should().BeTrue();
        missing.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);

        Result resolved = MultimodalOcrSession.CheckLayoutReadiness(
            Config(("use-local-layout", "true"), ("model-path", FixtureOnnxPath())));
        resolved.IsSuccess.Should().BeTrue("an existing .onnx path satisfies the file-level probe");
    }

    [Fact]
    public async Task Region_input_sends_the_issued_bytes_verbatim_and_needs_no_weights()
    {
        // A crop image with local layout enabled but NO model path: detection must never run.
        FakeDetector detector = new([]);
        RecordingLlm llm = new(
            """{"content":[{"type":"table","markdown":"| a | b |\n| 1 | 2 |"},{"type":"code","code":"x=1","language":"python"}]}""");
        byte[] issued = Png(60, 32);
        OcrJobInput input = new(new OcrInputId("region-1"), new NormalizedBBox(0.1, 0.2, 0.5, 0.4),
            "image/png");
        OcrJobPage page = Page(input);
        MultimodalOcrSession session = new(
            Config(("use-local-layout", "true"), ("model-path", ""), ("reasoning-effort", "low")),
            detector);
        VisionResources resources = new(llm);

        OcrExtensionBoxCandidate[] candidates = await session.RecognizeAsync(page, input,
            new OcrInputImage(input.Id, input.ContentType, issued), resources, CancellationToken.None);

        detector.DetectCalls.Should().Be(0, "explicit region inputs bypass layout detection");
        resources.Resolutions.Should().ContainSingle().Which.Domain.Should().Be("ocr");
        LlmRequest sent = llm.Requests.Should().ContainSingle().Subject;
        sent.ReasoningEffort.Should().Be("low");
        sent.Domain.Should().Be("ocr");
        LlmImage image = sent.Messages[0].Images.Should().ContainSingle().Subject;
        image.Data.Should().Equal(issued, "the already-cropped input is never re-rendered or re-cropped");
        image.MediaType.Should().Be("image/png");

        candidates.Should().HaveCount(2);
        candidates.Should().OnlyContain(candidate =>
                candidate.InputId == input.Id && candidate.BBox == input.Region,
            "region content anchors to the issued region baseline");
        DocumentBoxSeed table = candidates[0].Candidate;
        table.BoxType.Should().Be(DocumentBoxType.Table);
        table.Payload.Should().BeOfType<TableBoxPayload>()
            .Which.Markdown.Should().Contain("| a | b |");
        DocumentBoxSeed code = candidates[1].Candidate;
        code.BoxType.Should().Be(DocumentBoxType.Code);
        code.Payload.Should().BeOfType<CodeBoxPayload>().Which.Code.Should().Be("x=1");
        code.CodeLanguage.Should().Be("python");
        candidates.Select(candidate => candidate.Candidate.SourceOrder).Should().BeInAscendingOrder();
        session.Dispose();
    }

    [Fact]
    public async Task Direct_full_page_uses_the_models_own_boxes_mapped_through_the_region()
    {
        RecordingLlm llm = new(
            """{"boxes":[{"type":"equation","latex":"x^2","x":0.1,"y":0.2,"width":0.3,"height":0.1,"confidence":0.95}]}""");
        OcrJobInput input = new(new OcrInputId("page-0"), FullPage, "image/png");
        OcrJobPage page = Page(input);
        MultimodalOcrSession session = new(Config(("use-local-layout", "false")));
        byte[] issued = Png();

        OcrExtensionBoxCandidate[] candidates = await session.RecognizeAsync(page, input,
            new OcrInputImage(input.Id, input.ContentType, issued), new VisionResources(llm),
            CancellationToken.None);

        llm.Requests[0].Messages[0].Images.Should().ContainSingle().Which.Data.Should().Equal(issued);
        DocumentBoxSeed seed = candidates.Should().ContainSingle().Subject.Candidate;
        seed.BoxType.Should().Be(DocumentBoxType.Equation);
        seed.Payload.Should().BeOfType<EquationBoxPayload>().Which.Latex.Should().Be("x^2");
        seed.BBox.Should().Be(new NormalizedBBox(0.1, 0.2, 0.3, 0.1));
        seed.Confidence.Should().Be(0.95);
    }

    [Fact]
    public void Malformed_nonempty_replies_fail_instead_of_becoming_blank_results()
    {
        // An intentional empty array is the only blank success.
        VisionResultParser.ParseDirect("""{"boxes":[]}""").IsSuccess.Should().BeTrue();
        VisionResultParser.ParseRegionContent("""{"content":[]}""").IsSuccess.Should().BeTrue();

        string[] malformed =
        [
            """{"boxes":[{"type":"text","markdown":"ok","x":0,"y":0,"width":1,"height":0.5},"junk"]}""",
            """{"boxes":[{"markdown":"x","x":0,"y":0,"width":1,"height":0.5}]}""",
            """{"boxes":[{"type":"text","markdown":"x"}]}""",
            """{"boxes":[{"type":"text","markdown":"x","x":0,"y":0,"width":1.5,"height":0.5}]}""",
            """{"boxes":[{"type":"text","markdown":"x","x":0,"y":0,"width":1}]}""",
            """{"boxes":[{"type":"text","markdown":"x","x":0,"y":0,"width":1,"height":0.5,"confidence":1.4}]}""",
            """{"boxes":[{"type":"text","markdown":"x","x":"bad","y":0,"width":1,"height":0.5}]}""",
            """{"boxes":[{"type":"text","markdown":"ok","x":0,"y":0,"width":1,"height":0.5},{"type":"text","x":0,"y":0.5,"width":1,"height":0.5}]}""",
            """{"content":[{"type":"text"}]}""",
            """{"content":[{"type":"text","markdown":"ok"},{"type":"table"}]}"""
        ];
        foreach (string reply in malformed)
        {
            VisionResultParser.ParseDirect(reply).IsSuccess.Should().BeFalse(reply);
            VisionResultParser.ParseRegionContent(reply).IsSuccess.Should().BeFalse(reply);
        }

        // A figure with no textual payload is still a meaningful box, not a malformed member.
        VisionResultParser.ParseDirect(
                """{"boxes":[{"type":"image","x":0,"y":0,"width":0.5,"height":0.4}]}""")
            .IsSuccess.Should().BeTrue("visual types may legitimately carry empty content");
    }

    [Fact]
    public async Task A_malformed_region_reply_fails_the_input_without_a_blank_page()
    {
        RecordingLlm llm = new(
            """{"content":[{"type":"text","markdown":"ok"},{"type":"text"}]}""");
        OcrJobInput input = new(new OcrInputId("region-0"),
            new NormalizedBBox(0.1, 0.1, 0.5, 0.5), "image/png");
        MultimodalOcrSession session = new(Config(("use-local-layout", "false")));

        Func<Task> run = () => session.RecognizeAsync(Page(input), input,
            new OcrInputImage(input.Id, input.ContentType, Png()), new VisionResources(llm),
            CancellationToken.None);
        await run.Should().ThrowAsync<InvalidOperationException>(
            "a good+bad mixed reply must not silently adopt only the good item");
    }

    [Fact]
    public void Headings_and_supported_subtypes_survive_typed_parsing()
    {
        Result<IReadOnlyList<VisionTypedItem>> direct = VisionResultParser.ParseDirect("""
            {"boxes":[
                {"type":"title","markdown":"Chapter","level":2,"x":0,"y":0,"width":1,"height":0.1},
                {"type":"image","subtype":"photo","x":0,"y":0.2,"width":0.5,"height":0.3}
            ]}
            """);
        direct.IsSuccess.Should().BeTrue();
        DocumentBoxSeed? title = VisionResultParser.ToSeed(direct.Value[0], FullPage, 0);
        title!.BoxType.Should().Be(DocumentBoxType.Title);
        title.HeadingLevel.Should().Be(2, "a supplied heading level is preserved");
        DocumentBoxSeed? image = VisionResultParser.ToSeed(direct.Value[1], FullPage, 1);
        image!.BoxType.Should().Be(DocumentBoxType.Image);
        image.SubType.Should().Be("photo", "a valid supplied subtype is kept");
    }

    [Fact]
    public async Task Unknown_block_types_keep_their_raw_type_for_adoption_diagnostics()
    {
        RecordingLlm llm = new(
            """{"boxes":[{"type":"seal","markdown":"印","x":0.0,"y":0.0,"width":0.2,"height":0.1}]}""");
        OcrJobInput input = new(new OcrInputId("page-0"), FullPage, "image/png");
        MultimodalOcrSession session = new(Config(("use-local-layout", "false")));

        OcrExtensionBoxCandidate[] candidates = await session.RecognizeAsync(Page(input), input,
            new OcrInputImage(input.Id, input.ContentType, Png()), new VisionResources(llm),
            CancellationToken.None);

        DocumentBoxSeed seed = candidates.Should().ContainSingle().Subject.Candidate;
        seed.BoxType.Should().Be("seal", "unknown types are diagnosed downstream, never flattened to text");
        seed.Payload.Should().BeOfType<TextBoxPayload>().Which.Markdown.Should().Be("印");
    }

    [Fact]
    public async Task Layout_mode_orders_regions_with_one_correction_and_maps_them_to_page_space()
    {
        // Detector order is bottom-first; the LLM must re-order to top-first.
        VisionLayoutRegion bottom = new("plain text", new NormalizedBBox(0, 0.5, 1, 0.5), 0.9);
        VisionLayoutRegion top = new("doc_title", new NormalizedBBox(0, 0, 1, 0.5), 0.95);
        FakeDetector detector = new([bottom, top]);
        RecordingLlm llm = new(
            """{"order":[0]}""", // invalid: misses region 1 -> one correction
            """{"order":[1,0]}""", // corrected permutation: title region first
            """{"content":[{"type":"title","markdown":"Heading"}]}""",
            """{"content":[{"type":"text","markdown":"Body"}]}""");
        OcrJobInput input = new(new OcrInputId("page-0"), FullPage, "image/png");
        MultimodalOcrSession session = new(
            Config(("use-local-layout", "true"), ("concurrency", "1"), ("reasoning-effort", "low")),
            detector);

        OcrExtensionBoxCandidate[] candidates = await session.RecognizeAsync(Page(input), input,
            new OcrInputImage(input.Id, input.ContentType, Png()), new VisionResources(llm),
            CancellationToken.None);

        detector.DetectCalls.Should().Be(1);
        llm.Requests.Should().HaveCount(4, "two order attempts plus one call per region");
        llm.Requests.Should().OnlyContain(request => request.ReasoningEffort == "low");
        candidates.Should().HaveCount(2);
        candidates[0].Candidate.BoxType.Should().Be(DocumentBoxType.Title);
        candidates[0].BBox.Should().Be(top.BBox);
        candidates[1].Candidate.BoxType.Should().Be(DocumentBoxType.Text);
        candidates[1].BBox.Should().Be(bottom.BBox);
        candidates[0].Candidate.SourceOrder.Should().BeLessThan(candidates[1].Candidate.SourceOrder);

        session.Dispose();
        detector.Disposed.Should().BeTrue("the session owns and deterministically disposes the detector");
        Func<Task> afterDispose = () => session.RecognizeAsync(Page(input), input,
            new OcrInputImage(input.Id, input.ContentType, Png()), new VisionResources(llm),
            CancellationToken.None);
        await afterDispose.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Layout_mode_fails_explicitly_after_a_second_invalid_order()
    {
        FakeDetector detector = new(
        [
            new VisionLayoutRegion("text", new NormalizedBBox(0, 0, 1, 0.5), 0.9),
            new VisionLayoutRegion("text", new NormalizedBBox(0, 0.5, 1, 0.5), 0.8)
        ]);
        RecordingLlm llm = new("""{"order":[0]}""", """{"order":[9,9]}""");
        OcrJobInput input = new(new OcrInputId("page-0"), FullPage, "image/png");
        MultimodalOcrSession session = new(Config(("use-local-layout", "true")), detector);

        Func<Task> run = () => session.RecognizeAsync(Page(input), input,
            new OcrInputImage(input.Id, input.ContentType, Png()), new VisionResources(llm),
            CancellationToken.None);
        await run.Should().ThrowAsync<InvalidOperationException>();
        llm.Requests.Should().HaveCount(2, "exactly one correction attempt is made");
    }

    [Fact]
    public async Task Layout_mode_without_weights_fails_instead_of_falling_back_to_direct()
    {
        // Real lazy detector (no injected fake): the missing file must fail explicitly.
        RecordingLlm llm = new("""{"boxes":[]}""");
        OcrJobInput input = new(new OcrInputId("page-0"), FullPage, "image/png");
        MultimodalOcrSession session = new(
            Config(("use-local-layout", "true"), ("model-path", "")));

        Func<Task> run = () => session.RecognizeAsync(Page(input), input,
            new OcrInputImage(input.Id, input.ContentType, Png()), new VisionResources(llm),
            CancellationToken.None);
        await run.Should().ThrowAsync<InvalidOperationException>();
        llm.Requests.Should().BeEmpty("a missing layout model never silently switches to direct mode");
    }

    [Fact]
    public async Task Detector_failure_propagates_without_a_recognizer_fallback()
    {
        FakeDetector detector = new(null);
        RecordingLlm llm = new("""{"content":[]}""");
        OcrJobInput input = new(new OcrInputId("page-0"), FullPage, "image/png");
        MultimodalOcrSession session = new(Config(("use-local-layout", "true")), detector);

        Func<Task> run = () => session.RecognizeAsync(Page(input), input,
            new OcrInputImage(input.Id, input.ContentType, Png()), new VisionResources(llm),
            CancellationToken.None);
        await run.Should().ThrowAsync<InvalidOperationException>();
        llm.Requests.Should().BeEmpty("a detector failure never falls back to the direct model call");
    }

    [Fact]
    public void Parser_preserves_typed_payloads_subtypes_and_html_tables()
    {
        Result<IReadOnlyList<VisionTypedItem>> direct = VisionResultParser.ParseDirect("""
            {"boxes":[
                {"type":"table","html":"<table><tr><td>x</td></tr></table>","x":0,"y":0,"width":1,"height":0.4},
                {"type":"algorithm","markdown":"procedure A","x":0,"y":0.5,"width":1,"height":0.2},
                {"type":"image","subtype":"photo","markdown":"a photo","x":0,"y":0.7,"width":0.4,"height":0.3}
            ]}
            """);
        direct.IsSuccess.Should().BeTrue();
        IReadOnlyList<VisionTypedItem> items = direct.Value;
        DocumentBoxSeed?[] seeds = items.Select((item, index) =>
            VisionResultParser.ToSeed(item, FullPage, index + 1)).ToArray();

        TableBoxPayload table = seeds[0]!.Payload.Should().BeOfType<TableBoxPayload>().Subject;
        table.ContentFormat.Should().Be(TableContentFormat.Html);
        table.ContentFormatVersion.Should().Be(TableContentFormat.AuthoritativeHtmlVersion);
        table.Html.Should().Contain("<table>");
        table.Markdown.Should().BeEmpty();

        seeds[1]!.BoxType.Should().Be(DocumentBoxType.Code);
        seeds[1]!.SubType.Should().Be(DocumentBoxType.Algorithm);
        seeds[1]!.Payload.Should().BeOfType<AlgorithmBoxPayload>().Which.Markdown
            .Should().Be("procedure A");

        seeds[2]!.BoxType.Should().Be(DocumentBoxType.Image);
        seeds[2]!.SubType.Should().Be("photo");
        seeds[2]!.Payload.Should().BeOfType<MediaBoxPayload>();
    }

    [Fact]
    public void Reading_order_parsing_accepts_only_complete_permutations()
    {
        VisionReadingOrder.Parse("""{"order":[1,0,2]}""", 3).IsSuccess.Should().BeTrue();
        VisionReadingOrder.Parse("""{"order":[1,1,0]}""", 3).IsSuccess.Should().BeFalse();
        VisionReadingOrder.Parse("""{"order":[1,0]}""", 3).IsSuccess.Should().BeFalse();
        VisionReadingOrder.Parse("""{"order":[1,0,9]}""", 3).IsSuccess.Should().BeFalse();
        VisionReadingOrder.Parse("not json", 3).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Options_resolve_frozen_keys_and_json_overlay()
    {
        MultimodalOcrOptions options = MultimodalOcrOptions.FromSnapshot(Config(
            ("model", """{"providerId":"anthropic","model":"claude-vision"}"""),
            ("use-local-layout", "false"), ("reasoning-effort", "high"),
            ("parameters-json", """{"concurrency":8,"imageDetail":"low"}""")));

        options.ModelSelection.Should().Be(new ExtensionModelSelection("anthropic", "claude-vision"));
        options.ReasoningEffort.Should().Be("high");
        options.UseLocalLayout.Should().BeFalse();
        options.Concurrency.Should().Be(8);
        options.ImageDetail.Should().Be("low");
    }

    /// <summary>
    /// The real compiled package driven end-to-end: main.fsx + its binary support closure
    /// activated through the catalog/manager, with <see cref="HostExtensionResources"/> backed by
    /// a recording transport — the same fake-HTTP seam the hosted-resource fixtures use. No
    /// network, weights, or real credentials are touched.
    /// </summary>
    [Fact]
    [Trait("Category", "compiled-extension")]
    public async Task Compiled_package_runs_direct_recognition_through_the_shared_driver()
    {
        string source = await File.ReadAllTextAsync(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.Infrastructure", "Ocr", "Extensions", "ocr.multimodal", "main.fsx"));
        string runtimeDirectory = TempDirectory("patchouli-ocr-multimodal");
        try
        {
            await RunAsync(source, runtimeDirectory);
        }
        finally
        {
            DeleteDirectory(runtimeDirectory);
        }

        // The collectible ALC can be reclaimed only after this inner async method's state
        // machine releases its extension-typed locals, so the body lives in its own call.
        static async Task RunAsync(string source, string runtimeDirectory)
        {
            RecordingTransport transport = new(
                """{"boxes":[{"type":"text","markdown":"Hello world","x":0.05,"y":0.1,"width":0.5,"height":0.08}]}""");
            HostExtensionResources resources = CreateHostResources(OcrHostSettings(), transport);
            await using IExtensionManager manager = new ExtensionManager(runtimeDirectory, resources,
                new FileExtensionStore(runtimeDirectory),
                new CompiledExtensionGenerationLoader(runtimeDirectory));
            ExtensionPackageSnapshot package = new("ocr.multimodal", new Dictionary<string, string>
            {
                ["main.fsx"] = source
            }, NativeOcrSupportFiles.CollectBinaries());
            ExtensionCheckResult activated = await manager.ActivateAsync(package.Id, package,
                CancellationToken.None);
            activated.Succeeded.Should().BeTrue(string.Join(Environment.NewLine,
                activated.Diagnostics.Select(issue =>
                    $"{issue.Code} {issue.Location.FileName}:{issue.Location.Line}:{issue.Location.Column}: {issue.Message}")));
            ExtensionConfigSnapshot config = Config(("use-local-layout", "false"));
            await manager.SaveConfigAsync(package.Id, config, CancellationToken.None);

            await using IExtensionLease lease = (await manager.AcquireAsync(package.Id,
                CancellationToken.None))!;
            IOcrExtension ocr = lease.Ocr!;
            ocr.EngineDefinition.EngineId.Should().Be(MultimodalOcrSession.MultimodalLlmEngineId);
            IExtensionResources runResources = resources.ForRun(config);
            (await ocr.CheckReadinessAsync(config, runResources, CancellationToken.None))
                .IsSuccess.Should().BeTrue();

            byte[] issued = Png();
            OcrJobInput input = new(new OcrInputId("page-0"), FullPage, "image/png");
            FakeOcrJob job = new(config, [Page(input)],
                new Dictionary<string, byte[]> { [input.Id.Value] = issued })
            {
                Progress = new RecordingProgress()
            };
            await ocr.RunAsync(job, runResources, CancellationToken.None);

            // The frozen OCR slot — not translation/chat — supplied provider/model/reasoning.
            LlmTransportRequest sent = transport.Requests.Should().ContainSingle().Subject;
            sent.Provider.ProviderId.Should().Be("anthropic");
            sent.Model.Should().Be("claude-vision");
            sent.ReasoningEffort.Should().Be("high");
            LlmMessagePart.LlmImagePart image = sent.Messages.SelectMany(message => message.Parts)
                .OfType<LlmMessagePart.LlmImagePart>().Should().ContainSingle().Subject;
            Convert.FromBase64String(image.DataUriOrUrl["data:image/png;base64,".Length..])
                .Should().Equal(issued);
            OcrExtensionCandidate settled = job.Committed.Should().ContainSingle().Subject;
            settled.Inputs.Should().ContainSingle().Which.State
                .Should().Be(OcrInputSettlementState.Recognized);
            DocumentBoxSeed seed = settled.Boxes.Should().ContainSingle().Subject.Candidate;
            seed.BoxType.Should().Be(DocumentBoxType.Text);
            seed.Payload.Should().BeOfType<TextBoxPayload>().Which.Markdown.Should().Be("Hello world");
            seed.BBox.Should().Be(new NormalizedBBox(0.05, 0.1, 0.5, 0.08));
            job.FailedPages.Should().BeEmpty();
            RecordingProgress progress = (RecordingProgress)job.Progress;
            progress.Values.Should().Contain(value => value.Phase == "page-ocr" &&
                                                      value.Status == ExtensionProgressStatus.Completed);
            progress.Values.Where(value => value.Phase == "direct-ocr").Select(value => value.Completed)
                .Should().Equal([0, 1], "the compiled package forwards the job sink to its vision session");
        }
    }

    [Fact]
    [Trait("Category", "compiled-extension")]
    public async Task Compiled_package_recognizes_an_explicit_crop_without_layout_weights()
    {
        string source = await File.ReadAllTextAsync(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.Infrastructure", "Ocr", "Extensions", "ocr.multimodal", "main.fsx"));
        string runtimeDirectory = TempDirectory("patchouli-ocr-multimodal-crop");
        try
        {
            await RunAsync(source, runtimeDirectory);
        }
        finally
        {
            DeleteDirectory(runtimeDirectory);
        }

        static async Task RunAsync(string source, string runtimeDirectory)
        {
            byte[] cropBytes = Png(48, 24);
            RecordingTransport transport =
                new("""{"content":[{"type":"text","markdown":"cropped"}]}""");
            HostExtensionResources resources = CreateHostResources(OcrHostSettings(), transport);
            await using IExtensionManager manager = new ExtensionManager(runtimeDirectory, resources,
                new FileExtensionStore(runtimeDirectory),
                new CompiledExtensionGenerationLoader(runtimeDirectory));
            ExtensionPackageSnapshot package = new("ocr.multimodal", new Dictionary<string, string>
            {
                ["main.fsx"] = source
            }, NativeOcrSupportFiles.CollectBinaries());
            (await manager.ActivateAsync(package.Id, package, CancellationToken.None))
                .Succeeded.Should().BeTrue();
            // Local layout on, no weights: engine readiness stays branch-neutral and an explicit
            // region input still runs.
            ExtensionConfigSnapshot config = Config(("use-local-layout", "true"),
                ("model-path", ""), ("concurrency", "1"));
            await manager.SaveConfigAsync(package.Id, config, CancellationToken.None);

            await using IExtensionLease lease = (await manager.AcquireAsync(package.Id,
                CancellationToken.None))!;
            IExtensionResources runResources = resources.ForRun(config);
            (await lease.Ocr!.CheckReadinessAsync(config, runResources, CancellationToken.None))
                .IsSuccess.Should().BeTrue("readiness must not require layout weights");
            OcrJobInput input = new(new OcrInputId("crop-0"),
                new NormalizedBBox(0.2, 0.3, 0.4, 0.25), "image/png");
            FakeOcrJob job = new(config, [Page(input)],
                new Dictionary<string, byte[]> { [input.Id.Value] = cropBytes });
            await lease.Ocr!.RunAsync(job, runResources, CancellationToken.None);

            LlmTransportRequest sent = transport.Requests.Should().ContainSingle().Subject;
            sent.Provider.ProviderId.Should().Be("anthropic");
            sent.ReasoningEffort.Should().Be("high");
            Convert.FromBase64String(sent.Messages.SelectMany(message => message.Parts)
                    .OfType<LlmMessagePart.LlmImagePart>().Single()
                    .DataUriOrUrl["data:image/png;base64,".Length..])
                .Should().Equal(cropBytes, "the crop is sent verbatim through the hosted pipeline");
            OcrExtensionCandidate settled = job.Committed.Should().ContainSingle().Subject;
            settled.Boxes.Should().ContainSingle().Which.BBox.Should().Be(input.Region);
            job.FailedPages.Should().BeEmpty();
        }
    }

    [Fact]
    [Trait("Category", "compiled-extension")]
    public async Task Compiled_package_fails_full_page_layout_without_weights_without_fallback()
    {
        string source = await File.ReadAllTextAsync(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.Infrastructure", "Ocr", "Extensions", "ocr.multimodal", "main.fsx"));
        string runtimeDirectory = TempDirectory("patchouli-ocr-multimodal-layout-missing");
        try
        {
            await RunAsync(source, runtimeDirectory);
        }
        finally
        {
            DeleteDirectory(runtimeDirectory);
        }

        static async Task RunAsync(string source, string runtimeDirectory)
        {
            RecordingTransport transport = new();
            HostExtensionResources resources = CreateHostResources(OcrHostSettings(), transport);
            await using IExtensionManager manager = new ExtensionManager(runtimeDirectory, resources,
                new FileExtensionStore(runtimeDirectory),
                new CompiledExtensionGenerationLoader(runtimeDirectory));
            ExtensionPackageSnapshot package = new("ocr.multimodal", new Dictionary<string, string>
            {
                ["main.fsx"] = source
            }, NativeOcrSupportFiles.CollectBinaries());
            (await manager.ActivateAsync(package.Id, package, CancellationToken.None))
                .Succeeded.Should().BeTrue();
            ExtensionConfigSnapshot config = Config(("use-local-layout", "true"),
                ("model-path", ""));
            await manager.SaveConfigAsync(package.Id, config, CancellationToken.None);

            await using IExtensionLease lease = (await manager.AcquireAsync(package.Id,
                CancellationToken.None))!;
            OcrJobInput input = new(new OcrInputId("page-0"), FullPage, "image/png");
            FakeOcrJob job = new(config, [Page(input)],
                new Dictionary<string, byte[]> { [input.Id.Value] = Png() });
            await lease.Ocr!.RunAsync(job, resources.ForRun(config), CancellationToken.None);

            job.FailedPages.Should().ContainSingle("whole-page layout must fail explicitly");
            job.Committed.Should().BeEmpty();
            transport.Requests.Should().BeEmpty(
                "a missing layout model never silently switches to direct model calls");
        }
    }

    private static string FixtureOnnxPath()
    {
        string path = Path.Combine(TestPaths.FromRepositoryRoot(".tmp", "tests",
            "multimodal-vision"), "fixture.onnx");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, [1, 2, 3]);
        }

        return path;
    }

    /// <summary>
    /// Hosted settings where the OCR slot is deliberately distinct from translation/chat so a
    /// compiled run proves the frozen OCR binding — never the live translation defaults.
    /// </summary>
    private static LlmAppSettings OcrHostSettings()
    {
        return LlmAppSettings.Default() with
        {
            OcrProviderId = "anthropic",
            OcrModel = "claude-vision",
            OcrReasoningEffort = "high",
            ChatProviderId = "openai",
            ChatModel = "gpt-4o-mini",
            Translation = new TranslationAppSettings { ProviderId = "openai", Model = "gpt-4o-mini" }
        };
    }

    private static HostExtensionResources CreateHostResources(LlmAppSettings settings,
        RecordingTransport transport)
    {
        return new HostExtensionResources(() => settings, new StubCredentialStore(),
            new HttpMessageInvoker(new ThrowingHandler()), _ => transport);
    }

    private static byte[] Png(int width = 120, int height = 80)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKCanvas canvas = new(bitmap);
        canvas.Clear(SKColors.White);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    private static OcrJobPage Page(OcrJobInput input)
    {
        return new OcrJobPage(new ExtensionPageIdentity(
            DocumentInstanceId.Parse("00000000-0000-0000-0000-000000000001"),
            PageId.Parse("00000000-0000-0000-0000-000000000002"),
            DocumentTreeRevisionId.Parse("00000000-0000-0000-0000-000000000003"), 1), [input]);
    }

    private static ExtensionConfigSnapshot Config(params (string Key, string Value)[] pairs)
    {
        return new ExtensionConfigSnapshot(
            pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), 1);
    }

    private static string TempDirectory(string prefix)
    {
        return Path.Combine(TestPaths.FromRepositoryRoot(".tmp", "tests", prefix),
            Guid.NewGuid().ToString("N"));
    }

    private static void DeleteDirectory(string path)
    {
        TestTempFileCleanup.DeleteDirectoryForcingExtensionUnload(path);
    }

    /// <summary>Records every request and answers with a scripted reply queue.</summary>
    private sealed class RecordingLlm : ILlmCapability
    {
        private readonly Queue<string> _replies;

        public RecordingLlm(params string[] replies)
        {
            _replies = new Queue<string>(replies);
        }

        public List<LlmRequest> Requests { get; } = [];

        public Task<LlmResponse> CompleteAsync(LlmRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (_replies)
            {
                Requests.Add(request);
                return Task.FromResult(new LlmResponse(_replies.Count > 0 ? _replies.Dequeue() : "{}"));
            }
        }
    }

    /// <summary>
    /// Mimics the host's domain-bound capability contract: ResolveLlm records the requested
    /// domain/selection and returns a wrapper that stamps the domain on every request, exactly
    /// like the host's BoundLlm does.
    /// </summary>
    private sealed class VisionResources : IExtensionResources, IDisposable
    {
        private readonly RecordingLlm _inner;

        public VisionResources(RecordingLlm inner)
        {
            _inner = inner;
            Http = new HttpClient(new UnusableHandler());
        }

        public List<(string? Domain, ExtensionModelSelection? Selection)> Resolutions { get; } = [];
        public HttpClient Http { get; }
        public ILlmCapability Llm => ResolveLlm(null, null);

        public ILlmCapability ResolveLlm(string? defaultDomain, ExtensionModelSelection? selection)
        {
            Resolutions.Add((defaultDomain, selection));
            return new DomainBoundLlm(defaultDomain, _inner);
        }

        public IExtensionResources ForRun(ExtensionConfigSnapshot config)
        {
            return this;
        }

        public Task WaitToProceedAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<string?> GetCredentialAsync(string reference,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }

        public void Dispose()
        {
            Http.Dispose();
        }

        private sealed class DomainBoundLlm(string? domain, RecordingLlm inner) : ILlmCapability
        {
            public Task<LlmResponse> CompleteAsync(LlmRequest request,
                CancellationToken cancellationToken = default)
            {
                return inner.CompleteAsync(request with { Domain = domain ?? request.Domain },
                    cancellationToken);
            }
        }

        private sealed class UnusableHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                throw new NotSupportedException("The vision fixture never uses the HTTP channel.");
            }
        }
    }

    /// <summary>Stub layout detector; null regions means "detection failed".</summary>
    private sealed class FakeDetector(IReadOnlyList<VisionLayoutRegion>? regions)
        : IVisionLayoutDetector
    {
        public int DetectCalls { get; private set; }
        public bool Disposed { get; private set; }

        public Task<Result<IReadOnlyList<VisionLayoutRegion>>> DetectAsync(byte[] imageBytes,
            CancellationToken cancellationToken = default)
        {
            DetectCalls++;
            return Task.FromResult(regions is null
                ? Result<IReadOnlyList<VisionLayoutRegion>>.Failure(AppErrorCodes.ValidationFailed,
                    "fixture detector failure")
                : Result<IReadOnlyList<VisionLayoutRegion>>.Success(regions));
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    /// <summary>
    /// Fake hosted HTTP transport: records every provider-shaped request and answers from a
    /// scripted reply queue — the same seam the host resource fixtures use, so compiled-package
    /// runs exercise the real credential/resolve/shaping pipeline without a network.
    /// </summary>
    private sealed class RecordingTransport(params string[] replies) : ILlmChatTransport
    {
        private readonly Queue<string> _replies = new(replies);

        public List<LlmTransportRequest> Requests { get; } = [];

        public Task<LlmTransportResult> SendAsync(LlmTransportRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(LlmTransportResult.Success(
                _replies.Count > 0 ? _replies.Dequeue() : "{}", request.Model, "stop",
                LlmUsage.Empty));
        }
    }

    private sealed class StubCredentialStore : ICredentialStore
    {
        public Task<Result<ProviderCredentialMetadata>> SaveAsync(string providerId, string displayName,
            string secretValue, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<ProviderCredentialMetadata>.Failure(
                AppErrorCodes.UnsupportedOperation, "The fixture store is read-only."));
        }

        public Task<Result<string>> GetActiveSecretForProviderAsync(string providerId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<string>.Success("sk-fixture"));
        }

        public Task<Result> RemoveAsync(string providerId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Failure(AppErrorCodes.UnsupportedOperation,
                "The fixture store is read-only."));
        }

        public Task<Result<IReadOnlyList<ProviderCredentialMetadata>>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<ProviderCredentialMetadata>>.Success([]));
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("The extension HTTP channel is unused in this fixture.");
        }
    }

    private sealed class RecordingProgress : IProgress<ExtensionProgress>
    {
        public List<ExtensionProgress> Values { get; } = [];

        public void Report(ExtensionProgress value)
        {
            Values.Add(value);
        }
    }

    /// <summary>Minimal <see cref="IOcrJob"/> over canned pages and image bytes.</summary>
    private sealed class FakeOcrJob(
        ExtensionConfigSnapshot config,
        IReadOnlyList<OcrJobPage> pages,
        IReadOnlyDictionary<string, byte[]> images) : IOcrJob
    {
        private int _next;

        public OcrJobInfo Info => new("job-fixture",
            DocumentInstanceId.Parse("00000000-0000-0000-0000-000000000001"), new OcrJobOptions(),
            config);

        public IProgress<ExtensionProgress>? Progress { get; init; }
        public List<OcrExtensionCandidate> Committed { get; } = [];
        public List<(ExtensionPageIdentity Page, string Code)> FailedPages { get; } = [];

        public ValueTask<OcrJobPage?> NextPageAsync(CancellationToken cancellationToken = default)
        {
            return new ValueTask<OcrJobPage?>(_next < pages.Count ? pages[_next++] : null);
        }

        public ValueTask<OcrInputImage> ReadInputImageAsync(OcrJobInput input,
            CancellationToken cancellationToken = default)
        {
            return new ValueTask<OcrInputImage>(new OcrInputImage(input.Id, input.ContentType,
                images[input.Id.Value]));
        }

        public ValueTask<OcrSubmissionReceipt> SubmitCandidatesAsync(OcrExtensionCandidate candidate,
            CancellationToken cancellationToken = default)
        {
            Committed.Add(candidate);
            return new ValueTask<OcrSubmissionReceipt>(
                OcrSubmissionReceipt.Accepted(candidate.Boxes.Count));
        }

        public ValueTask<OcrSubmissionReceipt> FailPageAsync(ExtensionPageIdentity page,
            string errorCode, string? errorMessage = null,
            CancellationToken cancellationToken = default)
        {
            FailedPages.Add((page, errorCode));
            return new ValueTask<OcrSubmissionReceipt>(OcrSubmissionReceipt.Accepted(0));
        }
    }
}
