using Avalonia.Headless;

namespace Patchouli.Tests;

// Serialized collection for UI tests that still create a private HeadlessUnitTestSession per
// case. Its DisableParallelization keeps those per-case sessions from ever overlapping the
// shared-session collection below.
//
// R14 audit — resource matrix behind DisableParallelization on BOTH UI collections.
// Two separate requirements were evaluated:
//   (a) "UI tests serial among themselves" — already guaranteed by collection membership alone;
//        xunit never runs classes inside one collection concurrently.
//   (b) "UI isolated from every other collection" — required by the process-global roots below;
//        only DisableParallelization provides it, so it stays on both definitions.
//
//   Process-global / thread-affine root                Who touches it
//   Application.Current (process-global static)        every Dispatch (PerTest rebuilds the app);
//                                                      ThemePaletteApplier.Apply; DialogService.GetMainWindow;
//                                                      UiViewModelTests.CreateMainWindow scheduler probe
//   Dispatcher.UIThread (single dispatcher reference)  ModalOperationRunner/UpdateFlowService posts;
//                                                      RunJobs inside Dispatch
//   ThemePaletteApplier statics (CurrentPaletteId/     every `new MainWindowViewModel` — the ctor calls
//     CurrentThemeMode/CurrentEffectiveIsDark/         ThemePaletteApplier.Apply unconditionally,
//     _watchedApp)                                     including session-less classes in this collection;
//                                                      asserted on by ThemePaletteApplierTests
//   Application.Resources / RequestedThemeVariant      Apply mutates them on the calling thread —
//                                                      safe only on the owning dispatcher or serialized
//   static AvaloniaObjects                             TextDecorations.Underline/Strikethrough are owned
//                                                      by the first thread touching them; sharing across
//                                                      sessions throws cross-thread access
//                                                      (src/Patchouli.Reading/ReadingView.cs)
//   AvaloniaLocator global registry                    rebound in-process by
//                                                      ThemePaletteApplierTests.TriggerSystemTheme
//   AvaloniaHeadlessPlatform                           ForceRenderTimerTick is process-global render state
//   SqliteConnection pools                             several classes purge via ClearAllPools in teardown
//   loopback ports / compiled extensions               tests may exercise host-side extension loading;
//                                                      no extension worker process is involved
//
// Conclusion (R14): keep DisableParallelization on both collections. A session-less
// `new MainWindowViewModel` still writes ThemePaletteApplier statics and reads Application.Current,
// so the absence of a Dispatch call is not evidence of UI-freedom; narrowing isolation would need
// per-class proof of zero contact with the roots above plus repeated-run evidence — not available
// today. See tests/test-suites.json "r14_collection_audit" for the sanctioned experiment matrix.
[CollectionDefinition("Avalonia", DisableParallelization = true)]
public sealed class AvaloniaTestCollection : ICollectionFixture<UiThreadExecutionGateFixture>;

// The single shared headless session lives behind exactly ONE collection definition. Tests that
// touch the same static Avalonia objects (markdown text decorations, retained tab chrome,
// dispatcher-bound caches and property subscriptions) must dispatch on the same UI thread, so
// classes migrated off per-test StartNew join THIS collection and inject the shared fixture
// instead of a second collection new-ing up its own session/thread. PerTest isolation is
// unchanged: every Dispatch still builds a fresh Application on that thread, and each case
// creates/closes its own settings, view models, windows, hosts and databases.
[CollectionDefinition("RetainedTabUI", DisableParallelization = true)]
public sealed class RetainedTabUITestCollection : ICollectionFixture<AvaloniaSharedSessionFixture>,
    ICollectionFixture<UiThreadExecutionGateFixture>;

/// <summary>
/// Owns the collection's one <see cref="HeadlessUnitTestSession"/> through explicit xunit
/// lifecycle: created in <see cref="InitializeAsync"/>, released asynchronously in
/// <see cref="DisposeAsync"/> so the dispatcher loop can drain before the thread stops.
/// </summary>
public sealed class AvaloniaSharedSessionFixture : IAsyncLifetime
{
    private HeadlessUnitTestSession? _session;

    // Markdown text decorations are static AvaloniaObjects: all Markdown tests must use this thread.
    // Per-test application isolation is retained by each Dispatch call.
    public HeadlessUnitTestSession Session =>
        _session ?? throw new InvalidOperationException("The shared headless session is not running.");

    public Task InitializeAsync()
    {
        _session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        // xunit can call DisposeAsync without InitializeAsync having run (e.g. when every case in
        // the collection is skipped), so tolerate a null session.
        if (_session is { } session)
        {
            _session = null;
            await session.DisposeAsync();
        }
    }
}
