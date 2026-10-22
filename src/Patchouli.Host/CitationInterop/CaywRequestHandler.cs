using Patchouli.Core.CitationInterop;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Host.CitationInterop;

/// <summary>A transport-neutral HTTP response for the interop handlers.</summary>
public sealed record CitationHttpResponse(int StatusCode, string ContentType, string Body)
{
    public static CitationHttpResponse Text(int statusCode, string body)
    {
        return new CitationHttpResponse(statusCode, "text/plain; charset=utf-8", body);
    }
}

/// <summary>
/// The CAYW request lifecycle (issue #14 §4), independent of the HTTP stack so
/// it can be tested with a fake bridge:
/// one interactive request at a time (extra requests fail immediately with a
/// diagnosable busy error), an interactive deadline separate from API
/// timeouts, cancellation from client disconnect / deadline / host shutdown,
/// completion at most once, and desktop side effects (clipboard / select /
/// minimize) only for confirmed picks that are still connected.
/// </summary>
public sealed class CaywRequestHandler
{
    private static readonly HashSet<string> ReservedParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "probe", "format", "command", "brackets", "clipboard", "minimize", "selected", "select",
        "texstudio", "style", "locale", "html"
    };

    private readonly CitationInteropService _service;
    private readonly ICitationDesktopBridge _bridge;
    private readonly SemaphoreSlim _interactiveGate = new(1, 1);
    private readonly TimeSpan _interactiveDeadline;
    private readonly Action<Exception, string>? _reportUnexpected;

    public CaywRequestHandler(
        CitationInteropService service,
        ICitationDesktopBridge bridge,
        TimeSpan? interactiveDeadline = null,
        Action<Exception, string>? reportUnexpected = null)
    {
        _service = service;
        _bridge = bridge;
        _interactiveDeadline = interactiveDeadline ?? CaywCompatibility.DefaultInteractiveDeadline;
        _reportUnexpected = reportUnexpected;
    }

    /// <summary>True while an interactive pick is in flight (diagnostics/tests).</summary>
    public bool IsPickInProgress => _interactiveGate.CurrentCount == 0;

    /// <summary>
    /// Probe: ready only when an interactive picker can actually be shown.
    /// Headless hosts answer 503 with an explanatory body instead of claiming
    /// a readiness they cannot honor.
    /// </summary>
    public CitationHttpResponse HandleProbe()
    {
        return _bridge.IsInteractiveAvailable
            ? CitationHttpResponse.Text(200, CaywCompatibility.ProbeReadyBody)
            : CitationHttpResponse.Text(503,
                "not ready: this Patchouli host has no interactive citation picker (headless). JSON-RPC search/export remains available.");
    }

    public async Task<CitationHttpResponse> HandleAsync(
        IReadOnlyDictionary<string, string> query,
        CancellationToken requestAborted)
    {
        if (IsSet(query, "probe"))
        {
            return HandleProbe();
        }

        if (IsSet(query, "texstudio"))
        {
            return CitationHttpResponse.Text(500,
                "unsupported: TeXstudio push (texstudio=1) is not supported by Patchouli CAYW.");
        }

        string? format = Get(query, "format");
        Result<CitationFormatters.Resolution> resolution = CitationFormatters.Resolve(format);
        if (resolution.IsFailure)
        {
            return CitationHttpResponse.Text(500, resolution.ErrorMessage!);
        }

        CitationFormatOptions options = BuildOptions(query, format);

        if (IsSet(query, "selected"))
        {
            return await HandleSelectedAsync(query, options, IsHtmlPreferred(query), requestAborted);
        }

        return await HandleInteractiveAsync(query, options, IsHtmlPreferred(query), requestAborted);
    }

    private async Task<CitationHttpResponse> HandleSelectedAsync(
        IReadOnlyDictionary<string, string> query,
        CitationFormatOptions options,
        bool preferHtml,
        CancellationToken requestAborted)
    {
        if (!_bridge.IsInteractiveAvailable)
        {
            return CitationHttpResponse.Text(500,
                "ui_unavailable: 'selected' requires the Patchouli desktop window, which this host does not have.");
        }

        IReadOnlyList<ItemId> selectedIds;
        try
        {
            selectedIds = await _bridge.GetSelectedItemIdsAsync(requestAborted);
        }
        catch (CitationUiUnavailableException)
        {
            return CitationHttpResponse.Text(500,
                "ui_unavailable: 'selected' requires the Patchouli desktop window, which this host does not have.");
        }

        if (selectedIds.Count == 0)
        {
            return CitationHttpResponse.Text(200, "");
        }

        CitationBridgePickEntry[] entries = selectedIds
            .Select(static id => new CitationBridgePickEntry(id)).ToArray();
        Result<CitationPick> pick = await _service.ValidatePickAsync(entries, requestAborted);
        if (pick.IsFailure)
        {
            return CitationHttpResponse.Text(500, $"{pick.ErrorCode}: {pick.ErrorMessage}");
        }

        Result<CitationFormattedOutput> formatted = await _service.FormatPickAsync(
            pick.Value, Get(query, "format"), options, Get(query, "style"), Get(query, "locale"),
            preferHtml, requestAborted);
        if (formatted.IsFailure)
        {
            return CitationHttpResponse.Text(500, $"{formatted.ErrorCode}: {formatted.ErrorMessage}");
        }

        await ApplySideEffectsAsync(query, pick.Value, formatted.Value.Body, requestAborted);
        return new CitationHttpResponse(200, formatted.Value.ContentType, formatted.Value.Body);
    }

    private async Task<CitationHttpResponse> HandleInteractiveAsync(
        IReadOnlyDictionary<string, string> query,
        CitationFormatOptions options,
        bool preferHtml,
        CancellationToken requestAborted)
    {
        if (!_bridge.IsInteractiveAvailable)
        {
            return CitationHttpResponse.Text(500,
                "ui_unavailable: this Patchouli host has no interactive citation picker (headless). Use the desktop app, 'selected' from a desktop host, or the JSON-RPC API.");
        }

        bool gateHeld;
        try
        {
            gateHeld = await _interactiveGate.WaitAsync(0, requestAborted);
        }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
        {
            return CitationHttpResponse.Text(200, "");
        }

        if (!gateHeld)
        {
            return CitationHttpResponse.Text(500,
                "busy: another citation pick is already in progress in Patchouli; finish or cancel it first.");
        }

        try
        {
            using CancellationTokenSource deadline = new(_interactiveDeadline);
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(requestAborted, deadline.Token);

            CitationPickBridgeResult bridgeResult;
            try
            {
                await _bridge.ActivateMainWindowAsync(linked.Token);
                bridgeResult = await _bridge.PickCitationsAsync(linked.Token);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                // Client disconnect, deadline or host shutdown: the pick is
                // cancelled, the window is closed by the bridge, and no side
                // effects may happen afterwards.
                return CitationHttpResponse.Text(200, "");
            }

            if (bridgeResult.Outcome == CitationPickOutcome.Unavailable)
            {
                return CitationHttpResponse.Text(500,
                    "ui_unavailable: this Patchouli host has no interactive citation picker (headless).");
            }

            if (bridgeResult.Outcome == CitationPickOutcome.Cancelled || bridgeResult.Entries.Count == 0)
            {
                return CitationHttpResponse.Text(200, "");
            }

            // Completion guard, shared by every cancellation source: a late
            // confirmation racing a disconnect OR the interactive deadline
            // must not be validated, formatted or produce side effects for a
            // request that is already over. Validation/formatting run on the
            // linked token so a deadline expiring mid-flight aborts them too;
            // the catch below converges that abort to the same cancelled
            // result instead of leaking an exception or a partial output.
            try
            {
                if (linked.IsCancellationRequested)
                {
                    return CitationHttpResponse.Text(200, "");
                }

                Result<CitationPick> pick = await _service.ValidatePickAsync(bridgeResult.Entries, linked.Token);
                if (pick.IsFailure)
                {
                    return CitationHttpResponse.Text(500, $"{pick.ErrorCode}: {pick.ErrorMessage}");
                }

                Result<CitationFormattedOutput> formatted = await _service.FormatPickAsync(
                    pick.Value, Get(query, "format"), options, Get(query, "style"), Get(query, "locale"),
                    preferHtml, linked.Token);
                if (formatted.IsFailure)
                {
                    return CitationHttpResponse.Text(500, $"{formatted.ErrorCode}: {formatted.ErrorMessage}");
                }

                if (linked.IsCancellationRequested)
                {
                    return CitationHttpResponse.Text(200, "");
                }

                await ApplySideEffectsAsync(query, pick.Value, formatted.Value.Body, linked.Token);
                return new CitationHttpResponse(200, formatted.Value.ContentType, formatted.Value.Body);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                return CitationHttpResponse.Text(200, "");
            }
        }
        finally
        {
            _interactiveGate.Release();
        }
    }

    /// <summary>
    /// Desktop side effects, executed only for a confirmed, still-connected
    /// request and only when the corresponding flag parameter is set.
    /// Failures are reported but do not change the response the client
    /// already earned.
    /// </summary>
    private async Task ApplySideEffectsAsync(
        IReadOnlyDictionary<string, string> query, CitationPick pick, string formattedBody,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || pick.IsEmpty)
        {
            return;
        }

        try
        {
            if (IsSet(query, "clipboard"))
            {
                await _bridge.SetClipboardTextAsync(formattedBody, cancellationToken);
            }

            if (IsSet(query, "select"))
            {
                await _bridge.SelectItemsAsync(
                    pick.Entries.Select(static entry => entry.ItemId).ToArray(), cancellationToken);
            }

            if (IsSet(query, "minimize"))
            {
                await _bridge.MinimizeMainWindowAsync(cancellationToken);
            }
        }
        catch (CitationUiUnavailableException)
        {
            // A side effect raced a UI teardown; the formatted result stands.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _reportUnexpected?.Invoke(exception, "cayw-side-effects");
        }
    }

    private static CitationFormatOptions BuildOptions(IReadOnlyDictionary<string, string> query, string? format)
    {
        string? command = Get(query, "command") ?? CaywCompatibility.AliasCommand(format ?? "");
        Dictionary<string, string> labelOverrides = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> pair in query)
        {
            if (!ReservedParameters.Contains(pair.Key))
            {
                labelOverrides[pair.Key] = pair.Value;
            }
        }

        return new CitationFormatOptions(
            command,
            IsSet(query, "brackets"),
            labelOverrides.Count == 0 ? null : labelOverrides);
    }

    private static bool IsHtmlPreferred(IReadOnlyDictionary<string, string> query)
    {
        // CAYW clients are editors/curl; HTML is opt-in via an explicit flag so
        // the default formatted output stays plain text.
        return IsSet(query, "html");
    }

    private static bool IsSet(IReadOnlyDictionary<string, string> query, string name)
    {
        return query.TryGetValue(name, out string? value) && CaywCompatibility.IsFlagSet(value);
    }

    private static string? Get(IReadOnlyDictionary<string, string> query, string name)
    {
        return query.TryGetValue(name, out string? value) ? value : null;
    }
}
