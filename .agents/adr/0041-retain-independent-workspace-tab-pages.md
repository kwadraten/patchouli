# ADR 0041: Retain independent workspace tab pages

Status: accepted, 2026-10-09.

## Context

Commit `cf49e6f` (2026-07-07) replaced permanently mounted workspace pages with
data-templated Avalonia `TabControl` content. No corresponding ADR exists. The
single selected-content presenter does not preserve an independent visual tree
for every tab: templates may rebuild a page or recycle a visual across data
contexts. View models survive, but scroll offsets, selections, caret positions,
and expanded controls do not reliably survive switches. PDF reattachment also
replays the reading document and reapplies its original anchor; chat activation
unconditionally scrolls to the newest message.

This decision applies to all workspace tab kinds and preserves ADR 0033's UI
reactivity boundaries. Runtime services and running agent/OCR operations remain
host-owned. A page's visibility is not the lifetime of its underlying operation.

## Decision

Adopt these five principles:

1. **Ownership follows the tab instance.** Each open workspace tab owns an
   independent page host and visual tree. Resolve the existing page templates
   against that tab's content. Never share a page by view-model type, and never
   retain closed pages in an application-wide static cache. The UI host owns
   controls; view models need not reference Avalonia controls.
2. **Materialize on first activation, then retain.** Create the page only when
   its tab is first selected. Keep realized pages attached to their existing
   host and hide inactive pages using `IsVisible = false`. Switching must not
   change the page's data context, replay unchanged documents, or reset its
   user-controlled state. Genuine document/session changes may initialize new
   content. Preserve the page across collection moves and selection changes.
3. **Activation is explicit.** Activate and deactivate pages independently of
   visual-tree attachment. Suspend front-end polling, speculative image loads,
   prefetch, and unnecessary UI rebuilds while inactive. Keep domain operations
   running according to their existing ownership. Reconcile missed updates on
   return while preserving reading position and the user's follow-latest choice.
4. **Retain state while bounding rebuildable resources.** Keep page state and
   useful text layouts, but preserve existing viewport-based image caching and
   cancellation. Release reloadable media when inactive without resetting
   scrolling. Large lists remain virtualized; hiding a page does not by itself
   stop bindings or event callbacks. Do not eagerly rebuild every hidden page.
5. **Close performs deterministic cleanup.** Removing a tab releases its page,
   view-specific tasks, subscriptions, image resources, and host references.
   Collection resets, content-host replacement, and window teardown must also
   clean up. Existing ownership rules still decide whether the content view
   model is disposed: closing a chat front end must not terminate its session,
   and window-owned singleton view models remain usable on reopen.

Use a tab strip and a workspace-scoped retained content panel. A small page
lifecycle interface provides activation, deactivation, and close callbacks for
pages with view-specific work. Ordinary pages require no custom callback: their
independent retained control tree already preserves interface state. Focus moves
to visible content only; hidden pages must not receive input.

## Consequences and verification

Switching avoids page construction and repeated reading-document reconstruction.
Avalonia skips hidden subtrees during normal layout and rendering, but memory
includes realized open pages and event work must be gated explicitly. No claim
of measured switching latency or constant memory is made.

Tests must exercise independent same-type tabs, lazy construction, scroll/caret
retention, inactivity, closing/reopening, collection reset/replacement, and window
teardown. PDF and chat tests must verify their actual page behavior rather than
only the view-model state. Preserve existing page-navigation and session-start
semantics. Use lifecycle/build-count assertions and resource-release checks
instead of fragile wall-clock performance thresholds.

## Alternatives and evidence

- Default `TabControl` plus saved scroll offsets leaves other control state and
  attach-time side effects unaddressed.
- Caching controls and remounting the selected page reduces construction but
  still invokes attachment/data-context paths. ILSpy has explicit regressions
  for these state resets and gives each dockable ownership of its realized view.
- A full docking framework adds unnecessary workspace behavior for this change.
  Reconsider it when split panes or floating document windows are requested.

Primary implementation references:

- [Avalonia tab lifecycle documentation](https://docs.avaloniaui.net/docs/data-binding/how-to-bind-tabs)
- [Avalonia retained-tab discussion](https://github.com/AvaloniaUI/Avalonia/discussions/11837)
- [Dock retained content template](https://github.com/wieslawsoltes/Dock/blob/84b92a66ba7ab213acec27aa37e1dfda855a96de/src/Dock.Avalonia.Themes.Fluent/Controls/DocumentControl.axaml)
- [ILSpy tab-owned views](https://github.com/icsharpcode/ILSpy/blob/ad2cc62293017fc3e383d2fb1f232fde7fb120c4/ILSpy/Docking/DockableViewRecycling.cs)
- [Stability Matrix persistent views](https://github.com/LykosAI/StabilityMatrix/blob/604387e55633550c248c9b71b7a2968711210286/StabilityMatrix.Avalonia/ViewLocator.cs)
- [Avalonia visibility and layout](https://docs.avaloniaui.net/docs/layout)
