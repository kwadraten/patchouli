using Dapper;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Ocr;

namespace Patchouli.Tests;

/// <summary>
/// Production-path coverage for the task-level OCR extension runtime: one
/// <see cref="IOcrEngineLease.RunOcrJobAsync"/> invocation per accepted scope, typed
/// <see cref="DocumentBoxSeed"/> candidates into the Working Revision import, explicit
/// native-provider failure without image fallback, real page-settlement error codes, and
/// acceptance-time generation pinning/release through <see cref="OcrEngineLeaseRegistry"/> and
/// <see cref="OcrQueueScheduler"/>.
/// </summary>
public sealed class OcrRuntimeExtensionJobTests
{
    private const string EngineId = "test-extension-engine";

    [Fact]
    public async Task Extension_progress_reaches_the_caller_before_import_and_late_callbacks_cannot_replace_it()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion version = await context.CreatePresetVersionAsync(EngineId);
        RecordingProgress progress = new();
        IProgress<ExtensionProgress>? retained = null;
        RecordingEngineLease lease = new(EngineId)
        {
            JobResult = request =>
            {
                retained = request.Progress;
                retained.Should().NotBeNull();
                retained!.Report(new ExtensionProgress("page-ocr", 1, 1,
                    Status: ExtensionProgressStatus.Completed, ItemId: "0"));
                progress.Values[^1].Fraction.Should().Be(0.5);
                progress.Values[^1].Detail.Should().StartWith("pages:1/2;page:1");
                progress.Values.Should().NotContain(report => report.Stage == OcrTaskStage.Importing);
                return new OcrDomainRunResult([], request.Pages.Select(page =>
                    new OcrPageSettlement(page.Page, OcrPageSettlementState.Accepted)).ToArray());
            }
        };

        Result<OcrRun> executed = await context.Engine.ExecuteAsync(new OcrRunExecutionRequest(
            context.Document.DocumentInstanceId, version.PresetId, context.Pages.Select(page => page.PageId).ToArray(),
            OcrRunExecutionScope.Pages, version.PresetVersionId, engineLease: lease), progress: progress);

