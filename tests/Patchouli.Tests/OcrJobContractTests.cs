using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Extensions;
using Patchouli.Ocr;

namespace Patchouli.Tests;

/// <summary>
/// Regression coverage for the task-level <see cref="IOcrJob"/> semantics through
/// <see cref="ExtensionDomainRun.RunOcrAsync"/>: issued identity, input-level outcomes,
/// exactly-once settlement, bounded lazy inputs, deterministic release, lease draining and the
/// background invocation boundary. No database or compiled package is involved; a
/// delegate-backed fake lease stands in for the extension.
/// </summary>
public sealed class OcrJobContractTests
{
    private static readonly DocumentInstanceId DocumentId = DocumentInstanceId.New();

    private static readonly ExtensionConfigSnapshot Config = new(new Dictionary<string, string>
    {
        ["engine"] = "fake"
    }, 3);

    [Fact]
    public async Task Job_pulls_and_settles_every_page_in_request_order()
    {
        OcrJobPage[] pages = [Page(1), Page(2), Page(3)];
        List<ExtensionPageIdentity> pulled = [];

        OcrDomainRunResult result = await RunAsync(pages, async (job, _, token) =>
        {
            while (await job.NextPageAsync(token) is { } page)
            {
                pulled.Add(page.Page);
                await job.SubmitCandidatesAsync(
                    new OcrExtensionCandidate(page.Page, [Box(page)], Settled(page)), token);
            }
        });

        pulled.Should().Equal(pages.Select(page => page.Page), "pages are issued in request order");
        result.AcceptedCandidates.Should().HaveCount(3);
        result.Settlements.Should().OnlyContain(settlement =>
            settlement.State == OcrPageSettlementState.Accepted && settlement.AcceptedBoxCount == 1);
        result.Settlements.Select(settlement => settlement.Page).Should()
            .Equal(pages.Select(page => page.Page));
    }

    [Fact]
    public async Task Duplicate_or_unsettled_pages_have_explicit_terminal_outcomes()
    {
        OcrJobPage[] pages = [Page(1), Page(2), Page(3)];
        List<OcrSubmissionReceipt> receipts = [];

        OcrDomainRunResult result = await RunAsync(pages, async (job, _, token) =>
        {
            OcrJobPage first = (await job.NextPageAsync(token))!;
            receipts.Add(await job.SubmitCandidatesAsync(
                new OcrExtensionCandidate(first.Page, [Box(first)], Settled(first)), token));
            // A second submission of a settled page is rejected, never merged.
            receipts.Add(await job.SubmitCandidatesAsync(
                new OcrExtensionCandidate(first.Page, [Box(first)], Settled(first)), token));
            // Page two is taken but deliberately left unsettled; page three is never taken.
            (await job.NextPageAsync(token)).Should().NotBeNull();
        });

        receipts[0].State.Should().Be(OcrSubmissionState.Accepted);
        receipts[1].State.Should().Be(OcrSubmissionState.Rejected);
        result.Settlements.Select(settlement => settlement.State).Should().Equal(
            OcrPageSettlementState.Accepted,
            OcrPageSettlementState.Failed,
            OcrPageSettlementState.Cancelled);
    }

    [Fact]
    public async Task Submissions_for_foreign_pages_or_revisions_are_rejected()
    {
        OcrJobPage[] pages = [Page(1)];
        List<OcrSubmissionReceipt> receipts = [];

        await RunAsync(pages, async (job, _, token) =>
        {
            OcrJobPage page = (await job.NextPageAsync(token))!;
            receipts.Add(await job.SubmitCandidatesAsync(
                new OcrExtensionCandidate(page.Page with { RevisionId = DocumentTreeRevisionId.New() },
                    [Box(page)], Settled(page)), token));
            receipts.Add(await job.SubmitCandidatesAsync(
                new OcrExtensionCandidate(
                    new ExtensionPageIdentity(DocumentId, PageId.New(), DocumentTreeRevisionId.New(), 9),
                    [Box(page)], Settled(page)), token));
            // The page is still unsettled, so a corrected submission still succeeds.
            receipts.Add(await job.SubmitCandidatesAsync(
                new OcrExtensionCandidate(page.Page, [Box(page)], Settled(page)), token));
        });

        receipts.Select(receipt => receipt.State).Should().Equal(
            OcrSubmissionState.Rejected, OcrSubmissionState.Rejected, OcrSubmissionState.Accepted);
    }

