using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Host.CitationInterop;

namespace Patchouli.Tests.CitationInterop;

/// <summary>CAYW request lifecycle against a fake desktop bridge (issue #14 §4).</summary>
public sealed class CaywRequestHandlerTests
{
    private sealed class FakeBridge : ICitationDesktopBridge
    {
        public bool Available { get; set; } = true;

        public Func<CancellationToken, Task<CitationPickBridgeResult>>? OnPick { get; set; }

        public IReadOnlyList<ItemId> Selected { get; set; } = [];

        public List<string> Calls { get; } = new();

        public string? ClipboardText { get; private set; }

        public IReadOnlyList<ItemId>? SelectedViaBridge { get; private set; }

        public bool Minimized { get; private set; }

        public bool Activated { get; private set; }

        public bool IsInteractiveAvailable => Available;

        public Task<CitationPickBridgeResult> PickCitationsAsync(CancellationToken cancellationToken)
        {
            Calls.Add("pick");
            return OnPick?.Invoke(cancellationToken)
                   ?? Task.FromResult(CitationPickBridgeResult.CancelledResult);
        }

        public Task<IReadOnlyList<ItemId>> GetSelectedItemIdsAsync(CancellationToken cancellationToken)
        {
            Calls.Add("selected-snapshot");
            return Task.FromResult(Selected);
        }

        public Task SetClipboardTextAsync(string text, CancellationToken cancellationToken)
        {
            Calls.Add("clipboard");
            ClipboardText = text;
            return Task.CompletedTask;
        }

        public Task SelectItemsAsync(IReadOnlyList<ItemId> itemIds, CancellationToken cancellationToken)
        {
            Calls.Add("select");
            SelectedViaBridge = itemIds;
            return Task.CompletedTask;
        }

        public Task MinimizeMainWindowAsync(CancellationToken cancellationToken)
        {
            Calls.Add("minimize");
            Minimized = true;
            return Task.CompletedTask;
        }

        public Task ActivateMainWindowAsync(CancellationToken cancellationToken)
        {
            Calls.Add("activate");
            Activated = true;
            return Task.CompletedTask;
        }
    }

    private static Dictionary<string, string> Query(params (string Key, string Value)[] pairs)
    {
        return pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<(CaywRequestHandler Handler, FakeBridge Bridge, InteropTestContext Context)>
        CreateAsync(FakeBridge bridge, TimeSpan? deadline = null)
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        CaywRequestHandler handler = new(context.Interop, bridge, deadline);
        return (handler, bridge, context);
    }

    [Fact]
    public async Task Probe_is_ready_only_when_a_picker_exists()
    {
        FakeBridge bridge = new();
        (CaywRequestHandler handler, _, InteropTestContext context) = await CreateAsync(bridge);
        await using (context)
        {
            handler.HandleProbe().StatusCode.Should().Be(200);
            handler.HandleProbe().Body.Should().Be("ready");

            bridge.Available = false;
            CitationHttpResponse response = handler.HandleProbe();
            response.StatusCode.Should().Be(503);
            response.Body.Should().Contain("not ready");
        }
    }

    [Fact]
    public async Task Headless_interactive_request_fails_fast_with_ui_unavailable()
    {
        FakeBridge bridge = new() { Available = false };
        (CaywRequestHandler handler, _, InteropTestContext context) = await CreateAsync(bridge);
        await using (context)
        {
            CitationHttpResponse response = await handler.HandleAsync(Query(), CancellationToken.None);
            response.StatusCode.Should().Be(500);
            response.Body.Should().Contain("ui_unavailable");
            bridge.Calls.Should().NotContain("pick");
        }
    }