        executed.IsSuccess.Should().BeTrue(executed.ErrorMessage);
        progress.Values.Should().Contain(new OcrTaskStageProgress(OcrTaskStage.Importing, 0.5, "pages:1/2"));
        progress.Values[^1].Should().Be(new OcrTaskStageProgress(OcrTaskStage.Importing, 1, "pages:2/2"));
        int count = progress.Values.Count;
        retained!.Report(new ExtensionProgress("page-ocr", 0, 1, ItemId: "1"));
        progress.Values.Should().HaveCount(count, "the invocation's progress sink closes before import starts");
    }

    [Fact]
    public async Task Native_text_layer_reports_each_page_and_import_as_distinct_stages()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion version = await context.CreatePresetVersionAsync(EngineId);
        RecordingProgress progress = new();
        RecordingEngineLease lease = new(EngineId)
        {
            TextLayerProvider = PdfTextLayerProviderIds.NativeXyCut,
            TextLayerResult = _ => new PdfTextLayerResult(
            [
                new PdfTextLayerBox(new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null,
                    new NormalizedBBox(0, 0, 1, 1), new TextBoxPayload("native")), [])
            ], [], [])
        };

        Result<OcrRun> executed = await context.Engine.ExecuteAsync(new OcrRunExecutionRequest(
            context.Document.DocumentInstanceId, version.PresetId, context.Pages.Select(page => page.PageId).ToArray(),
            OcrRunExecutionScope.Pages, version.PresetVersionId, engineLease: lease), progress: progress);

        executed.IsSuccess.Should().BeTrue(executed.ErrorMessage);
        lease.JobInvocations.Should().BeEmpty();
        progress.Values.Where(report => report.Stage == OcrTaskStage.ParsingTextLayer)
            .Select(report => report.Fraction).Should().Equal(0, 0.5, 1);
        progress.Values.Where(report => report.Stage == OcrTaskStage.Importing)
            .Select(report => report.Fraction).Should().Equal(0, 0.5, 1);
    }

    [Fact]
    public async Task Extension_job_invokes_once_for_all_pages_and_imports_typed_candidates()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion version = await context.CreatePresetVersionAsync(EngineId);
        RecordingEngineLease lease = new(EngineId) { JobResult = null };

        Result<OcrRun> executed = await context.Engine.ExecuteAsync(new OcrRunExecutionRequest(
            context.Document.DocumentInstanceId, version.PresetId,
            context.Pages.Select(page => page.PageId).ToArray(), OcrRunExecutionScope.Pages,
            version.PresetVersionId, engineLease: lease));

        executed.IsSuccess.Should().BeTrue(executed.ErrorMessage);
        lease.JobInvocations.Should().HaveCount(1,
            "the whole accepted scope executes through one task-level job invocation");
        OcrDomainJobRequest job = lease.JobInvocations[0];
        job.Pages.Should().HaveCount(context.Pages.Count);
        job.Pages.Select(page => page.Page.PageId).Should()
            .Equal(context.Pages.Select(page => page.PageId));

        (await context.CountAsync(
                "select count(*) from ocr_page_results where state = 'succeeded'")).Should()
            .Be(context.Pages.Count);
        (await context.ScalarAsync<string>(
                "select parameters_snapshot_json from ocr_runs limit 1")).Should()
            .Contain("\"_runtime\"").And.Contain("ext.test");
    }

    [Fact]
    public async Task Typed_candidates_reach_the_box_tree_without_flattening()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion version = await context.CreatePresetVersionAsync(EngineId);
        NormalizedBBox bbox = new(0.1, 0.1, 0.8, 0.4);
        DocumentBoxId firstBox = DocumentBoxId.New();
        RecordingEngineLease lease = new(EngineId)
        {
            JobResult = request =>
            {
                OcrJobPage page = request.Pages[0];
                OcrExtensionCandidate candidate = new(page.Page,
                    [
                        new OcrExtensionBoxCandidate(page.Inputs[0].Id,
                            new DocumentBoxSeed(null, null, 0, DocumentBoxType.Table, null, bbox,
                                new TableBoxPayload("| a | b |\n| --- | --- |\n| 1 | 2 |"))),
                        new OcrExtensionBoxCandidate(page.Inputs[0].Id,
                            new DocumentBoxSeed(null, null, 1, "equation", null, bbox,
                                new EquationBoxPayload("e = mc^2"))),
                        new OcrExtensionBoxCandidate(page.Inputs[0].Id,
                            new DocumentBoxSeed(null, null, 2, DocumentBoxType.Title, null, bbox,
                                new TextBoxPayload("Title"), 1)),
                        new OcrExtensionBoxCandidate(page.Inputs[0].Id,
                            new DocumentBoxSeed(firstBox, null, 3, DocumentBoxType.Text, null, bbox,
                                new TextBoxPayload("First physical text"))),
                        new OcrExtensionBoxCandidate(page.Inputs[0].Id,
                            new DocumentBoxSeed(null, null, 4, DocumentBoxType.Text, null, bbox,
                                new TextBoxPayload("Continuation with its own content"),
                                ContinuesFromBoxId: firstBox))
                    ],
                    [
                        new OcrInputSettlement(page.Inputs[0].Id, OcrInputSettlementState.Recognized)
                    ]);
                return new OcrDomainRunResult([candidate],
                [
                    new OcrPageSettlement(page.Page, OcrPageSettlementState.Accepted,
                        AcceptedBoxCount: 5)
                ]);
            }
        };

        Result<OcrRun> executed = await context.Engine.ExecuteAsync(new OcrRunExecutionRequest(
            context.Document.DocumentInstanceId, version.PresetId,
            [context.Pages[0].PageId], OcrRunExecutionScope.Pages,
            version.PresetVersionId, engineLease: lease));

        executed.IsSuccess.Should().BeTrue(executed.ErrorMessage);
        await context.AssertCompletedAsync(executed.Value);
        (await context.ScalarAsync<int>(
            "select count(*) from document_boxes where box_type = 'table'")).Should().Be(1);
        (await context.ScalarAsync<int>(
            "select count(*) from document_boxes where box_type = 'equation'")).Should().Be(1);
        (await context.ScalarAsync<int>(
            "select count(*) from document_boxes where heading_level = 1")).Should().Be(1);
        (await context.ScalarAsync<int>(
                "select count(*) from document_boxes where continues_from_box_id is not null")).Should()
            .Be(1);
    }

    [Fact]
    public async Task Cross_page_typed_continuations_keep_their_declared_identity_and_each_pages_own_text()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion version = await context.CreatePresetVersionAsync(EngineId);
        DocumentBoxId first = DocumentBoxId.New();
        DocumentBoxId second = DocumentBoxId.New();
        RecordingEngineLease lease = new(EngineId)
        {
            JobResult = request => new OcrDomainRunResult(
                request.Pages.Select((page, index) => new OcrExtensionCandidate(page.Page,
                        [
                            new OcrExtensionBoxCandidate(page.Inputs[0].Id,
                                new DocumentBoxSeed(index == 0 ? first : second, null, 0, DocumentBoxType.Text,
                                    null, new NormalizedBBox(0.1, 0.1, 0.8, 0.2),
                                    new TextBoxPayload(index == 0 ? "Beginning" : "Continued on the next page"),
                                    ContinuesFromBoxId: index == 0 ? null : first))
                        ],
                        [new OcrInputSettlement(page.Inputs[0].Id, OcrInputSettlementState.Recognized)]))
                    .ToArray(),
                request.Pages.Select(page => new OcrPageSettlement(page.Page, OcrPageSettlementState.Accepted,
                    AcceptedBoxCount: 1)).ToArray())
        };

        Result<OcrRun> executed = await context.Engine.ExecuteAsync(new OcrRunExecutionRequest(
            context.Document.DocumentInstanceId, version.PresetId, context.Pages.Select(page => page.PageId).ToArray(),
            OcrRunExecutionScope.Pages, version.PresetVersionId, engineLease: lease));

        executed.IsSuccess.Should().BeTrue(executed.ErrorMessage);
        await context.AssertCompletedAsync(executed.Value);
        (await context.ScalarAsync<int>("select count(*) from document_boxes where continues_from_box_id is not null"))
            .Should().Be(1);
        (await context.ScalarAsync<string>($"select continues_from_box_id from document_boxes where box_id='{second}'"))
            .Should().Be(first.ToString());
        List<string> payloads = await context.QueryAsync<string>("select payload_json from document_boxes");
        payloads.Should().Contain(payload => payload.Contains("Beginning", StringComparison.Ordinal));
        payloads.Should().Contain(payload => payload.Contains("Continued on the next page", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Native_provider_with_no_usable_text_fails_under_the_strict_missing_text_policy()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion version = await context.CreatePresetVersionAsync(EngineId);
        RecordingEngineLease lease = new(EngineId)
        {
            TextLayerProvider = PdfTextLayerProviderIds.NativeXyCut,
            TextLayerMissingTextPolicy = TextLayerMissingTextPolicies.Fail,
            TextLayerResult = _ => new PdfTextLayerResult([], [], [])
        };

        Result<OcrRun> executed = await context.Engine.ExecuteAsync(new OcrRunExecutionRequest(
            context.Document.DocumentInstanceId, version.PresetId,
            [context.Pages[0].PageId], OcrRunExecutionScope.Pages,
            version.PresetVersionId, engineLease: lease));

        executed.IsSuccess.Should().BeTrue(executed.ErrorMessage);
        lease.JobInvocations.Should().BeEmpty(
            "the strict missing-text policy keeps a provider-empty page an explicit failure");
        lease.ReadinessProbes.Should().Be(0,
            "pure-native execution must not validate image engine readiness");
        (await context.ScalarAsync<string>(
                "select error_code from ocr_page_results limit 1")).Should()
            .Be(PdfTextLayerDiagnosticCodes.NativeTextMissing);
    }

    [Fact]
    public async Task Missing_native_text_falls_back_to_a_whole_page_image_input_by_default()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion version = await context.CreatePresetVersionAsync(EngineId);
        RecordingEngineLease lease = new(EngineId)
        {
            TextLayerProvider = PdfTextLayerProviderIds.NativeXyCut,
            TextLayerOutcome = _ => Result<PdfTextLayerResult>.Failure(
                PdfTextLayerDiagnosticCodes.NativeTextMissing, "no usable text layer")
        };

        Result<OcrRun> executed = await context.Engine.ExecuteAsync(new OcrRunExecutionRequest(
            context.Document.DocumentInstanceId, version.PresetId,
            [context.Pages[0].PageId], OcrRunExecutionScope.Pages,
            version.PresetVersionId, engineLease: lease));

        executed.IsSuccess.Should().BeTrue(executed.ErrorMessage);
        await context.AssertCompletedAsync(executed.Value);
        OcrDomainJobRequest job = lease.JobInvocations.Should().ContainSingle(
            "the default missing-text policy routes the page to the image engine").Subject;
        OcrJobInput input = job.Pages.Should().ContainSingle().Subject.Inputs
            .Should().ContainSingle().Subject;
        input.Kind.Should().Be(OcrInputKinds.PageImage);
        input.Region.Should().Be(new NormalizedBBox(0, 0, 1, 1));
        (await context.ScalarAsync<string>(
                "select state from ocr_page_results limit 1")).Should()
            .Be(OcrPageResultState.Succeeded);
    }

    [Fact]
    public async Task Empty_provider_result_falls_back_to_a_whole_page_image_input_by_default()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion version = await context.CreatePresetVersionAsync(EngineId);
        RecordingEngineLease lease = new(EngineId)
        {
            TextLayerProvider = PdfTextLayerProviderIds.NativeXyCut,
            TextLayerResult = _ => new PdfTextLayerResult([], [], [])
        };

        Result<OcrRun> executed = await context.Engine.ExecuteAsync(new OcrRunExecutionRequest(
            context.Document.DocumentInstanceId, version.PresetId,
            [context.Pages[0].PageId], OcrRunExecutionScope.Pages,
            version.PresetVersionId, engineLease: lease));

        executed.IsSuccess.Should().BeTrue(executed.ErrorMessage);
        await context.AssertCompletedAsync(executed.Value);
        OcrDomainJobRequest job = lease.JobInvocations.Should().ContainSingle().Subject;
        job.Pages.Should().ContainSingle().Subject.Inputs.Should().ContainSingle()
            .Which.Kind.Should().Be(OcrInputKinds.PageImage);
    }

    [Fact]
    public async Task Missing_native_text_failure_stays_a_failure_under_the_strict_policy()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion version = await context.CreatePresetVersionAsync(EngineId);
        RecordingEngineLease lease = new(EngineId)
        {
            TextLayerProvider = PdfTextLayerProviderIds.NativeXyCut,
            TextLayerMissingTextPolicy = TextLayerMissingTextPolicies.Fail,
            TextLayerOutcome = _ => Result<PdfTextLayerResult>.Failure(
                PdfTextLayerDiagnosticCodes.NativeTextMissing, "no usable text layer")
        };

        Result<OcrRun> executed = await context.Engine.ExecuteAsync(new OcrRunExecutionRequest(
            context.Document.DocumentInstanceId, version.PresetId,
            [context.Pages[0].PageId], OcrRunExecutionScope.Pages,
            version.PresetVersionId, engineLease: lease));

        executed.IsSuccess.Should().BeTrue(executed.ErrorMessage);
        lease.JobInvocations.Should().BeEmpty();
        (await context.ScalarAsync<string>(
                "select error_code from ocr_page_results limit 1")).Should()
            .Be(PdfTextLayerDiagnosticCodes.NativeTextMissing);
    }

    [Fact]
    public async Task Content_level_provider_failures_fall_back_to_a_whole_page_image_input()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion version = await context.CreatePresetVersionAsync(EngineId);
        RecordingEngineLease lease = new(EngineId)
        {
            TextLayerProvider = PdfTextLayerProviderIds.NativeXyCut,
            TextLayerOutcome = _ => Result<PdfTextLayerResult>.Failure(
                PdfTextLayerDiagnosticCodes.NativeTextUnusable, "corrupt glyph stream")
        };

        Result<OcrRun> executed = await context.Engine.ExecuteAsync(new OcrRunExecutionRequest(
            context.Document.DocumentInstanceId, version.PresetId,
            [context.Pages[0].PageId], OcrRunExecutionScope.Pages,
            version.PresetVersionId, engineLease: lease));

        executed.IsSuccess.Should().BeTrue(executed.ErrorMessage);
        await context.AssertCompletedAsync(executed.Value);
        OcrDomainJobRequest job = lease.JobInvocations.Should().ContainSingle(
            "damaged or unusable text falls back to image OCR like missing text").Subject;
        job.Pages.Should().ContainSingle().Subject.Inputs.Should().ContainSingle()
            .Which.Kind.Should().Be(OcrInputKinds.PageImage);
        (await context.ScalarAsync<string>(
                "select state from ocr_page_results limit 1")).Should()
            .Be(OcrPageResultState.Succeeded);
    }

    [Fact]
    public async Task Provider_error_diagnostics_fall_back_to_a_whole_page_image_input()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion version = await context.CreatePresetVersionAsync(EngineId);
        RecordingEngineLease lease = new(EngineId)
        {
            TextLayerProvider = PdfTextLayerProviderIds.NativeXyCut,
            TextLayerResult = _ => new PdfTextLayerResult([], [],
            [
                new PdfTextLayerDiagnostic(PdfTextLayerDiagnosticCodes.DuplicateGlyphOwnership,
                    "glyph claimed twice", PdfTextLayerDiagnosticSeverity.Error)
            ])
        };

        Result<OcrRun> executed = await context.Engine.ExecuteAsync(new OcrRunExecutionRequest(
            context.Document.DocumentInstanceId, version.PresetId,
            [context.Pages[0].PageId], OcrRunExecutionScope.Pages,
            version.PresetVersionId, engineLease: lease));

        executed.IsSuccess.Should().BeTrue(executed.ErrorMessage);
        await context.AssertCompletedAsync(executed.Value);
        OcrDomainJobRequest job = lease.JobInvocations.Should().ContainSingle(
            "an untrustworthy provider page output falls back to image OCR").Subject;
        job.Pages.Should().ContainSingle().Subject.Inputs.Should().ContainSingle()
            .Which.Kind.Should().Be(OcrInputKinds.PageImage);
    }

    [Fact]
    public async Task Content_provider_failure_stays_a_failure_under_the_strict_policy()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion version = await context.CreatePresetVersionAsync(EngineId);
        RecordingEngineLease lease = new(EngineId)
        {
            TextLayerProvider = PdfTextLayerProviderIds.NativeXyCut,
            TextLayerMissingTextPolicy = TextLayerMissingTextPolicies.Fail,
            TextLayerOutcome = _ => Result<PdfTextLayerResult>.Failure(
                PdfTextLayerDiagnosticCodes.NativeTextUnusable, "corrupt glyph stream")
        };

        Result<OcrRun> executed = await context.Engine.ExecuteAsync(new OcrRunExecutionRequest(
            context.Document.DocumentInstanceId, version.PresetId,
            [context.Pages[0].PageId], OcrRunExecutionScope.Pages,
            version.PresetVersionId, engineLease: lease));

        executed.IsSuccess.Should().BeTrue(executed.ErrorMessage);
        lease.JobInvocations.Should().BeEmpty(
            "the strict policy never substitutes image OCR for a failed text layer");
        (await context.ScalarAsync<string>(
                "select error_code from ocr_page_results limit 1")).Should()
            .Be(PdfTextLayerDiagnosticCodes.NativeTextUnusable);
    }

    [Fact]
    public async Task Provider_infrastructure_failures_stay_explicit_failures()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion version = await context.CreatePresetVersionAsync(EngineId);
        RecordingEngineLease lease = new(EngineId)
        {
            TextLayerProvider = PdfTextLayerProviderIds.NativeXyCut,
            TextLayerOutcome = _ => Result<PdfTextLayerResult>.Failure(
                AppErrorCodes.InvalidState, "The pinned PDF text-layer generation was released.")
        };

        Result<OcrRun> executed = await context.Engine.ExecuteAsync(new OcrRunExecutionRequest(
            context.Document.DocumentInstanceId, version.PresetId,
            [context.Pages[0].PageId], OcrRunExecutionScope.Pages,
            version.PresetVersionId, engineLease: lease));

        executed.IsSuccess.Should().BeTrue(executed.ErrorMessage);
        lease.JobInvocations.Should().BeEmpty(
            "a provider invocation that never ran cannot be substituted by image OCR");
        (await context.ScalarAsync<string>(
                "select error_code from ocr_page_results limit 1")).Should()
            .Be(AppErrorCodes.InvalidState);
    }

    [Fact]
    public async Task Explicit_ocr_regions_are_the_only_image_branch_of_a_text_provider()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion version = await context.CreatePresetVersionAsync(EngineId);
        NormalizedBBox region = new(0.2, 0.2, 0.5, 0.4);
        RecordingEngineLease lease = new(EngineId)
        {
            TextLayerProvider = PdfTextLayerProviderIds.VisualGuided,
            TextLayerResult = _ => new PdfTextLayerResult(
                [
                    new PdfTextLayerBox(new DocumentBoxSeed(null, null, 0, "text", null,
                        new NormalizedBBox(0, 0, 1, 0.1), new TextBoxPayload("native text")), [])
                ],
                [new PdfTextRequiredRegion(region, "non_textual_content")], []),
            JobResult = request =>
            {
                OcrJobPage page = request.Pages[0];
                return new OcrDomainRunResult(
                [
                    new OcrExtensionCandidate(page.Page,
                        [
                            new OcrExtensionBoxCandidate(page.Inputs[0].Id,
                                new DocumentBoxSeed(null, null, 0, DocumentBoxType.Image, null, region,
                                    new MediaBoxPayload(null, "ocr figure")))
                        ],
                        [
                            new OcrInputSettlement(page.Inputs[0].Id,
                                OcrInputSettlementState.Recognized)
                        ])
                ], [
                    new OcrPageSettlement(page.Page, OcrPageSettlementState.Accepted,
                        AcceptedBoxCount: 1)
                ]);
            }
        };

        Result<OcrRun> executed = await context.Engine.ExecuteAsync(new OcrRunExecutionRequest(
            context.Document.DocumentInstanceId, version.PresetId,
            [context.Pages[0].PageId], OcrRunExecutionScope.Pages,
            version.PresetVersionId, engineLease: lease));

        executed.IsSuccess.Should().BeTrue(executed.ErrorMessage);
        await context.AssertCompletedAsync(executed.Value);
        OcrDomainJobRequest job = lease.JobInvocations.Should().ContainSingle().Subject;
        OcrJobPage issued = job.Pages.Should().ContainSingle().Subject;
        issued.Inputs.Should().ContainSingle().Which.Region.Should().Be(region,
            "only the provider-declared required regions reach the image engine");
        (await context.ScalarAsync<int>(
            "select count(*) from document_boxes")).Should().Be(2,
            "native boxes and region OCR boxes merge without duplication");
    }

    [Fact]
    public async Task A_terminal_conflict_settlement_records_the_real_error_code()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion version = await context.CreatePresetVersionAsync(EngineId);
        RecordingEngineLease lease = new(EngineId)
        {
            JobResult = request =>
                new OcrDomainRunResult([],
                [
                    new OcrPageSettlement(request.Pages[0].Page, OcrPageSettlementState.Failed,
                        OcrSettlementCodes.SourceRevisionConflict,
                        "The page's source revision moved while the job ran.")
                ])
        };

        Result<OcrRun> executed = await context.Engine.ExecuteAsync(new OcrRunExecutionRequest(
            context.Document.DocumentInstanceId, version.PresetId,
            [context.Pages[0].PageId], OcrRunExecutionScope.Pages,
            version.PresetVersionId, engineLease: lease));

        executed.IsSuccess.Should().BeTrue(executed.ErrorMessage);
        (await context.ScalarAsync<string>(
                "select error_code from ocr_page_results limit 1")).Should()
            .Be(OcrSettlementCodes.SourceRevisionConflict);
        (await context.ScalarAsync<string>("select state from ocr_runs limit 1")).Should()
            .Be(OcrRunState.Failed);
    }

    [Fact]
    public async Task Region_scope_issues_one_region_input_to_one_invocation()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion version = await context.CreatePresetVersionAsync(EngineId);
        NormalizedBBox region = new(0.1, 0.2, 0.3, 0.4);
        RecordingEngineLease lease = new(EngineId)
        {
            JobResult = request =>
            {
                OcrJobPage page = request.Pages[0];
                return new OcrDomainRunResult(
                [
                    new OcrExtensionCandidate(page.Page,
                        [
                            new OcrExtensionBoxCandidate(page.Inputs[0].Id,
                                new DocumentBoxSeed(null, null, 0, "text", null, region,
                                    new TextBoxPayload("region text")))
                        ],
                        [
                            new OcrInputSettlement(page.Inputs[0].Id,
                                OcrInputSettlementState.Recognized)
                        ])
                ], [
                    new OcrPageSettlement(page.Page, OcrPageSettlementState.Accepted,
                        AcceptedBoxCount: 1)
                ]);
            }
        };

        Result<OcrRun> executed = await context.Engine.ExecuteAsync(new OcrRunExecutionRequest(
            context.Document.DocumentInstanceId, version.PresetId,
            [context.Pages[0].PageId], OcrRunExecutionScope.Region, version.PresetVersionId,
            region, engineLease: lease));

        executed.IsSuccess.Should().BeTrue(executed.ErrorMessage);
        OcrDomainJobRequest job = lease.JobInvocations.Should().ContainSingle().Subject;
        OcrJobPage issued = job.Pages.Should().ContainSingle().Subject;
        issued.Inputs.Should().ContainSingle().Which.Region.Should().Be(region);
        (await context.ScalarAsync<string>(
            "select state from ocr_page_results limit 1")).Should().Be("succeeded");
    }

    [Fact]
    public async Task Acceptance_pins_the_generation_before_the_task_is_visible()
    {
        RecordingEngineLease lease = new(EngineId);
        FakeEngineRuntime runtime = new(lease);
        OcrQueueTaskId? taskId = null;
        OcrEngineLeaseRegistry registry = new(runtime,
            (id, _) => Task.FromResult(Result<OcrPresetVersion>.Success(
                new OcrPresetVersion(id, OcrPresetId.New(), EngineId, "model", null, "{}", false,
                    DateTimeOffset.UnixEpoch))));
        RecordingExecutor executor = new(registry);
        OcrQueueScheduler scheduler = new(LibraryId.New(), new FixedClock(DateTimeOffset.UnixEpoch),
            executor, limits: new OcrQueueLimits(1, 1, 1, 1, 1), engineLeases: registry);

        Result<OcrQueueTask> queued = await scheduler.EnqueueAsync(new OcrQueueTaskRequest(
            DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], OcrQueueTaskKind.Pages,
            EngineId, OcrAdapterKind.LocalLibrary, null, OcrQueuePriority.UserStartedDocument,
            PresetVersionId: OcrPresetVersionId.New(), Source: OcrQueueSource.UiPages));

        queued.IsSuccess.Should().BeTrue(queued.ErrorMessage);
        taskId = queued.Value.TaskId;
        runtime.Acquired.Should().Be(1, "acceptance pins the engine generation");
        registry.Get(taskId.Value).Should().BeSameAs(lease,
            "the pinned lease is retained while the task waits");

        await scheduler.RunOneSchedulingTickAsync();
        await WaitForAsync(async () =>
            await scheduler.GetTaskAsync(taskId.Value) is { IsSuccess: true } task &&
            task.Value.State == OcrQueueTaskState.Succeeded);
        executor.Leases.Should().ContainSingle().Which.Should().BeSameAs(lease,
            "the executor receives the acceptance-time pin, never a fresh acquisition");
        lease.Disposed.Should().Be(1, "terminal completion releases the pin");
        registry.Get(taskId.Value).Should().BeNull();

        await registry.DisposeAsync();
    }

    [Fact]
    public async Task Queued_cancellation_releases_the_pin()
    {
        RecordingEngineLease lease = new(EngineId);
        OcrEngineLeaseRegistry registry = new(new FakeEngineRuntime(lease),
            (id, _) => Task.FromResult(Result<OcrPresetVersion>.Success(
                new OcrPresetVersion(id, OcrPresetId.New(), EngineId, "model", null, "{}", false,
                    DateTimeOffset.UnixEpoch))));
        OcrQueueScheduler scheduler = new(LibraryId.New(), new FixedClock(DateTimeOffset.UnixEpoch),
            new RecordingExecutor(registry), engineLeases: registry);

        Result<OcrQueueTask> queued = await scheduler.EnqueueAsync(new OcrQueueTaskRequest(
            DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], OcrQueueTaskKind.Pages,
            EngineId, OcrAdapterKind.LocalLibrary, null, OcrQueuePriority.UserStartedDocument,
            PresetVersionId: OcrPresetVersionId.New(), Source: OcrQueueSource.UiPages));
        (await scheduler.CancelTaskAsync(queued.Value.TaskId)).IsSuccess.Should().BeTrue();

        lease.Disposed.Should().Be(1);
        registry.Get(queued.Value.TaskId).Should().BeNull();
        await registry.DisposeAsync();
    }

    [Fact]
    public async Task A_failed_acquisition_never_exposes_a_task()
    {
        FakeEngineRuntime runtime = new(null);
        OcrEngineLeaseRegistry registry = new(runtime,
            (id, _) => Task.FromResult(Result<OcrPresetVersion>.Success(
                new OcrPresetVersion(id, OcrPresetId.New(), EngineId, "model", null, "{}", false,
                    DateTimeOffset.UnixEpoch))));
        OcrQueueScheduler scheduler = new(LibraryId.New(), new FixedClock(DateTimeOffset.UnixEpoch),
            new RecordingExecutor(registry), engineLeases: registry);

        Result<OcrQueueTask> queued = await scheduler.EnqueueAsync(new OcrQueueTaskRequest(
            DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], OcrQueueTaskKind.Pages,
            EngineId, OcrAdapterKind.LocalLibrary, null, OcrQueuePriority.UserStartedDocument,
            PresetVersionId: OcrPresetVersionId.New(), Source: OcrQueueSource.UiPages));

        queued.IsFailure.Should().BeTrue(
            "a task whose engine generation cannot be pinned is never accepted or exposed");
        await registry.DisposeAsync();
    }

    [Fact]
    public async Task Registry_dispose_releases_every_retained_pin()
    {
        RecordingEngineLease first = new(EngineId);
        RecordingEngineLease second = new(EngineId);
        RecordingEngineLease[] leases = [first, second];
        int index = 0;
        FakeEngineRuntime runtime = new(null)
        {
            AcquireBody = (_, _) => Task.FromResult(
                Result<IOcrEngineLease>.Success(leases[index++]))
        };
        OcrEngineLeaseRegistry registry = new(runtime,
            (id, _) => Task.FromResult(Result<OcrPresetVersion>.Success(
                new OcrPresetVersion(id, OcrPresetId.New(), EngineId, "model", null, "{}", false,
                    DateTimeOffset.UnixEpoch))));

        foreach (RecordingEngineLease _ in leases)
        {
            OcrQueueTask task = new(OcrQueueTaskId.New(), LibraryId.New(),
                DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()],
                OcrQueueTaskKind.Pages, EngineId, OcrAdapterKind.LocalLibrary, null,
                OcrQueuePriority.UserStartedDocument, DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch, OcrQueueTaskState.Queued, 0, 3, null, null, null, null,
                PresetVersionId: OcrPresetVersionId.New());
            (await registry.AcquireAsync(task, default)).IsSuccess.Should().BeTrue();
        }

        await registry.DisposeAsync();
        first.Disposed.Should().Be(1);
        second.Disposed.Should().Be(1);
    }

    [Fact]
    public async Task A_page_with_a_failed_input_is_never_imported_as_a_success()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion version = await context.CreatePresetVersionAsync(EngineId);
        RecordingEngineLease lease = new(EngineId)
        {
            JobResult = request =>
            {
                OcrJobPage page = request.Pages[0];
                return new OcrDomainRunResult(
                [
                    new OcrExtensionCandidate(page.Page,
                        [
                            new OcrExtensionBoxCandidate(page.Inputs[0].Id,
                                new DocumentBoxSeed(null, null, 0, "text", null,
                                    page.Inputs[0].Region, new TextBoxPayload("ok")))
                        ],
                        [
                            new OcrInputSettlement(page.Inputs[0].Id,
                                OcrInputSettlementState.Recognized),
                            new OcrInputSettlement(new OcrInputId("extra"), OcrInputSettlementState.Failed,
                                "decode_failed", "The image decoder rejected this input.")
                        ])
                ], [
                    new OcrPageSettlement(page.Page, OcrPageSettlementState.Accepted,
                        FailedInputCount: 1)
                ]);
            }
        };

        Result<OcrRun> executed = await context.Engine.ExecuteAsync(new OcrRunExecutionRequest(
            context.Document.DocumentInstanceId, version.PresetId,
            [context.Pages[0].PageId], OcrRunExecutionScope.Pages,
            version.PresetVersionId, engineLease: lease));

        executed.IsSuccess.Should().BeTrue(executed.ErrorMessage);
        (await context.ScalarAsync<string>(
            "select error_code from ocr_page_results limit 1")).Should().Be("ocr_partial_inputs");
        (await context.ScalarAsync<string>(
                "select error_message from ocr_page_results limit 1")).Should()
            .Contain("decode_failed");
        (await context.ScalarAsync<int>("select count(*) from document_boxes")).Should().Be(0,
            "failed-input candidates are never imported into the tree");
    }

    [Fact]
    public async Task A_page_issued_but_never_settled_is_an_explicit_failure()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion version = await context.CreatePresetVersionAsync(EngineId);
        RecordingEngineLease lease = new(EngineId)
        {
            JobResult = _ => new OcrDomainRunResult([], [])
        };

        Result<OcrRun> executed = await context.Engine.ExecuteAsync(new OcrRunExecutionRequest(
            context.Document.DocumentInstanceId, version.PresetId,
            [context.Pages[0].PageId], OcrRunExecutionScope.Pages,
            version.PresetVersionId, engineLease: lease));

        executed.IsSuccess.Should().BeTrue(executed.ErrorMessage);
        (await context.ScalarAsync<string>(
            "select error_code from ocr_page_results limit 1")).Should().Be("ocr_page_unsettled",
            "a missing settlement must not become a blank-page success");
        (await context.ScalarAsync<int>("select count(*) from document_boxes")).Should().Be(0);
    }

    [Fact]
    public async Task Provider_regions_fill_their_declared_anchor_slot_in_reading_order()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion version = await context.CreatePresetVersionAsync(EngineId);
        NormalizedBBox region = new(0.2, 0.2, 0.5, 0.4);
        RecordingEngineLease lease = new(EngineId)
        {
            IsBuiltIn = true,
            TextLayerProvider = PdfTextLayerProviderIds.VisualGuided,
            TextLayerResult = _ => new PdfTextLayerResult(
                [
                    new PdfTextLayerBox(new DocumentBoxSeed(null, null, 0, "text", null,
                            new NormalizedBBox(0, 0, 1, 0.1), new TextBoxPayload("first")),
                        [new PdfTextAnchor(0, 5)]),
                    new PdfTextLayerBox(new DocumentBoxSeed(null, null, 1, "text", null,
                            new NormalizedBBox(0, 0.9, 1, 0.1), new TextBoxPayload("last")),
                        [new PdfTextAnchor(100, 4)])
                ],
                [
                    new PdfTextRequiredRegion(region, "non_textual_content",
                        Anchors: [new PdfTextAnchor(50, 1)])
                ], []),
            BuiltInRegionRunner = (jobPage, input, _, _) =>
                Task.FromResult(Result<IReadOnlyList<OcrExtensionBoxCandidate>>.Success(
                [
                    new OcrExtensionBoxCandidate(input.Id,
                        new DocumentBoxSeed(null, null, 0, DocumentBoxType.Image, null, region,
                            new MediaBoxPayload(null, "middle-hole figure")))
                ]))
        };

        Result<OcrRun> executed = await context.Engine.ExecuteAsync(new OcrRunExecutionRequest(
            context.Document.DocumentInstanceId, version.PresetId,
            [context.Pages[0].PageId], OcrRunExecutionScope.Pages,
            version.PresetVersionId, engineLease: lease));

        executed.IsSuccess.Should().BeTrue(executed.ErrorMessage);
        lease.JobInvocations.Should().BeEmpty(
            "a built-in engine resolves explicit regions through its region transport, " +
            "never a second extension job");
        lease.ReadinessProbes.Should().Be(0,
            "built-in region execution must not probe extension image readiness");
        await context.AssertCompletedAsync(executed.Value);
        List<string> types = await context.QueryAsync<string>(
            """
            with recursive ordered(box_id, next_sibling_box_id, box_type, ordinal) as (
              select b.box_id, b.next_sibling_box_id, b.box_type, 0 from document_boxes b
              where b.parent_box_id is null and not exists (
                select 1 from document_boxes p where p.next_sibling_box_id = b.box_id)
              union all
              select b.box_id, b.next_sibling_box_id, b.box_type, o.ordinal + 1
              from document_boxes b join ordered o on b.box_id = o.next_sibling_box_id
            ) select box_type from ordered order by ordinal;
            """);
        types.Should().Equal(new[] { "text", "image", "text" },
            "the region OCR box lands in its declared anchor slot between the native boxes");
    }

    [Fact]
    public async Task Re_pinning_a_task_releases_the_superseded_lease()
    {
        RecordingEngineLease first = new(EngineId);
        RecordingEngineLease second = new(EngineId);
        RecordingEngineLease[] leases = [first, second];
        int index = 0;
        FakeEngineRuntime runtime = new(null)
        {
            AcquireBody = (_, _) => Task.FromResult(
                Result<IOcrEngineLease>.Success(leases[index++ % leases.Length]))
        };
        OcrEngineLeaseRegistry registry = new(runtime,
            (id, _) => Task.FromResult(Result<OcrPresetVersion>.Success(
                new OcrPresetVersion(id, OcrPresetId.New(), EngineId, "model", null, "{}", false,
                    DateTimeOffset.UnixEpoch))));
        OcrQueueTask task = new(OcrQueueTaskId.New(), LibraryId.New(),
            DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()],
            OcrQueueTaskKind.Pages, EngineId, OcrAdapterKind.LocalLibrary, null,
            OcrQueuePriority.UserStartedDocument, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, OcrQueueTaskState.Queued, 0, 3, null, null, null, null,
            PresetVersionId: OcrPresetVersionId.New());

        (await registry.AcquireAsync(task, default)).IsSuccess.Should().BeTrue();
        (await registry.AcquireAsync(task, default)).IsSuccess.Should().BeTrue();

        first.Disposed.Should().Be(1, "a re-pin never leaks the superseded generation");
        registry.Get(task.TaskId).Should().BeSameAs(second);
        await registry.DisposeAsync();
    }

    [Fact]
    public async Task Registry_release_awaits_a_draining_lease_dispose()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingEngineLease lease = new(EngineId) { DisposeGate = gate };
        OcrEngineLeaseRegistry registry = new(new FakeEngineRuntime(lease),
            (id, _) => Task.FromResult(Result<OcrPresetVersion>.Success(
                new OcrPresetVersion(id, OcrPresetId.New(), EngineId, "model", null, "{}", false,
                    DateTimeOffset.UnixEpoch))));
        OcrQueueTask task = new(OcrQueueTaskId.New(), LibraryId.New(),
            DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()],
            OcrQueueTaskKind.Pages, EngineId, OcrAdapterKind.LocalLibrary, null,
            OcrQueuePriority.UserStartedDocument, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, OcrQueueTaskState.Queued, 0, 3, null, null, null, null,
            PresetVersionId: OcrPresetVersionId.New());
        (await registry.AcquireAsync(task, default)).IsSuccess.Should().BeTrue();

        Task release = registry.ReleaseAsync(task.TaskId);
        release.IsCompleted.Should().BeFalse(
            "release drains the lease's own in-flight work, not just the registry slot");
        Task shutdown = registry.DisposeAsync().AsTask();
        shutdown.IsCompleted.Should().BeFalse("shutdown owns a release even after its dictionary entry was removed");
        registry.DisposeAsync().AsTask().Should().BeSameAs(shutdown);
        gate.SetResult();
        await Task.WhenAll(release, shutdown).WaitAsync(TimeSpan.FromSeconds(15));
        registry.Get(task.TaskId).Should().BeNull();
        await registry.DisposeAsync();
    }

    [Fact]
    public async Task Registry_shutdown_drains_a_generation_acquisition_that_ignores_cancellation()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingEngineLease lease = new(EngineId);
        FakeEngineRuntime runtime = new(null)
        {
            AcquireBody = async (_, _) =>
            {
                started.TrySetResult();
                await finish.Task;
                return Result<IOcrEngineLease>.Success(lease);
            }
        };
        OcrEngineLeaseRegistry registry = new(runtime, (id, _) => Task.FromResult(
            Result<OcrPresetVersion>.Success(new OcrPresetVersion(id, OcrPresetId.New(), EngineId,
                "model", null, "{}", false, DateTimeOffset.UnixEpoch))));
        OcrQueueTask task = new(OcrQueueTaskId.New(), LibraryId.New(), DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], OcrQueueTaskKind.Pages, EngineId, OcrAdapterKind.LocalLibrary,
            null, OcrQueuePriority.UserStartedDocument, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            OcrQueueTaskState.Queued, 0, 3, null, null, null, null,
            PresetVersionId: OcrPresetVersionId.New());
        Task<Result<IOcrEngineLease?>> acquisition = registry.AcquireAsync(task);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Task shutdown = registry.DisposeAsync().AsTask();
        try
        {
            shutdown.IsCompleted.Should().BeFalse();
            registry.DisposeAsync().AsTask().Should().BeSameAs(shutdown);
            (await registry.AcquireAsync(task)).IsFailure.Should().BeTrue();
        }
        finally
        {
            finish.TrySetResult();
        }

        (await acquisition.WaitAsync(TimeSpan.FromSeconds(15))).IsFailure.Should().BeTrue();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(15));
        lease.Disposed.Should().Be(1);
        registry.Get(task.TaskId).Should().BeNull();
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (int i = 0; i < 100; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Condition was not reached in time.");
    }

    private sealed class RecordingProgress : IProgress<OcrTaskStageProgress>
    {
        public List<OcrTaskStageProgress> Values { get; } = [];

        public void Report(OcrTaskStageProgress value)
        {
            Values.Add(value);
        }
    }

    private sealed class RecordingEngineLease(string engineId) : IOcrEngineLease
    {
        public List<OcrDomainJobRequest> JobInvocations { get; } = [];
        public int ReadinessProbes { get; private set; }
        public int Disposed { get; private set; }
        public string? TextLayerProvider { get; init; }
        public Func<PageId, PdfTextLayerResult>? TextLayerResult { get; init; }
        public Func<PageId, Result<PdfTextLayerResult>>? TextLayerOutcome { get; init; }

        public string TextLayerMissingTextPolicy { get; init; } =
            TextLayerMissingTextPolicies.PageOcr;

        public Func<OcrDomainJobRequest, OcrDomainRunResult>? JobResult { get; init; }

        public OcrEngineDefinition Definition { get; } = new(engineId, "Test Extension",
            OcrAdapterKind.LocalLibrary, "test-model", [OcrInputKinds.ImageFile]);

        public string EngineId { get; } = engineId;
        public bool IsBuiltIn { get; init; }

        public Func<OcrJobPage, OcrJobInput, OcrPresetVersion, CancellationToken,
            Task<Result<IReadOnlyList<OcrExtensionBoxCandidate>>>>? BuiltInRegionRunner { get; init; }

        public TaskCompletionSource? DisposeGate { get; init; }
        public string? EngineExtensionId => "ext.test";
        public string? EngineFingerprint => "fingerprint";
        public int? EngineConfigurationVersion => 1;
        public string? ModelIdentity => "test-model";
        public string? ModelDigest => null;
        public ExtensionConfigSnapshot? EngineConfig => null;
        public string? TextLayerProviderId => TextLayerProvider;
        public string? TextLayerExtensionId => null;
        public string? TextLayerFingerprint => null;
        public int? TextLayerConfigurationVersion => null;
        public ExtensionConfigSnapshot? TextLayerConfig => null;

        public Task<Result> CheckReadinessAsync(CancellationToken cancellationToken = default)
        {
            ReadinessProbes++;
            return Task.FromResult(Result.Success());
        }

        public Task<Result<OcrDomainRunResult>> RunOcrJobAsync(OcrDomainJobRequest request,
            CancellationToken cancellationToken = default)
        {
            JobInvocations.Add(request);
            if (JobResult is { } job)
            {
                return Task.FromResult(Result<OcrDomainRunResult>.Success(job(request)));
            }

            List<OcrExtensionCandidate> candidates = [];
            List<OcrPageSettlement> settlements = [];
            foreach (OcrJobPage page in request.Pages)
            {
                candidates.Add(new OcrExtensionCandidate(page.Page,
                [
                    new OcrExtensionBoxCandidate(page.Inputs[0].Id,
                        new DocumentBoxSeed(null, null, 0, "text", null, page.Inputs[0].Region,
                            new TextBoxPayload("recognized text")))
                ], page.Inputs.Select(input =>
                        new OcrInputSettlement(input.Id, OcrInputSettlementState.Recognized))
                    .ToArray()));
                settlements.Add(new OcrPageSettlement(page.Page, OcrPageSettlementState.Accepted,
                    AcceptedBoxCount: 1));
            }

            return Task.FromResult(Result<OcrDomainRunResult>.Success(
                new OcrDomainRunResult(candidates, settlements)));
        }

        public Task<Result<PdfTextLayerResult>> ParseTextLayerAsync(DocumentInstanceId documentId,
            PageId pageId, int pageIndex, DocumentTreeRevisionId revisionId, NormalizedBBox? region,
            CancellationToken cancellationToken = default)
        {
            if (TextLayerOutcome is { } outcome)
            {
                return Task.FromResult(outcome(pageId));
            }

            if (TextLayerResult is { } parse)
            {
                return Task.FromResult(Result<PdfTextLayerResult>.Success(parse(pageId)));
            }

            return Task.FromResult(Result<PdfTextLayerResult>.Failure(
                AppErrorCodes.UnsupportedOperation, "No text-layer provider is pinned."));
        }

        public async ValueTask DisposeAsync()
        {
            Disposed++;
            if (DisposeGate is { } gate)
            {
                await gate.Task;
            }
        }
    }

    private sealed class FakeEngineRuntime(IOcrEngineLease? lease) : IOcrEngineRuntime
    {
        public int Acquired { get; private set; }

        public Func<OcrEngineLeaseRequest, CancellationToken, Task<Result<IOcrEngineLease>>>?
            AcquireBody { get; init; }

        public IReadOnlyList<OcrEngineDefinition> ListDefinitions()
        {
            return [];
        }

        public Task<Result<OcrEngineDefinition>> GetDefinitionAsync(string engineId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<OcrEngineDefinition>.Failure(AppErrorCodes.NotFound,
                "No test engine definitions."));
        }

        public Task<Result<IOcrEngineLease>> AcquireAsync(OcrEngineLeaseRequest request,
            CancellationToken cancellationToken = default)
        {
            Acquired++;
            if (AcquireBody is { } body)
            {
                return body(request, cancellationToken);
            }

            return Task.FromResult(lease is null
                ? Result<IOcrEngineLease>.Failure(AppErrorCodes.InvalidState,
                    "The test engine generation is unavailable.")
                : Result<IOcrEngineLease>.Success(lease));
        }
    }

    private sealed class RecordingExecutor(OcrEngineLeaseRegistry registry) : IOcrQueueTaskExecutor
    {
        public List<IOcrEngineLease?> Leases { get; } = [];

        public Task<OcrQueueExecutionResult> ExecuteAsync(OcrQueueTask task,
            OcrRunExecutionContext? execution, CancellationToken cancellationToken,
            IProgress<OcrTaskProgressReport>? progress = null)
        {
            Leases.Add(registry.Get(task.TaskId));
            return Task.FromResult(new OcrQueueExecutionResult(true, false));
        }

        public Task<OcrQueueExecutionResult> ExecuteAsync(OcrQueueTask task,
            CancellationToken cancellationToken, IProgress<OcrTaskProgressReport>? progress = null)
        {
            Leases.Add(registry.Get(task.TaskId));
            return Task.FromResult(new OcrQueueExecutionResult(true, false));
        }
    }

    private sealed class StubEngine : IOcrEngine
    {
        public string EngineId => "stub-engine";

        public Task<OcrEnginePageResult> RunPageAsync(Page page, OcrPresetVersion presetVersion,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubPageRender(string imagePath) : IPageRenderService
    {
        public int RenderCalls { get; private set; }

        public Task<Result<OcrInputDescriptor>> BuildOcrInputFromRenderedPageAsync(
            DocumentInstanceId documentInstanceId, PageId pageId, int dpi = 200,
            CancellationToken cancellationToken = default)
        {
            RenderCalls++;
            return Task.FromResult(Result<OcrInputDescriptor>.Success(new OcrInputDescriptor(
                pageId, documentInstanceId, OcrInputKinds.PdfPage, imagePath, null, null,
                "available", null)));
        }

        public Task<Result<string>> RenderRegionPngAsync(DocumentInstanceId documentInstanceId,
            PageId pageId, NormalizedBBox region, int dpi = 200,
            CancellationToken cancellationToken = default)
        {
            RenderCalls++;
            return Task.FromResult(Result<string>.Success(imagePath));
        }

        public Task<Result<PageRenderResult>> RenderPageAsync(PageRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<string?>> GetCachedRenderPathAsync(PageRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<string?>.Success((string?)null));
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
        private readonly string _imagePath;

        private Context(TemporarySqliteDatabase database, string imagePath,
            DocumentInstance document, IReadOnlyList<Page> pages, OcrRunEngine engine,
            IOcrPresetService presets)
        {
            _database = database;
            _imagePath = imagePath;
            Document = document;
            Pages = pages;
            Engine = engine;
            Presets = presets;
        }

        public DocumentInstance Document { get; }
        public IReadOnlyList<Page> Pages { get; }
        public OcrRunEngine Engine { get; }
        public IOcrPresetService Presets { get; }

        public async Task AssertCompletedAsync(OcrRun run)
        {
            Result<IReadOnlyList<OcrPageResult>> pages = await Engine.ListPageResultsAsync(run.OcrRunId);
            pages.IsSuccess.Should().BeTrue(pages.ErrorMessage);
            run.State.Should().Be(OcrRunState.Completed,
                string.Join("; ", pages.Value.Select(page => page.ErrorCode + ": " + page.ErrorMessage)));
            pages.Value.Should().OnlyContain(page => page.State == OcrPageResultState.Succeeded);
        }

        public async Task<OcrPresetVersion> CreatePresetVersionAsync(string engineId)
        {
            OcrPreset preset = (await Presets.CreatePresetAsync("Extension preset", null, engineId,
                "test-model", null, "{}", true)).Value;
            return (await Presets.GetCurrentVersionAsync(preset.PresetId)).Value;
        }

        public async Task<int> CountAsync(string sql)
        {
            return await ScalarAsync<int>(sql);
        }

        public async Task<List<T>> QueryAsync<T>(string sql)
        {
            await using Microsoft.Data.Sqlite.SqliteConnection connection =
                _database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            return (await connection.QueryAsync<T>(sql)).ToList();
        }

        public async Task<T?> ScalarAsync<T>(string sql)
        {
            await using Microsoft.Data.Sqlite.SqliteConnection connection =
                _database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<T>(sql);
        }

        public static async Task<Context> CreateAsync(int pageCount = 2)
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory)
                .RunAsync();
            LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
            await libraries.CreateLibraryAsync("OCR extension runtime");
            ItemMetadata item = (await new ItemService(database.ConnectionFactory, libraries, clock)
                .CreateItemAsync("document", "OCR extension runtime")).Value;
            DocumentInstance document = (await new DocumentInstanceService(
                        database.ConnectionFactory, clock)
                    .AttachDocumentInstanceAsync(item.ItemId, null, DocumentInstanceType.PrimaryScan))
                .Value;
            Infrastructure.Layout.PageService pages = new(database.ConnectionFactory, clock);
            Page[] created = new Page[pageCount];
            for (int index = 0; index < created.Length; index++)
            {
                created[index] = (await pages.CreatePageAsync(document.DocumentInstanceId, index,
                    (index + 1).ToString(), null, null, 0, CoordinateBasis.NormalizedPage, null,
                    null, "test", null)).Value;
            }

            string imagePath = Path.Combine(Path.GetTempPath(),
                $"ocr-ext-runtime-{Guid.NewGuid():N}.png");
            await File.WriteAllBytesAsync(imagePath, [137, 80, 78, 71]);
            IDocumentTreeService trees =
                BoxTreeTestData.CreateService(database.ConnectionFactory, clock);
            OcrRunEngine engine = new(database.ConnectionFactory, clock, new StubEngine(),
                treeImporter: new OcrDocumentTreeImporter(trees),
                pageRenderService: new StubPageRender(imagePath));
            return new Context(database, imagePath, document, created, engine,
                new OcrPresetService(database.ConnectionFactory, libraries, clock));
        }

        public ValueTask DisposeAsync()
        {
            if (File.Exists(_imagePath))
            {
                File.Delete(_imagePath);
            }

            return _database.DisposeAsync();
        }
    }
}