    [Fact]
    public async Task A_partially_failed_page_reports_its_failed_inputs()
    {
        OcrJobPage[] pages = [Page(1, 2)];
        List<OcrSubmissionReceipt> receipts = [];

        OcrDomainRunResult result = await RunAsync(pages, async (job, _, token) =>
        {
            OcrJobPage page = (await job.NextPageAsync(token))!;
            OcrInputSettlement[] inputs =
            [
                new(page.Inputs[0].Id, OcrInputSettlementState.Recognized),
                new(page.Inputs[1].Id, OcrInputSettlementState.Failed,
                    "ocr_input_timeout", "TimeoutException")
            ];
            receipts.Add(await job.SubmitCandidatesAsync(
                new OcrExtensionCandidate(page.Page, [Box(page)], inputs), token));
        });

        receipts[0].State.Should().Be(OcrSubmissionState.Accepted);
        receipts[0].FailedInputCount.Should().Be(1);
        result.Settlements.Should().ContainSingle(settlement =>
            settlement.State == OcrPageSettlementState.Accepted &&
            settlement.AcceptedBoxCount == 1 && settlement.FailedInputCount == 1);
        result.AcceptedCandidates[0].Inputs.Should().Contain(input =>
            input.State == OcrInputSettlementState.Failed &&
            input.ErrorCode == "ocr_input_timeout");
    }

    [Fact]
    public async Task Submissions_that_leave_inputs_unaccounted_are_rejected()
    {
        OcrJobPage[] pages = [Page(1, 2)];
        List<OcrSubmissionReceipt> receipts = [];

        await RunAsync(pages, async (job, _, token) =>
        {
            OcrJobPage page = (await job.NextPageAsync(token))!;
            // Only one of the two issued inputs is accounted for.
            receipts.Add(await job.SubmitCandidatesAsync(
                new OcrExtensionCandidate(page.Page, [Box(page)],
                    [new OcrInputSettlement(page.Inputs[0].Id, OcrInputSettlementState.Recognized)]),
                token));
            // A foreign outcome id is not issuable either.
            receipts.Add(await job.SubmitCandidatesAsync(
                new OcrExtensionCandidate(page.Page, [Box(page)],
                [
                    new OcrInputSettlement(page.Inputs[0].Id, OcrInputSettlementState.Recognized),
                    new OcrInputSettlement(new OcrInputId("foreign"), OcrInputSettlementState.Recognized)
                ]), token));
            // Corrected: both issued inputs accounted for — still accepted because rejection
            // never settled the page.
            receipts.Add(await job.SubmitCandidatesAsync(
                new OcrExtensionCandidate(page.Page, [Box(page)], Settled(page)), token));
        });

        receipts.Select(receipt => receipt.State).Should().Equal(
            OcrSubmissionState.Rejected, OcrSubmissionState.Rejected, OcrSubmissionState.Accepted);
    }

