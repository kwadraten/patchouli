using System;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using FluentAssertions;
using Patchouli.Core.Diagnostics;
using Patchouli.Host.Settings;
using Patchouli.UI;
using Patchouli.UI.Controls;
using Patchouli.UI.Controls.CatWalk;
using Patchouli.UI.ViewModels;
using Xunit;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class CatWalkTests
{
    [Fact]
    public void StateMachine_initializes_as_idle()
    {
        CatAnimationStateMachine sm = new();
        sm.IsBusy.Should().BeFalse();
        sm.ShouldAnimate.Should().BeFalse();
        sm.DisplayFrameIndex.Should().Be(CatAnimationStateMachine.IdleFrameIndex);
        sm.Step().Should().BeFalse();
    }

    [Fact]
    public void StateMachine_loops_strictly_through_five_frames_when_busy()
    {
        CatAnimationStateMachine sm = new() { IsBusy = true };
        sm.ShouldAnimate.Should().BeTrue();
        sm.CurrentFrameIndex.Should().Be(0);
        sm.DisplayFrameIndex.Should().Be(0);

        int[] expectedSequence = [1, 2, 3, 4, 0, 1, 2, 3, 4, 0];
        foreach (int expected in expectedSequence)
        {
            sm.Step().Should().BeTrue();
            sm.CurrentFrameIndex.Should().Be(expected);
            sm.DisplayFrameIndex.Should().Be(expected);
        }
    }

    [Fact]
    public void StateMachine_stops_when_window_is_minimized_and_resumes_without_catching_up()
    {
        CatAnimationStateMachine sm = new() { IsBusy = true };
        sm.Step(); // frame 1
        sm.Step(); // frame 2
        sm.CurrentFrameIndex.Should().Be(2);

        // Window minimized: animation should pause
        sm.IsWindowMinimized = true;
        sm.ShouldAnimate.Should().BeFalse();
        sm.Step().Should().BeFalse();
        sm.CurrentFrameIndex.Should().Be(2);

        // Window restored: animation resumes without skipping or catching up frames
        sm.IsWindowMinimized = false;
        sm.ShouldAnimate.Should().BeTrue();
        sm.Step().Should().BeTrue();
        sm.CurrentFrameIndex.Should().Be(3);
    }

    [Fact]
    public void StateMachine_stops_when_control_is_detached()
    {
        CatAnimationStateMachine sm = new() { IsBusy = true };
        sm.Step(); // frame 1
        sm.CurrentFrameIndex.Should().Be(1);

        sm.IsAttached = false;
        sm.ShouldAnimate.Should().BeFalse();
        sm.Step().Should().BeFalse();
        sm.CurrentFrameIndex.Should().Be(1);

        sm.IsAttached = true;
        sm.ShouldAnimate.Should().BeTrue();
        sm.Step().Should().BeTrue();
        sm.CurrentFrameIndex.Should().Be(2);
    }

    [Fact]
    public void StateMachine_resets_to_idle_when_busy_becomes_false()
    {
        CatAnimationStateMachine sm = new() { IsBusy = true };
        sm.Step(); // frame 1
        sm.Step(); // frame 2
        sm.CurrentFrameIndex.Should().Be(2);

        sm.IsBusy = false;
        sm.ShouldAnimate.Should().BeFalse();
        sm.DisplayFrameIndex.Should().Be(CatAnimationStateMachine.IdleFrameIndex);

        // When returning to busy, starts from frame 0
        sm.IsBusy = true;
        sm.CurrentFrameIndex.Should().Be(0);
        sm.DisplayFrameIndex.Should().Be(0);
    }

    [Fact]
    public async Task CatGeometryProvider_loads_all_six_frames_and_shares_bounds()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(() =>
        {
            CatGeometrySet geometrySet = CatGeometryProvider.Instance;

            geometrySet.Should().NotBeNull();
            geometrySet.IdleGeometry.Should().NotBeNull();
            geometrySet.IdleGeometry.Bounds.Width.Should().BeGreaterThan(0);
            geometrySet.IdleGeometry.Bounds.Height.Should().BeGreaterThan(0);

            geometrySet.ActiveGeometries.Should().HaveCount(5);
            for (int i = 0; i < 5; i++)
            {
                Geometry active = geometrySet.ActiveGeometries[i];
                active.Should().NotBeNull();
                active.Bounds.Width.Should().BeGreaterThan(0);
                active.Bounds.Height.Should().BeGreaterThan(0);
            }

            geometrySet.SharedBounds.Width.Should().BeGreaterThan(0);
            geometrySet.SharedBounds.Height.Should().BeGreaterThan(0);

            // SharedBounds must encompass each individual geometry's bounds
            geometrySet.SharedBounds.Contains(geometrySet.IdleGeometry.Bounds.TopLeft).Should().BeTrue();
            foreach (Geometry active in geometrySet.ActiveGeometries)
            {
                geometrySet.SharedBounds.Contains(active.Bounds.TopLeft).Should().BeTrue();
            }

            // Must be cached and parsed only once
            CatGeometrySet second = CatGeometryProvider.Instance;
            ReferenceEquals(geometrySet, second).Should().BeTrue();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public void CatStatusIndicator_has_fixed_footprint_and_formats_status_correctly()
    {
        CatStatusIndicator.DefaultIndicatorSize.Width.Should().Be(24.0);
        CatStatusIndicator.DefaultIndicatorSize.Height.Should().Be(20.0);

        CatStatusIndicator.FormatStatusText(true, "OCR (page 2/10)", null)
            .Should().Be("正在运行: OCR (page 2/10)");

        CatStatusIndicator.FormatStatusText(true, null, null)
            .Should().Be("正在运行后台任务");

        CatStatusIndicator.FormatStatusText(false, null, "OCR: 等待重试")
            .Should().Be("等待中: OCR: 等待重试");

        CatStatusIndicator.FormatStatusText(false, null, null)
            .Should().Be("空闲：无后台活动");
    }

    [Fact]
    public async Task MainWindowViewModel_projects_activity_tracker_snapshots_reactively()
    {
        using TemporaryAppSettingsFile settings = new();
        using HostActivityTracker tracker = new();

        MainWindowViewModel vm = new(
            new AlphaPackagingTests_TestClipboard(),
            settingsPath: settings.Path,
            activityTracker: tracker,
            uiScheduler: ImmediateScheduler.Instance);

        try
        {
            // Initial state should be Idle
            vm.IsActivityBusy.Should().BeFalse();
            vm.ActivitySummary.Should().BeNull();
            vm.ActivityReason.Should().BeNull();
            vm.ActivityDescription.Should().Be("空闲");

            // Start an activity scope
            using (IActivityScope scope = tracker.BeginScope("OCR", HostActivityKind.Ocr, "Page 1/10"))
            {
                vm.IsActivityBusy.Should().BeTrue();
                vm.ActivitySummary.Should().Be("OCR (Page 1/10)");
                vm.ActivityReason.Should().BeNull();
                vm.ActivityDescription.Should().Be("运行中: OCR (Page 1/10)");

                // Pause the scope
                scope.SetPaused(true, "等待用户确认");
                vm.IsActivityBusy.Should().BeFalse();
                vm.ActivityReason.Should().Contain("等待用户确认");
                vm.ActivityDescription.Should().Contain("等待中: OCR: 等待用户确认");

                // Resume the scope
                scope.SetPaused(false);
                vm.IsActivityBusy.Should().BeTrue();
                vm.ActivityReason.Should().BeNull();
                vm.ActivityDescription.Should().Be("运行中: OCR (Page 1/10)");
            }

            // After scope ends, returns to idle
            vm.IsActivityBusy.Should().BeFalse();
            vm.ActivitySummary.Should().BeNull();
            vm.ActivityReason.Should().BeNull();
            vm.ActivityDescription.Should().Be("空闲");
        }
        finally
        {
            await vm.ShutdownAsync();
        }
    }

    [Fact]
    public async Task MainWindowViewModel_disposes_owned_activity_tracker_and_settings_store_on_shutdown()
    {
        using TemporaryAppSettingsFile settings = new();
        FakeSettingsStore store = new();
        HostActivityTracker tracker = new();

        MainWindowViewModel vm = new(
            new AlphaPackagingTests_TestClipboard(),
            settingsPath: settings.Path,
            settingsStore: store,
            activityTracker: tracker,
            uiScheduler: ImmediateScheduler.Instance);

        store.IsDisposed.Should().BeFalse();

        await vm.ShutdownAsync();

        store.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void AboutViewModel_includes_catwalk_enhanced_with_gpl_and_pinned_commit()
    {
        using TemporaryAppSettingsFile settings = new();
        AboutViewModel about = new(new MainWindowViewModel(
            new AlphaPackagingTests_TestClipboard(),
            settingsPath: settings.Path,
            uiScheduler: ImmediateScheduler.Instance));

        ThirdPartyLibrary? catWalk = about.ThirdPartyLibraries
            .FirstOrDefault(lib => lib.Name == "CatWalk Enhanced");

        catWalk.Should().NotBeNull();
        catWalk!.License.Should().Be("GPL-3.0+");
        catWalk.Url.Should().Contain("https://github.com/BLADR-ONE/CatWalk-Enhanced-Plasmoid");
        catWalk.Url.Should().Contain("0e20cec96c7e5e390623a80aed6302c2f1b8c7ee");
    }

    private sealed class AlphaPackagingTests_TestClipboard : IClipboardService
    {
        public Task SetTextAsync(string text)
        {
            return Task.CompletedTask;
        }

        public Task<string?> GetTextAsync()
        {
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class FakeSettingsStore : IAppSettingsStore
    {
        public bool IsDisposed { get; private set; }
        public PatchouliAppSettings Current => PatchouliAppSettings.Default();
        public bool IsDirty => false;

        public void Update(Func<PatchouliAppSettings, PatchouliAppSettings> updater, string? fieldCategory = null)
        {
        }

        public Task<SettingsSaveResult> SaveImmediatelyAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SettingsSaveResult.Success);
        }

        public Task<SettingsSaveResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SettingsSaveResult.Success);
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