    [Fact]
    public async Task Cancelled_pick_is_an_empty_success_and_touches_nothing()
    {
        FakeBridge bridge = new();
        (CaywRequestHandler handler, _, InteropTestContext context) = await CreateAsync(bridge);
        await using (context)
        {
            CitationHttpResponse response = await handler.HandleAsync(
                Query(("clipboard", "1"), ("select", "1"), ("minimize", "1")), CancellationToken.None);

            response.StatusCode.Should().Be(200);
            response.Body.Should().Be("");
            bridge.ClipboardText.Should().BeNull();
            bridge.SelectedViaBridge.Should().BeNull();
            bridge.Minimized.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Busy_gate_rejects_a_second_interactive_request_immediately()
    {
        TaskCompletionSource<CitationPickBridgeResult> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeBridge bridge = new() { OnPick = _ => gate.Task };
        (CaywRequestHandler handler, _, InteropTestContext context) = await CreateAsync(bridge);
        await using (context)
        {
            Task<CitationHttpResponse> first = handler.HandleAsync(Query(), CancellationToken.None);
            // Let the first request enter the picker.
            await Task.Delay(50);

            CitationHttpResponse second = await handler.HandleAsync(Query(), CancellationToken.None);
            second.StatusCode.Should().Be(500);
            second.Body.Should().Contain("busy");

            gate.SetResult(CitationPickBridgeResult.CancelledResult);
            CitationHttpResponse firstResponse = await first;
            firstResponse.StatusCode.Should().Be(200);
            handler.IsPickInProgress.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Deadline_cancels_the_pick_and_returns_empty()
    {
        FakeBridge bridge = new()
        {
            OnPick = async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return CitationPickBridgeResult.CancelledResult;
            }
        };
        (CaywRequestHandler handler, _, InteropTestContext context) =
            await CreateAsync(bridge, TimeSpan.FromMilliseconds(150));
        await using (context)
        {
            CitationHttpResponse response = await handler.HandleAsync(Query(), CancellationToken.None);
            response.StatusCode.Should().Be(200);
            response.Body.Should().Be("");
            handler.IsPickInProgress.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Client_disconnect_cancels_the_pick()
    {
        FakeBridge bridge = new()
        {
            OnPick = async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return CitationPickBridgeResult.CancelledResult;
            }
        };
        (CaywRequestHandler handler, _, InteropTestContext context) = await CreateAsync(bridge);
        await using (context)
        {
            using CancellationTokenSource disconnect = new(TimeSpan.FromMilliseconds(150));
            CitationHttpResponse response = await handler.HandleAsync(Query(), disconnect.Token);
            response.StatusCode.Should().Be(200);
            response.Body.Should().Be("");
        }
    }

    [Fact]
    public async Task Texstudio_push_is_an_explicit_unsupported_error()
    {
        FakeBridge bridge = new();
        (CaywRequestHandler handler, _, InteropTestContext context) = await CreateAsync(bridge);
        await using (context)
        {
            CitationHttpResponse response = await handler.HandleAsync(
                Query(("texstudio", "1")), CancellationToken.None);
            response.StatusCode.Should().Be(500);
            response.Body.Should().Contain("TeXstudio");
        }
    }

    [Fact]
    public async Task Unsupported_and_unknown_formats_are_stable_errors()
    {
        FakeBridge bridge = new();
        (CaywRequestHandler handler, _, InteropTestContext context) = await CreateAsync(bridge);
        await using (context)
        {
            CitationHttpResponse unsupported = await handler.HandleAsync(
                Query(("format", "mmd")), CancellationToken.None);
            unsupported.StatusCode.Should().Be(500);
            unsupported.Body.Should().Contain("not supported");

            CitationHttpResponse unknown = await handler.HandleAsync(
                Query(("format", "docx")), CancellationToken.None);
            unknown.StatusCode.Should().Be(500);
            unknown.Body.Should().Contain("Unknown CAYW format");
        }
    }

    [Fact]
    public async Task Selected_with_false_string_still_means_selected()
    {
        // BBT flag rule: ANY non-empty value sets the flag, including "false".
        FakeBridge bridge = new();
        (CaywRequestHandler handler, _, InteropTestContext context) = await CreateAsync(bridge);
        await using (context)
        {
            CitationHttpResponse response = await handler.HandleAsync(
                Query(("selected", "false")), CancellationToken.None);

            response.StatusCode.Should().Be(200);
            response.Body.Should().Be(""); // empty selection -> empty output
            bridge.Calls.Should().Contain("selected-snapshot");
            bridge.Calls.Should().NotContain("pick");
        }
    }

    [Fact]
    public async Task Selected_snapshot_formats_without_opening_the_picker()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            ItemMetadata book = await context.CreateItemAsync(
                new CreateItemRequest("book", "Selected Book"));
            FakeBridge bridge = new() { Selected = [book.ItemId] };
            CaywRequestHandler handler = new(context.Interop, bridge);

            CitationHttpResponse response = await handler.HandleAsync(
                Query(("selected", "1"), ("format", "natbib")), CancellationToken.None);

            response.StatusCode.Should().Be(200);
            response.Body.Should().Be($"\\cite{{{book.CitationKey}}}");
            bridge.Calls.Should().NotContain("pick");
        }
    }

    [Fact]
    public async Task Confirmed_pick_side_effects_match_the_flags()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            ItemMetadata book = await context.CreateItemAsync(
                new CreateItemRequest("book", "Picked Book"));
            FakeBridge bridge = new()
            {
                OnPick = _ => Task.FromResult(new CitationPickBridgeResult(
                    CitationPickOutcome.Confirmed,
                    [new CitationBridgePickEntry(book.ItemId, "33")]))
            };
            CaywRequestHandler handler = new(context.Interop, bridge);

            CitationHttpResponse response = await handler.HandleAsync(
                Query(("format", "natbib"), ("clipboard", "1")), CancellationToken.None);

            response.StatusCode.Should().Be(200);
            response.Body.Should().Be($"\\cite[p. 33]{{{book.CitationKey}}}");
            bridge.ClipboardText.Should().Be(response.Body);
            bridge.SelectedViaBridge.Should().BeNull(); // select flag not set
            bridge.Minimized.Should().BeFalse(); // minimize flag not set
        }
    }

    [Fact]
    public async Task Late_confirmation_after_disconnect_produces_no_side_effects()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            ItemMetadata book = await context.CreateItemAsync(
                new CreateItemRequest("book", "Late Book"));
            using CancellationTokenSource disconnect = new();
            FakeBridge bridge = new()
            {
                OnPick = _ =>
                {
                    // The user confirms at the same moment the client hangs up.
                    disconnect.Cancel();
                    return Task.FromResult(new CitationPickBridgeResult(
                        CitationPickOutcome.Confirmed, [new CitationBridgePickEntry(book.ItemId)]));
                }
            };
            CaywRequestHandler handler = new(context.Interop, bridge);

            CitationHttpResponse response = await handler.HandleAsync(
                Query(("clipboard", "1")), disconnect.Token);

            response.StatusCode.Should().Be(200);
            response.Body.Should().Be("");
            bridge.ClipboardText.Should().BeNull();
        }
    }
}