    [Fact]
    public async Task Input_images_are_materialized_lazily_and_foreign_reads_are_rejected()
    {
        OcrJobPage[] pages = [Page(1, 2)];
        int reads = 0;

        await RunAsync(pages, async (job, _, token) =>
        {
            reads.Should().Be(0, "no image is read before the extension asks");
            OcrJobPage page = (await job.NextPageAsync(token))!;
            using OcrInputImage first = await job.ReadInputImageAsync(page.Inputs[0], token);
            reads.Should().Be(1);
            using OcrInputImage second = await job.ReadInputImageAsync(page.Inputs[1], token);
            reads.Should().Be(2);
            await Assert.ThrowsAsync<ArgumentException>(() =>
                job.ReadInputImageAsync(new OcrJobInput(new OcrInputId("foreign"),
                    new NormalizedBBox(0, 0, 1, 1), "image/png"), token).AsTask());
            // An equal-valued but different descriptor instance is still foreign.
            await Assert.ThrowsAsync<ArgumentException>(() =>
                job.ReadInputImageAsync(new OcrJobInput(page.Inputs[0].Id, page.Inputs[0].Region,
                    page.Inputs[0].ContentType), token).AsTask());
            await job.SubmitCandidatesAsync(new OcrExtensionCandidate(page.Page, [], Settled(page)),
                token);
        }, (input, _) =>
        {
            reads++;
            return ValueTask.FromResult(new OcrInputImage(input.Id, input.ContentType, new byte[] { 1, 2 }));
        });

        reads.Should().Be(2);
    }

