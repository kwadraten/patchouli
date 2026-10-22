using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Host.CitationInterop;
using static Patchouli.Tests.CitationInterop.CaywIndependentQueries;

namespace Patchouli.Tests.CitationInterop;

/// <summary>
/// Independent verification of the CAYW request lifecycle (issue #14 §4,
/// checklist items 1/3/4): probe purity, the BBT flag-truthiness matrix for
/// <c>selected</c>, and the concurrency/cancellation races, all orchestrated
/// with TaskCompletionSource barriers instead of sleeps.
/// </summary>
public sealed class CaywIndependentHandlerTests
{
    private static (TaskCompletionSource Entered, TaskCompletionSource<CitationPickBridgeResult> Release)
        NewGate()
    {
        return (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource<CitationPickBridgeResult>(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    [Fact]
    public async Task Probe_returns_ready_text_and_never_touches_the_bridge()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            CountingCitationBridge bridge = new();
            CaywRequestHandler handler = new(context.Interop, bridge);

            CitationHttpResponse response = await handler.HandleAsync(Query(("probe", "1")), CancellationToken.None);

            response.StatusCode.Should().Be(200);
            response.Body.Should().Be("ready");
            response.ContentType.Should().StartWith("text/plain");
            bridge.AssertNoBridgeCalls();
        }
    }

    [Fact]
    public async Task Probe_on_headless_bridge_is_503_and_still_touches_nothing()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            CaywRequestHandler headless = new(context.Interop, UnavailableCitationDesktopBridge.Instance);
            CitationHttpResponse response = await headless.HandleAsync(Query(("probe", "1")), CancellationToken.None);
            response.StatusCode.Should().Be(503);
            response.Body.Should().Contain("not ready");

            CountingCitationBridge unavailable = new() { Available = false };
            CaywRequestHandler handler = new(context.Interop, unavailable);
            CitationHttpResponse second = await handler.HandleAsync(Query(("probe", "true")), CancellationToken.None);
            second.StatusCode.Should().Be(503);
            unavailable.AssertNoBridgeCalls();
        }
    }

    [Fact]
    public async Task Probe_flag_follows_bbt_truthiness_and_empty_probe_is_not_a_probe()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            CountingCitationBridge bridge = new();
            CaywRequestHandler handler = new(context.Interop, bridge);

            // "false" is a non-empty value: the probe flag IS set (BBT rule).
            CitationHttpResponse falseProbe =
                await handler.HandleAsync(Query(("probe", "false")), CancellationToken.None);
            falseProbe.StatusCode.Should().Be(200);
            falseProbe.Body.Should().Be("ready");
            bridge.AssertNoBridgeCalls();

            // An empty value does not set the flag: the request falls through
            // to the interactive path and opens the picker (which cancels).
            CitationHttpResponse emptyProbe = await handler.HandleAsync(Query(("probe", "")), CancellationToken.None);
            emptyProbe.StatusCode.Should().Be(200);
            emptyProbe.Body.Should().Be("");
            bridge.PickCalls.Should().Be(1, "an empty probe value must not be treated as a probe");
        }
    }

    [Fact]
    public async Task Probe_short_circuits_even_with_an_unknown_format()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            CountingCitationBridge bridge = new();
            CaywRequestHandler handler = new(context.Interop, bridge);

            CitationHttpResponse response = await handler.HandleAsync(
                Query(("probe", "1"), ("format", "docx")), CancellationToken.None);

            response.StatusCode.Should().Be(200);
            response.Body.Should().Be("ready");
            bridge.AssertNoBridgeCalls();
        }
    }

    [Theory]
    [InlineData("false", true)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("0", true)]
    [InlineData("", false)]
    public async Task Selected_flag_matrix_follows_bbt_non_empty_rule(string value, bool expectSelected)
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            ItemMetadata book = await context.CreateItemAsync(new CreateItemRequest("book", "Matrix Book"));
            CountingCitationBridge bridge = new()
            {
                Selected = [book.ItemId],
                // Interactive path cancels, so the two paths are distinguishable
                // by output: selected formats the snapshot, interactive is empty.
                OnPick = _ => Task.FromResult(CitationPickBridgeResult.CancelledResult)
            };
            CaywRequestHandler handler = new(context.Interop, bridge);

            CitationHttpResponse response = await handler.HandleAsync(
                Query(("selected", value), ("format", "natbib")), CancellationToken.None);

            response.StatusCode.Should().Be(200);
            if (expectSelected)
            {
                bridge.SnapshotCalls.Should().Be(1);
                bridge.PickCalls.Should().Be(0, "selected mode must never open the picker");
                response.Body.Should().Be($"\\cite{{{book.CitationKey}}}");
            }
            else
            {
                bridge.SnapshotCalls.Should().Be(0);
                bridge.PickCalls.Should().Be(1);
                response.Body.Should().Be("");
            }
        }
    }

    [Fact]
    public async Task Missing_selected_parameter_uses_the_interactive_picker()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            CountingCitationBridge bridge = new()
            {
                OnPick = _ => Task.FromResult(CitationPickBridgeResult.CancelledResult)
            };
            CaywRequestHandler handler = new(context.Interop, bridge);

            CitationHttpResponse response = await handler.HandleAsync(
                Query(("format", "natbib")), CancellationToken.None);

            response.StatusCode.Should().Be(200);
            bridge.PickCalls.Should().Be(1);
            bridge.SnapshotCalls.Should().Be(0);
        }
    }

    [Fact]
    public async Task Selected_on_headless_host_is_ui_unavailable_not_empty_or_hang()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            CountingCitationBridge bridge = new() { Available = false };
            CaywRequestHandler handler = new(context.Interop, bridge);

            CitationHttpResponse response = await handler.HandleAsync(
                Query(("selected", "1")), CancellationToken.None);

            response.StatusCode.Should().Be(500);
            response.Body.Should().Contain("ui_unavailable");
            bridge.SnapshotCalls.Should().Be(0);
            bridge.PickCalls.Should().Be(0);
        }
    }

    [Fact]
    public async Task Selected_snapshot_with_a_trashed_item_fails_instead_of_formatting_it()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            ItemMetadata book = await context.CreateItemAsync(new CreateItemRequest("book", "Doomed Book"));
            await context.Items.DeleteItemAsync(book.ItemId);
            // The desktop snapshot is stale: it still carries the trashed item.
            CountingCitationBridge bridge = new() { Selected = [book.ItemId] };
            CaywRequestHandler handler = new(context.Interop, bridge);

            CitationHttpResponse response = await handler.HandleAsync(
                Query(("selected", "1"), ("format", "natbib")), CancellationToken.None);

            response.StatusCode.Should().Be(500);
            bridge.PickCalls.Should().Be(0);
            bridge.ClipboardCalls.Should().Be(0);
        }
    }

    [Fact]
    public async Task Busy_gate_rejects_second_request_and_recovers_after_first_completes()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            ItemMetadata book = await context.CreateItemAsync(new CreateItemRequest("book", "Busy Book"));
            (TaskCompletionSource entered, TaskCompletionSource<CitationPickBridgeResult> release) = NewGate();
            CountingCitationBridge bridge = new()
            {
                OnPick = _ =>
                {
                    entered.TrySetResult();
                    return release.Task;
                }
            };
            CaywRequestHandler handler = new(context.Interop, bridge);

            Task<CitationHttpResponse> first = handler.HandleAsync(Query(), CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            CitationHttpResponse second = await handler.HandleAsync(Query(), CancellationToken.None);
            second.StatusCode.Should().Be(500);
            second.Body.Should().Contain("busy");
            bridge.PickCalls.Should().Be(1, "the busy request must not reach the picker");

            release.SetResult(new CitationPickBridgeResult(
                CitationPickOutcome.Confirmed, [new CitationBridgePickEntry(book.ItemId)]));
            CitationHttpResponse firstResponse = await first.WaitAsync(TimeSpan.FromSeconds(10));
            firstResponse.StatusCode.Should().Be(200);
            firstResponse.Body.Should().Be($"\\cite{{{book.CitationKey}}}");
            handler.IsPickInProgress.Should().BeFalse();

            // The gate is free again: a third request reaches the picker.
            bridge.OnPick = _ => Task.FromResult(CitationPickBridgeResult.CancelledResult);
            CitationHttpResponse third = await handler.HandleAsync(Query(), CancellationToken.None);
            third.StatusCode.Should().Be(200);
            bridge.PickCalls.Should().Be(2);
        }
    }

    [Fact]
    public async Task Disconnect_then_late_confirmation_has_no_side_effects_and_gate_recovers()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            ItemMetadata book = await context.CreateItemAsync(new CreateItemRequest("book", "Late Book"));
            (TaskCompletionSource entered, TaskCompletionSource<CitationPickBridgeResult> release) = NewGate();
            CountingCitationBridge bridge = new()
            {
                // A bridge that does not observe cancellation on its own: the
                // handler itself must refuse the late confirmation.
                OnPick = _ =>
                {
                    entered.TrySetResult();
                    return release.Task;
                }
            };
            CaywRequestHandler handler = new(context.Interop, bridge);
            using CancellationTokenSource disconnect = new();

            Task<CitationHttpResponse> request = handler.HandleAsync(
                Query(("clipboard", "1"), ("select", "1"), ("minimize", "1")), disconnect.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            disconnect.Cancel();
            release.SetResult(new CitationPickBridgeResult(
                CitationPickOutcome.Confirmed, [new CitationBridgePickEntry(book.ItemId)]));

            CitationHttpResponse response = await request.WaitAsync(TimeSpan.FromSeconds(10));
            response.StatusCode.Should().Be(200);
            response.Body.Should().Be("");
            bridge.ClipboardCalls.Should().Be(0);
            bridge.SelectCalls.Should().Be(0);
            bridge.MinimizeCalls.Should().Be(0);
            handler.IsPickInProgress.Should().BeFalse();

            bridge.OnPick = _ => Task.FromResult(CitationPickBridgeResult.CancelledResult);
            CitationHttpResponse next = await handler.HandleAsync(Query(), CancellationToken.None);
            next.StatusCode.Should().Be(200);
        }
    }

    [Fact]
    public async Task Deadline_bounds_the_pick_even_when_the_bridge_returns_late()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            ItemMetadata book = await context.CreateItemAsync(new CreateItemRequest("book", "Deadline Book"));
            (TaskCompletionSource entered, TaskCompletionSource<CitationPickBridgeResult> release) = NewGate();
            CountingCitationBridge bridge = new()
            {
                // The bridge ignores the cancellation token entirely and only
                // answers when the test releases it — after the deadline.
                OnPick = _ =>
                {
                    entered.TrySetResult();
                    return release.Task;
                }
            };
            CaywRequestHandler handler = new(context.Interop, bridge, TimeSpan.FromMilliseconds(200));

            Task<CitationHttpResponse> request = handler.HandleAsync(
                Query(("clipboard", "1")), CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Wait until the 200 ms interactive deadline has definitely passed
            // (the wait only lets the deadline elapse; event order is gated by
            // the TaskCompletionSources, not by this delay).
            await Task.Delay(600);
            release.SetResult(new CitationPickBridgeResult(
                CitationPickOutcome.Confirmed, [new CitationBridgePickEntry(book.ItemId)]));

            CitationHttpResponse response = await request.WaitAsync(TimeSpan.FromSeconds(10));
            response.StatusCode.Should().Be(200);
            bridge.ClipboardCalls.Should().Be(0,
                $"no side effect may fire after the deadline; the actual response body was '{response.Body}'");
            response.Body.Should().Be("", "a confirmation arriving after the interactive deadline must be discarded");
            handler.IsPickInProgress.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Confirmed_pick_applies_each_side_effect_exactly_once()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            ItemMetadata book = await context.CreateItemAsync(new CreateItemRequest("book", "Once Book"));
            CountingCitationBridge bridge = new()
            {
                OnPick = _ => Task.FromResult(new CitationPickBridgeResult(
                    CitationPickOutcome.Confirmed, [new CitationBridgePickEntry(book.ItemId)]))
            };
            CaywRequestHandler handler = new(context.Interop, bridge);
            using CancellationTokenSource request = new();

            CitationHttpResponse response = await handler.HandleAsync(
                Query(("clipboard", "1"), ("select", "1"), ("minimize", "1")), request.Token);
            // A cancellation racing in after completion must not re-run anything.
            request.Cancel();

            response.StatusCode.Should().Be(200);
            response.Body.Should().Be($"\\cite{{{book.CitationKey}}}");
            bridge.PickCalls.Should().Be(1);
            bridge.ClipboardCalls.Should().Be(1);
            bridge.SelectCalls.Should().Be(1);
            bridge.MinimizeCalls.Should().Be(1);
            bridge.ClipboardTexts.Should().Equal(response.Body);
            bridge.LastSelectedViaBridge.Should().Equal(book.ItemId);
        }
    }

    [Fact]
    public async Task Item_trashed_while_picker_is_open_fails_at_confirm_time()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            ItemMetadata book = await context.CreateItemAsync(new CreateItemRequest("book", "Vanishing Book"));
            (TaskCompletionSource entered, TaskCompletionSource<CitationPickBridgeResult> release) = NewGate();
            CountingCitationBridge bridge = new()
            {
                OnPick = _ =>
                {
                    entered.TrySetResult();
                    return release.Task;
                }
            };
            CaywRequestHandler handler = new(context.Interop, bridge);

            Task<CitationHttpResponse> request = handler.HandleAsync(
                Query(("clipboard", "1")), CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The picked item leaves the citable set while the picker is open.
            await context.Items.DeleteItemAsync(book.ItemId);
            release.SetResult(new CitationPickBridgeResult(
                CitationPickOutcome.Confirmed, [new CitationBridgePickEntry(book.ItemId)]));

            CitationHttpResponse response = await request.WaitAsync(TimeSpan.FromSeconds(10));
            response.StatusCode.Should().Be(500, "confirm-time re-validation must reject the trashed item");
            bridge.ClipboardCalls.Should().Be(0);
            handler.IsPickInProgress.Should().BeFalse();
        }
    }
}