    [Fact]
    public async Task A_settled_pages_inputs_can_no_longer_be_read()
    {
        OcrJobPage[] pages = [Page(1, 1)];

        await RunAsync(pages, async (job, _, token) =>
        {
            OcrJobPage page = (await job.NextPageAsync(token))!;
            await job.SubmitCandidatesAsync(new OcrExtensionCandidate(page.Page, [], Settled(page)),
                token);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                job.ReadInputImageAsync(page.Inputs[0], token).AsTask());
        });
    }

    [Fact]
    public async Task Disposed_input_images_drop_their_buffer_and_reject_reads()
    {
        OcrJobPage[] pages = [Page(1, 1)];

        await RunAsync(pages, async (job, unusedResources, token) =>
        {
            OcrJobPage page = (await job.NextPageAsync(token))!;
            OcrInputImage image = await job.ReadInputImageAsync(page.Inputs[0], token);
            image.IsDisposed.Should().BeFalse();
            image.Data.Length.Should().BeGreaterThan(0);
            image.Dispose();
            image.IsDisposed.Should().BeTrue();
            Assert.Throws<ObjectDisposedException>(() => _ = image.Data);
            await job.SubmitCandidatesAsync(new OcrExtensionCandidate(page.Page, [], Settled(page)),
                token);
        });
    }

    [Fact]
    public async Task Concurrent_image_reads_are_bounded_and_resume_when_a_lease_is_released()
    {
        OcrJobPage[] pages = [Page(1, 2)];

        await RunAsync(pages, async (job, _, token) =>
        {
            OcrJobPage page = (await job.NextPageAsync(token))!;
            OcrInputImage first = await job.ReadInputImageAsync(page.Inputs[0], token);
            Task<OcrInputImage> second = job.ReadInputImageAsync(page.Inputs[1], token).AsTask();
            second.IsCompleted.Should().BeFalse(
                "with a one-image budget the second read waits for the first lease");
            first.Dispose();
            using OcrInputImage image = await second;
            await job.SubmitCandidatesAsync(new OcrExtensionCandidate(page.Page, [], Settled(page)),
                token);
        }, maxInFlightImages: 1);
    }

    [Fact]
    public async Task Image_leases_the_extension_leaves_open_are_released_when_the_job_ends()
    {
        OcrJobPage[] pages = [Page(1, 1)];
        int released = 0;

        await RunAsync(pages, async (job, unusedResources, token) =>
        {
            OcrJobPage page = (await job.NextPageAsync(token))!;
            // Deliberately undisposed: the host must still release the lease deterministically.
            _ = await job.ReadInputImageAsync(page.Inputs[0], token);
            await job.SubmitCandidatesAsync(new OcrExtensionCandidate(page.Page, [], Settled(page)),
                token);
        }, (input, _) => ValueTask.FromResult(new OcrInputImage(input.Id, input.ContentType,
            new byte[] { 1 }, () => Interlocked.Increment(ref released))));

        released.Should().Be(1);
    }

    [Fact]
    public async Task Completion_and_disposal_wait_for_a_read_that_ignores_cancellation()
    {
        OcrJobPage[] pages = [Page(1, 1)];
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource produce = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource closing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int released = 0;
        ValueTask<OcrInputImage>? pending = null;
        FakeLease lease = new(new FakeOcrExtension(async (job, _, token) =>
        {
            OcrJobPage page = (await job.NextPageAsync(token))!;
            // Started but deliberately never awaited: the read stays in flight as the
            // extension invocation returns without settling the page.
            pending = job.ReadInputImageAsync(page.Inputs[0], token);
            await entered.Task;
        }), Config);
        using TestExtensionResources resources = TestExtensionResources.Unavailable();
        ExtensionDomainRun run = new(lease, resources.ForRun(Config), Config);

        Task<Core.Results.Result<OcrDomainRunResult>> runTask = run.RunOcrAsync(
            new OcrDomainJobRequest("job-drain-read", DocumentId, pages, async (input, token) =>
            {
                using CancellationTokenRegistration registration = token.Register(() => closing.TrySetResult());
                entered.TrySetResult();
                // This loader ignores the job's read-lifetime cancellation on purpose.
                await produce.Task;
                return new OcrInputImage(input.Id, input.ContentType, new byte[] { 1 },
                    () => Interlocked.Increment(ref released));
            }, new OcrJobOptions()));
        await entered.Task;
        await closing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        runTask.IsCompleted.Should().BeFalse(
            "completion drains a read that is still in flight instead of releasing around it");
        ValueTask disposing = run.DisposeAsync();
        disposing.IsCompleted.Should().BeFalse(
            "the generation lease stays pinned while the read drains");
        produce.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending!.Value.AsTask());
        released.Should().Be(1, "the post-completion read disposes the image it just loaded");
        Core.Results.Result<OcrDomainRunResult> result = await runTask;
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.Settlements.Should().ContainSingle(settlement =>
            settlement.State == OcrPageSettlementState.Failed && settlement.FailedInputCount == 1);
        await disposing;
        lease.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task Request_collections_are_frozen_when_the_job_starts()
    {
        List<OcrJobInput> firstInputs =
        [
            new(new OcrInputId("input-1"), new NormalizedBBox(0, 0, 1, 1), "image/png")
        ];
        List<OcrJobPage> requestPages =
        [
            new(new ExtensionPageIdentity(DocumentId, PageId.New(),
                DocumentTreeRevisionId.New(), 1), firstInputs),
            new(new ExtensionPageIdentity(DocumentId, PageId.New(),
                    DocumentTreeRevisionId.New(), 2),
                [
                    new OcrJobInput(new OcrInputId("input-2"), new NormalizedBBox(0, 0, 1, 1),
                        "image/png")
                ])
        ];
        int pulled = 0;

        OcrDomainRunResult result = await RunAsync(requestPages, async (job, _, token) =>
        {
            while (await job.NextPageAsync(token) is { } page)
            {
                pulled++;
                if (pulled == 1)
                {
                    // Mutating caller-owned containers mid-run must not change the running job.
                    requestPages.Clear();
                    firstInputs.Clear();
                }

                await job.SubmitCandidatesAsync(
                    new OcrExtensionCandidate(page.Page, [], Settled(page)), token);
            }
        });

        pulled.Should().Be(2, "the job iterates its frozen copy, not the caller's list");
        result.Settlements.Should().HaveCount(2);
        result.Settlements.Should().OnlyContain(settlement =>
            settlement.State == OcrPageSettlementState.Accepted);
    }

    [Fact]
    public async Task Issued_page_inputs_cannot_be_mutated_through_collection_casts()
    {
        OcrJobPage[] pages = [Page(1, 2)];

        await RunAsync(pages, async (job, _, token) =>
        {
            OcrJobPage page = (await job.NextPageAsync(token))!;
            OcrJobInput original = page.Inputs[0];
            OcrJobInput forged = new(new OcrInputId("forged"), new NormalizedBBox(0, 0, 1, 1),
                "image/png");
            page.Inputs.Should().NotBeOfType<OcrJobInput[]>(
                "an array-backed Inputs list could be cast back and rewritten");
            IList<OcrJobInput> writable = (IList<OcrJobInput>)page.Inputs;
            Assert.Throws<NotSupportedException>(() => writable[0] = forged);
            Assert.Throws<NotSupportedException>(() => writable.Add(forged));
            page.Inputs[0].Should().BeSameAs(original);
            using OcrInputImage image = await job.ReadInputImageAsync(original, token);
            await job.SubmitCandidatesAsync(new OcrExtensionCandidate(page.Page, [], Settled(page)),
                token);
        });
    }

    [Fact]
    public async Task Cross_page_continuations_follow_settlement_order()
    {
        OcrJobPage[] pages = [Page(1), Page(2)];
        DocumentBoxId head = DocumentBoxId.New();
        List<OcrSubmissionReceipt> receipts = [];

        await RunAsync(pages, async (job, _, token) =>
        {
            OcrJobPage first = (await job.NextPageAsync(token))!;
            OcrJobPage second = (await job.NextPageAsync(token))!;

            // The second page settles first and references a box the first page will declare —
            // a forward reference is rejected, never silently resolved.
            OcrExtensionBoxCandidate tail = new(second.Inputs[0].Id,
                new DocumentBoxSeed(null, null, 0, "text", null,
                    new NormalizedBBox(0.1, 0.4, 0.2, 0.2), new TextBoxPayload("tail"),
                    ContinuesFromBoxId: head));
            receipts.Add(await job.SubmitCandidatesAsync(
                new OcrExtensionCandidate(second.Page, [tail], Settled(second)), token));

            OcrExtensionBoxCandidate headBox = new(first.Inputs[0].Id,
                new DocumentBoxSeed(head, null, 0, "text", null,
                    new NormalizedBBox(0.1, 0.1, 0.2, 0.2), new TextBoxPayload("head")));
            receipts.Add(await job.SubmitCandidatesAsync(
                new OcrExtensionCandidate(first.Page, [headBox], Settled(first)), token));

            // Once the head page is accepted, the same continuation resolves.
            receipts.Add(await job.SubmitCandidatesAsync(
                new OcrExtensionCandidate(second.Page, [tail], Settled(second)), token));
        });

        receipts.Select(receipt => receipt.State).Should().Equal(
            OcrSubmissionState.Rejected, OcrSubmissionState.Accepted, OcrSubmissionState.Accepted);
    }

    [Fact]
    public async Task FailPage_records_an_explicit_failure_and_still_settles_once()
    {
        OcrJobPage[] pages = [Page(1, 2)];
        List<OcrSubmissionReceipt> receipts = [];

        OcrDomainRunResult result = await RunAsync(pages, async (job, _, token) =>
        {
            OcrJobPage page = (await job.NextPageAsync(token))!;
            receipts.Add(await job.FailPageAsync(page.Page, "engine_crashed", "native session died", token));
            receipts.Add(await job.SubmitCandidatesAsync(
                new OcrExtensionCandidate(page.Page, [Box(page)], Settled(page)), token));
        });

        receipts[0].State.Should().Be(OcrSubmissionState.Failed);
        receipts[1].State.Should().Be(OcrSubmissionState.Rejected);
        result.AcceptedCandidates.Should().BeEmpty();
        result.Settlements.Should().ContainSingle(settlement =>
            settlement.State == OcrPageSettlementState.Failed &&
            settlement.ErrorCode == "engine_crashed" && settlement.FailedInputCount == 2);
    }

    [Fact]
    public async Task Cancellation_settles_unfinished_pages_as_cancelled()
    {
        OcrJobPage[] pages = [Page(1), Page(2)];
        using CancellationTokenSource source = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using TestExtensionResources resources = TestExtensionResources.Unavailable();
        await using ExtensionDomainRun run = NewRun(resources, async (job, unusedResources, token) =>
        {
            _ = await job.NextPageAsync(token);
            entered.TrySetResult();
            await resume.Task;
            try
            {
                await job.SubmitCandidatesAsync(
                    new OcrExtensionCandidate(pages[0].Page, [], Settled(pages[0])), token);
            }
            catch (OperationCanceledException)
            {
                // The extension may swallow cancellation and still return; the host then settles
                // the unfinished page as cancelled.
            }
        });
        OcrDomainJobRequest request = new("job-cancel", DocumentId, pages, AlwaysImage,
            new OcrJobOptions());
        Task<Core.Results.Result<OcrDomainRunResult>> running = run.RunOcrAsync(request, source.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        source.Cancel();
        resume.TrySetResult();
        Core.Results.Result<OcrDomainRunResult> result = await running;

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.Settlements.Select(settlement => settlement.State).Should().Equal(
            OcrPageSettlementState.Cancelled, OcrPageSettlementState.Cancelled);
    }

    [Fact]
    public async Task DisposeAsync_waits_for_the_active_invocation_before_releasing_the_lease()
    {
        OcrJobPage[] pages = [Page(1)];
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeLease lease = new(new FakeOcrExtension(async (job, _, token) =>
        {
            OcrJobPage page = (await job.NextPageAsync(token))!;
            entered.TrySetResult();
            await release.Task;
            await job.SubmitCandidatesAsync(new OcrExtensionCandidate(page.Page, [], Settled(page)),
                token);
        }), Config);
        using TestExtensionResources resources = TestExtensionResources.Unavailable();
        ExtensionDomainRun run = new(lease, resources.ForRun(Config), Config);

        Task<Core.Results.Result<OcrDomainRunResult>> runTask = run.RunOcrAsync(
            new OcrDomainJobRequest("job-drain", DocumentId, pages, AlwaysImage, new OcrJobOptions()));
        await entered.Task;
        ValueTask disposing = run.DisposeAsync();
        disposing.IsCompleted.Should().BeFalse("the lease must outlive the in-flight invocation");
        release.TrySetResult();
        (await runTask).IsSuccess.Should().BeTrue();
        await disposing;
        lease.DisposeCount.Should().Be(1);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            run.RunOcrAsync(new OcrDomainJobRequest("job-late", DocumentId, pages, AlwaysImage,
                new OcrJobOptions())));
    }

    [Fact]
    public async Task Concurrent_disposers_share_one_lease_disposal()
    {
        OcrJobPage[] pages = [Page(1)];
        TaskCompletionSource leaseEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource leaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeLease lease = new(new FakeOcrExtension(async (job, _, token) =>
        {
            OcrJobPage page = (await job.NextPageAsync(token))!;
            await job.SubmitCandidatesAsync(new OcrExtensionCandidate(page.Page, [], Settled(page)),
                token);
        }), Config)
        {
            DisposeBody = async () =>
            {
                leaseEntered.TrySetResult();
                await leaseGate.Task;
            }
        };
        using TestExtensionResources resources = TestExtensionResources.Unavailable();
        ExtensionDomainRun run = new(lease, resources.ForRun(Config), Config);
        Core.Results.Result<OcrDomainRunResult> ran = await run.RunOcrAsync(
            new OcrDomainJobRequest("job-share", DocumentId, pages, AlwaysImage, new OcrJobOptions()));
        ran.IsSuccess.Should().BeTrue(ran.ErrorMessage);

        ValueTask first = run.DisposeAsync();
        await leaseEntered.Task;
        ValueTask second = run.DisposeAsync();
        first.IsCompleted.Should().BeFalse("the shared lease disposal is still blocked");
        second.IsCompleted.Should().BeFalse(
            "a second disposer awaits the same disposal instead of returning early");
        leaseGate.TrySetResult();
        await first;
        await second;
        lease.DisposeCount.Should().Be(1, "both disposers awaited one lease disposal operation");
    }

    [Fact]
    public async Task The_extension_invocation_runs_off_the_callers_synchronization_context()
    {
        OcrJobPage[] pages = [Page(1)];
        SynchronizationContext? observed = null;
        SynchronizationContext? previous = SynchronizationContext.Current;
        Task<OcrDomainRunResult> invocation;
        SynchronizationContext.SetSynchronizationContext(new MarkedSynchronizationContext());
        try
        {
            invocation = RunAsync(pages, async (job, _, token) =>
            {
                observed = SynchronizationContext.Current;
                OcrJobPage page = (await job.NextPageAsync(token))!;
                await job.SubmitCandidatesAsync(
                    new OcrExtensionCandidate(page.Page, [], Settled(page)), token);
            });
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await invocation;
        observed.Should().BeNull(
            "the extension call is background-bound even when everything before it completes synchronously");
    }

    [Fact]
    public async Task Job_info_carries_the_pinned_document_and_run_configuration()
    {
        OcrJobPage[] pages = [Page(1)];
        OcrJobInfo? observed = null;

        await RunAsync(pages, async (job, _, token) =>
        {
            observed = job.Info;
            OcrJobPage page = (await job.NextPageAsync(token))!;
            await job.SubmitCandidatesAsync(new OcrExtensionCandidate(page.Page, [], Settled(page)),
                token);
        });

        observed.Should().NotBeNull();
        observed!.JobId.Should().Be("job-test");
        observed.DocumentId.Should().Be(DocumentId);
        observed.Config.Should().BeSameAs(Config, "the run pins the resolved configuration snapshot");
    }

    [Fact]
    public async Task A_stashed_job_cannot_be_used_after_the_invocation_ends()
    {
        OcrJobPage[] pages = [Page(1)];
        IOcrJob? stashed = null;

        await RunAsync(pages, async (job, _, token) =>
        {
            stashed = job;
            OcrJobPage page = (await job.NextPageAsync(token))!;
            await job.SubmitCandidatesAsync(new OcrExtensionCandidate(page.Page, [], Settled(page)),
                token);
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => stashed!.NextPageAsync().AsTask());
    }

    [Fact]
    public async Task Requests_with_duplicate_or_empty_pages_are_rejected_before_invocation()
    {
        OcrJobPage page = Page(1);
        using TestExtensionResources resources = TestExtensionResources.Unavailable();
        await using ExtensionDomainRun run = NewRun(resources, (_, _, _) => Task.CompletedTask);

        Core.Results.Result<OcrDomainRunResult> duplicates = await run.RunOcrAsync(
            new OcrDomainJobRequest("job-dup", DocumentId, [page, page], AlwaysImage,
                new OcrJobOptions()));
        duplicates.IsFailure.Should().BeTrue();

        OcrJobPage empty = new(page.Page, []);
        Core.Results.Result<OcrDomainRunResult> noInputs = await run.RunOcrAsync(
            new OcrDomainJobRequest("job-empty", DocumentId, [empty], AlwaysImage,
                new OcrJobOptions()));
        noInputs.IsFailure.Should().BeTrue();

        OcrJobPage badInputId = new(page.Page,
        [
            new OcrJobInput(new OcrInputId("ok"), new NormalizedBBox(0, 0, 1, 1), "image/png"),
            new OcrJobInput(default, new NormalizedBBox(0, 0, 1, 1), "image/png")
        ]);
        Core.Results.Result<OcrDomainRunResult> defaultId = await run.RunOcrAsync(
            new OcrDomainJobRequest("job-id", DocumentId, [badInputId], AlwaysImage,
                new OcrJobOptions()));
        defaultId.IsFailure.Should().BeTrue("a default OcrInputId has no identity");

        OcrJobPage foreign = new(page.Page with { DocumentId = DocumentInstanceId.New() },
            page.Inputs);
        Core.Results.Result<OcrDomainRunResult> otherDocument = await run.RunOcrAsync(
            new OcrDomainJobRequest("job-doc", DocumentId, [foreign], AlwaysImage,
                new OcrJobOptions()));
        otherDocument.IsFailure.Should().BeTrue("pages must belong to the job's document");

        Core.Results.Result<OcrDomainRunResult> noBudget = await run.RunOcrAsync(
            new OcrDomainJobRequest("job-budget", DocumentId, [page], AlwaysImage,
                new OcrJobOptions(), MaxInFlightImages: 0));
        noBudget.IsFailure.Should().BeTrue("the image budget must be at least one");
    }

    private static OcrJobPage Page(int index, int inputCount = 1)
    {
        ExtensionPageIdentity identity = new(DocumentId, PageId.New(), DocumentTreeRevisionId.New(),
            index);
        OcrJobInput[] inputs = Enumerable.Range(0, inputCount)
            .Select(item => new OcrJobInput(new OcrInputId($"input-{index}-{item}"),
                new NormalizedBBox(0, 0, 1, 1), "image/png"))
            .ToArray();
        return new OcrJobPage(identity, inputs);
    }

    private static OcrInputSettlement[] Settled(OcrJobPage page)
    {
        return page.Inputs
            .Select(input => new OcrInputSettlement(input.Id, OcrInputSettlementState.Recognized))
            .ToArray();
    }

    private static OcrExtensionBoxCandidate Box(OcrJobPage page)
    {
        OcrJobInput input = page.Inputs[0];
        return new OcrExtensionBoxCandidate(input.Id, new DocumentBoxSeed(null, null, 0, "text",
            null, new NormalizedBBox(0.1, 0.1, 0.5, 0.5), new TextBoxPayload("recognized")));
    }

    private static ValueTask<OcrInputImage> AlwaysImage(OcrJobInput input,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new OcrInputImage(input.Id, input.ContentType, new byte[] { 1 }));
    }

    private static ExtensionDomainRun NewRun(IExtensionResources resources,
        Func<IOcrJob, IExtensionResources, CancellationToken, Task> body)
    {
        FakeLease lease = new(new FakeOcrExtension(body), Config);
        return new ExtensionDomainRun(lease, resources.ForRun(Config), Config);
    }

    private static async Task<OcrDomainRunResult> RunAsync(IReadOnlyList<OcrJobPage> pages,
        Func<IOcrJob, IExtensionResources, CancellationToken, Task> body,
        Func<OcrJobInput, CancellationToken, ValueTask<OcrInputImage>>? readImage = null,
        int maxInFlightImages = 4)
    {
        using TestExtensionResources resources = TestExtensionResources.Unavailable();
        await using ExtensionDomainRun run = NewRun(resources, body);
        OcrDomainJobRequest request = new("job-test", DocumentId, pages,
            readImage ?? AlwaysImage, new OcrJobOptions(200),
            MaxInFlightImages: maxInFlightImages);
        Core.Results.Result<OcrDomainRunResult> result = await run.RunOcrAsync(request);
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        return result.Value;
    }

    private sealed class MarkedSynchronizationContext : SynchronizationContext
    {
    }

    private sealed class FakeOcrExtension(
        Func<IOcrJob, IExtensionResources, CancellationToken, Task> run) : IOcrExtension
    {
        public Task RunAsync(IOcrJob job, IExtensionResources resources,
            CancellationToken cancellationToken = default)
        {
            return run(job, resources, cancellationToken);
        }
    }

    private sealed class FakeLease(IOcrExtension ocr, ExtensionConfigSnapshot config) : IExtensionLease
    {
        public int DisposeCount { get; private set; }

        public ExtensionDescriptor Descriptor => new("test.ocr", "Test OCR", "1.0.0", "",
            ExtensionKind.Ocr);

        public string Fingerprint => "test-fingerprint";
        public ExtensionConfigSnapshot Config { get; } = config;
        public IReadOnlyList<ConfigFieldDescriptor> ConfigFields => [];
        public IExtensionDefinition Definition => ocr;

        public ITranslationExtension Translation => throw new InvalidOperationException(
            "The fake lease is OCR-only.");

        public IMetadataExtension? Metadata => null;
        public IOcrExtension? Ocr => ocr;
        public IRetrievalExtension? Retrieval => null;
        public Func<Task>? DisposeBody { get; init; }

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (DisposeBody is not null)
            {
                await DisposeBody();
            }
        }
    }
}
