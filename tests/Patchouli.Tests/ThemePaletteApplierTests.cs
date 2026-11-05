using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using FluentAssertions;
using Patchouli.UI;
using Patchouli.UI.Themes;
using Patchouli.UI.ViewModels;
using Patchouli.UI.Views;
using Xunit;

namespace Patchouli.Tests;

// R13 shared session registration: this class mutates the process-wide ThemePaletteApplier
// statics (CurrentPaletteId/CurrentThemeMode/_watchedApp) also touched by
// ExtensionConfigurationDialogTests, so it must share that session's UI thread. Dispose detaches
// the watcher; TriggerSystemTheme pumps Dispatcher.UIThread, which is the session's thread while
// this collection runs.
[Collection("RetainedTabUI")]
public sealed class ThemePaletteApplierTests(AvaloniaSharedSessionFixture headless) : IDisposable
{
    public void Dispose()
    {
        ThemePaletteApplier.DetachWatcher();
    }

    [Fact]
    public async Task Watcher_lifecycle_attaches_and_detaches_cleanly()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch(() =>
        {
            Application app = Application.Current!;

            // Attach to app
            ThemePaletteApplier.AttachWatcher(app);

            // Attach again to same app is idempotent
            ThemePaletteApplier.AttachWatcher(app);

            // Detach cleanly
            ThemePaletteApplier.DetachWatcher();

            // Repeated detach is safe
            ThemePaletteApplier.DetachWatcher();

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ThemePaletteApplier_applies_explicit_modes_and_synchronizes_all_brushes()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch(() =>
        {
            Application app = Application.Current!;

            // Apply explicit Light mode
            ThemePaletteApplier.Apply(UiColorPalettes.DefaultPaletteId, UiPreferences.ThemeModeLight);

            ThemePaletteApplier.CurrentThemeMode.Should().Be(UiPreferences.ThemeModeLight);
            ThemePaletteApplier.CurrentEffectiveIsDark.Should().BeFalse();
            app.RequestedThemeVariant.Should().Be(ThemeVariant.Light);

            // Verify light auxiliary brushes
            app.TryFindResource("SurfaceContainerLowestBrush", out object? lowestRes).Should().BeTrue();
            ((ISolidColorBrush)lowestRes!).Color.Should().Be(Color.Parse("#FFFFFF"));

            app.TryFindResource("SurfaceVariantBrush", out object? svRes).Should().BeTrue();
            ((ISolidColorBrush)svRes!).Color.Should().Be(Color.Parse("#EFEDED"));

            app.TryFindResource("OnErrorContainerBrush", out object? oecRes).Should().BeTrue();
            ((ISolidColorBrush)oecRes!).Color.Should().Be(Color.Parse("#BA1A1A"));

            // Apply explicit Dark mode
            ThemePaletteApplier.Apply(UiColorPalettes.DefaultPaletteId, UiPreferences.ThemeModeDark);

            ThemePaletteApplier.CurrentThemeMode.Should().Be(UiPreferences.ThemeModeDark);
            ThemePaletteApplier.CurrentEffectiveIsDark.Should().BeTrue();
            app.RequestedThemeVariant.Should().Be(ThemeVariant.Dark);

            // Verify dark auxiliary brushes
            app.TryFindResource("SurfaceContainerLowestBrush", out object? darkLowestRes).Should().BeTrue();
            ((ISolidColorBrush)darkLowestRes!).Color.Should().Be(Color.Parse("#14121F"));

            app.TryFindResource("SurfaceVariantBrush", out object? darkSvRes).Should().BeTrue();
            ((ISolidColorBrush)darkSvRes!).Color.Should().Be(Color.Parse("#291F43"));

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Explicit_mode_is_not_overridden_by_system_theme_variant_change()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch(() =>
        {
            Application app = Application.Current!;


            // 1. Set explicit Light mode
            ThemePaletteApplier.Apply(UiColorPalettes.DefaultPaletteId, UiPreferences.ThemeModeLight);
            ThemePaletteApplier.CurrentThemeMode.Should().Be(UiPreferences.ThemeModeLight);
            ThemePaletteApplier.CurrentEffectiveIsDark.Should().BeFalse();
            app.TryFindResource("SurfaceContainerLowestBrush", out object? lightLowestRes).Should().BeTrue();
            ((ISolidColorBrush)lightLowestRes!).Color.Should().Be(Color.Parse("#FFFFFF"));

            // 2. Simulate system switching to Dark: explicit mode must ignore it
            TriggerSystemTheme(app, PlatformThemeVariant.Dark);
            ThemePaletteApplier.CurrentThemeMode.Should().Be(UiPreferences.ThemeModeLight);
            ThemePaletteApplier.CurrentEffectiveIsDark.Should().BeFalse();
            ((ISolidColorBrush)lightLowestRes!).Color.Should().Be(Color.Parse("#FFFFFF"));

            // 3. Simulate system switching back to Light: still Light
            TriggerSystemTheme(app, PlatformThemeVariant.Light);
            ThemePaletteApplier.CurrentThemeMode.Should().Be(UiPreferences.ThemeModeLight);
            ThemePaletteApplier.CurrentEffectiveIsDark.Should().BeFalse();
            ((ISolidColorBrush)lightLowestRes!).Color.Should().Be(Color.Parse("#FFFFFF"));

            // 4. Set explicit Dark mode
            ThemePaletteApplier.Apply(UiColorPalettes.DefaultPaletteId, UiPreferences.ThemeModeDark);
            ThemePaletteApplier.CurrentThemeMode.Should().Be(UiPreferences.ThemeModeDark);
            ThemePaletteApplier.CurrentEffectiveIsDark.Should().BeTrue();
            app.TryFindResource("SurfaceContainerLowestBrush", out object? darkLowestRes).Should().BeTrue();
            ((ISolidColorBrush)darkLowestRes!).Color.Should().Be(Color.Parse("#14121F"));

            // 5. Simulate system switching to Light: explicit Dark must ignore it
            TriggerSystemTheme(app, PlatformThemeVariant.Light);
            ThemePaletteApplier.CurrentThemeMode.Should().Be(UiPreferences.ThemeModeDark);
            ThemePaletteApplier.CurrentEffectiveIsDark.Should().BeTrue();
            ((ISolidColorBrush)darkLowestRes!).Color.Should().Be(Color.Parse("#14121F"));

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task System_mode_responds_to_system_variant()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch(() =>
        {
            Application app = Application.Current!;


            // 1. Apply System mode starting in Light
            TriggerSystemTheme(app, PlatformThemeVariant.Light);
            ThemePaletteApplier.Apply(UiColorPalettes.DefaultPaletteId, UiPreferences.ThemeModeSystem);
            ThemePaletteApplier.CurrentThemeMode.Should().Be(UiPreferences.ThemeModeSystem);
            ThemePaletteApplier.CurrentEffectiveIsDark.Should().BeFalse();
            app.RequestedThemeVariant.Should().Be(ThemeVariant.Default);

            app.TryFindResource("SurfaceContainerLowestBrush", out object? lowestRes).Should().BeTrue();
            ((ISolidColorBrush)lowestRes!).Color.Should().Be(Color.Parse("#FFFFFF"));
            app.TryFindResource("SurfaceVariantBrush", out object? svRes).Should().BeTrue();
            ((ISolidColorBrush)svRes!).Color.Should().Be(Color.Parse("#EFEDED"));

            // 2. Trigger System change to Dark (Light -> Dark)
            TriggerSystemTheme(app, PlatformThemeVariant.Dark);

            ThemePaletteApplier.CurrentThemeMode.Should().Be(UiPreferences.ThemeModeSystem);
            ThemePaletteApplier.CurrentEffectiveIsDark.Should().BeTrue();
            ((ISolidColorBrush)lowestRes!).Color.Should().Be(Color.Parse("#14121F"));
            ((ISolidColorBrush)svRes!).Color.Should().Be(Color.Parse("#291F43"));

            // 3. Trigger System change back to Light (Dark -> Light)
            TriggerSystemTheme(app, PlatformThemeVariant.Light);

            ThemePaletteApplier.CurrentThemeMode.Should().Be(UiPreferences.ThemeModeSystem);
            ThemePaletteApplier.CurrentEffectiveIsDark.Should().BeFalse();
            ((ISolidColorBrush)lowestRes!).Color.Should().Be(Color.Parse("#FFFFFF"));
            ((ISolidColorBrush)svRes!).Color.Should().Be(Color.Parse("#EFEDED"));

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task FirstRunPage_visual_hierarchy_and_contrast_in_light_and_dark_modes()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<bool>(async () =>
        {
            Application app = Application.Current!;
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel vm = new(settingsPath: settings.Path);
            vm.IsFirstRunVisible = true;

            Window window = new()
            {
                Width = 800,
                Height = 600,
                Content = new FirstRunPage { DataContext = vm }
            };
            window.Show();

            // 1. Light Mode Verification
            ThemePaletteApplier.Apply(UiColorPalettes.DefaultPaletteId, UiPreferences.ThemeModeLight);
            app.ActualThemeVariant.Should().Be(ThemeVariant.Light);
            app.TryFindResource("SurfaceBrush", out object? lightSurfaceRes).Should().BeTrue();
            app.TryFindResource("OnSurfaceBrush", out object? lightOnSurfaceRes).Should().BeTrue();
            Color lightBg = ((ISolidColorBrush)lightSurfaceRes!).Color;
            Color lightFg = ((ISolidColorBrush)lightOnSurfaceRes!).Color;
            (lightBg.R > 200 && lightBg.G > 200 && lightBg.B > 200).Should().BeTrue("light background must be bright");
            (lightFg.R < 50 && lightFg.G < 50 && lightFg.B < 50).Should().BeTrue("light mode text must be dark");
            SavePreview(window, "light");

            // 2. Dark Mode Verification
            ThemePaletteApplier.Apply(UiColorPalettes.DefaultPaletteId, UiPreferences.ThemeModeDark);
            app.ActualThemeVariant.Should().Be(ThemeVariant.Dark);
            app.TryFindResource("SurfaceBrush", out object? darkSurfaceRes).Should().BeTrue();
            app.TryFindResource("OnSurfaceBrush", out object? darkOnSurfaceRes).Should().BeTrue();
            Color darkBg = ((ISolidColorBrush)darkSurfaceRes!).Color;
            Color darkFg = ((ISolidColorBrush)darkOnSurfaceRes!).Color;
            (darkBg.R < 50 && darkBg.G < 50 && darkBg.B < 50).Should().BeTrue("dark background must be dark");
            (darkFg.R > 200 && darkFg.G > 200 && darkFg.B > 200).Should().BeTrue("dark mode text must be bright");
            SavePreview(window, "dark");

            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Slim_scrollbars_and_inverse_status_buttons_keep_their_interaction_states_across_themes()
    {
        await headless.Session.Dispatch(() =>
        {
            ScrollViewer viewer = new()
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                AllowAutoHide = false,
                Content = new Border { Width = 1200, Height = 1200 }
            };
            Button button = new() { Content = "MCP: 运行中", Classes = { "statusBar" } };
            Border status = new() { Child = button };
            Grid layout = new() { RowDefinitions = new RowDefinitions("*,28") };
            layout.Children.Add(viewer);
            Grid.SetRow(status, 1);
            layout.Children.Add(status);
            Window window = new() { Width = 500, Height = 350, Content = layout };
            window.Show();
            try
            {
                foreach (string mode in new[] { UiPreferences.ThemeModeLight, UiPreferences.ThemeModeDark })
                {
                    ThemePaletteApplier.Apply(UiColorPalettes.DefaultPaletteId, mode);
                    status.Background = (IBrush)Application.Current!.FindResource("InverseSurfaceBrush")!;
                    viewer.Offset = default;
                    window.MouseMove(new Point(20, 20));
                    window.UpdateLayout();
                    ScrollBar[] bars = viewer.GetVisualDescendants().OfType<ScrollBar>().ToArray();
                    bars.Should().HaveCount(2);
                    foreach (ScrollBar bar in bars)
                    {
                        bool vertical = bar.Orientation == Orientation.Vertical;
                        (vertical ? bar.Bounds.Width : bar.Bounds.Height).Should().Be(10);
                        ((ISolidColorBrush)bar.Background!).Color.A.Should().Be(0);
                        Track track = bar.GetVisualDescendants().OfType<Track>().Single();
                        track.IsDirectionReversed.Should().Be(vertical);
                        Thumb thumb = track.Thumb!;
                        Point start = thumb.TranslatePoint(new Point(5, 5), window)!.Value;
                        window.MouseMove(start);
                        bar.Background!.Opacity.Should().BeInRange(0.01, 0.2);
                        window.MouseDown(start, MouseButton.Left);
                        Point end = start + (vertical ? new Vector(0, 40) : new Vector(40, 0));
                        window.MouseMove(end);
                        window.MouseUp(end, MouseButton.Left);
                        (vertical ? viewer.Offset.Y : viewer.Offset.X).Should().BeGreaterThan(0);
                        window.MouseMove(new Point(20, 20));
                        ((ISolidColorBrush)bar.Background!).Color.A.Should().Be(0);
                    }

                    ContentPresenter presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First();
                    Point buttonPoint = button.TranslatePoint(new Point(10, 10), window)!.Value;
                    Color inverseText = ((ISolidColorBrush)Application.Current!.FindResource("InverseOnSurfaceBrush")!)
                        .Color;
                    window.MouseMove(buttonPoint);
                    ((ISolidColorBrush)presenter.Background!).Color.Should().Be(inverseText);
                    presenter.Background.Opacity.Should().Be(0.12);
                    window.MouseDown(buttonPoint, MouseButton.Left);
                    presenter.Background.Opacity.Should().Be(0.2);
                    window.MouseUp(buttonPoint, MouseButton.Left);
                    string previewDirectory = TestPaths.FromRepositoryRoot(".tmp", "tests", "scrollbar-previews");
                    Directory.CreateDirectory(previewDirectory);
                    using RenderTargetBitmap bitmap = new(new PixelSize(500, 350));
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(previewDirectory, $"scrollbars-status-{mode}.png"));
                }

                return true;
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Mcp_popup_copy_button_keeps_dark_surface_states_in_both_application_themes()
    {
        await headless.Session.Dispatch(() =>
        {
            Button copy = new() { Classes = { "mcpPopupAction" }, Content = "复制", Width = 30, Height = 30 };
            Window window = new()
            {
                Width = 200, Height = 100,
                Content = new Border { Background = Brush.Parse("#2E2E2E"), Child = copy }
            };
            window.Show();
            try
            {
                foreach (string mode in new[] { UiPreferences.ThemeModeLight, UiPreferences.ThemeModeDark })
                {
                    ThemePaletteApplier.Apply(UiColorPalettes.DefaultPaletteId, mode);
                    window.UpdateLayout();
                    ContentPresenter presenter = copy.GetVisualDescendants().OfType<ContentPresenter>().First();
                    Point point = copy.TranslatePoint(new Point(15, 15), window)!.Value;
                    window.MouseMove(new Point(5, 5));
                    ((ISolidColorBrush)presenter.Background!).Color.Should().Be(Color.Parse("#3E3E3E"));
                    window.MouseMove(point);
                    ((ISolidColorBrush)presenter.Background!).Color.Should().Be(Color.Parse("#505050"));
                    ((ISolidColorBrush)presenter.Foreground!).Color.Should().Be(Colors.White);
                    window.MouseDown(point, MouseButton.Left);
                    ((ISolidColorBrush)presenter.Background!).Color.Should().Be(Color.Parse("#626262"));
                    window.MouseUp(point, MouseButton.Left);
                    copy.IsEnabled = false;
                    presenter.Opacity.Should().Be(0.4);
                    ((ISolidColorBrush)presenter.Background!).Color.Should().Be(Color.Parse("#3E3E3E"));
                    copy.IsEnabled = true;
                }

                return true;
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static void SavePreview(Window window, string mode)
    {
        window.UpdateLayout();
        string directory = TestPaths.FromRepositoryRoot(".tmp", "tests", "theme-mode-previews");
        Directory.CreateDirectory(directory);
        using RenderTargetBitmap bitmap = new(new PixelSize(800, 600));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(directory, $"first-run-{mode}.png"));
    }

    private static void TriggerSystemTheme(Application app, PlatformThemeVariant variant)
    {
        // Avalonia 12 re-reads platform settings when receiving a notification, rather than
        // trusting its event arguments. Scope a runtime proxy because its reference assembly
        // deliberately prevents applications from implementing the private platform interface.
        Type locatorType = typeof(AvaloniaObject).Assembly.GetType("Avalonia.AvaloniaLocator")!;
        using IDisposable scope = (IDisposable)locatorType.GetMethod("EnterScope")!.Invoke(null, null)!;
        object locator = locatorType.GetProperty("CurrentMutable")!.GetValue(null)!;
        IPlatformSettings settings = DispatchProxy.Create<IPlatformSettings, ThemeSettingsProxy>();
        ((ThemeSettingsProxy)(object)settings).Colors = new PlatformColorValues { ThemeVariant = variant };
        object binding = locatorType.GetMethod("Bind")!.MakeGenericMethod(typeof(IPlatformSettings))
            .Invoke(locator, null)!;
        binding.GetType().GetMethod("ToConstant")!.MakeGenericMethod(typeof(IPlatformSettings))
            .Invoke(binding, new object[] { settings });
        MethodInfo? handler = typeof(Application).GetMethod("OnColorValuesChanged",
            BindingFlags.NonPublic | BindingFlags.Instance);
        handler.Should().NotBeNull("the platform notification handler must exist for this integration test");
        handler!.Invoke(app, new object?[] { settings, ((ThemeSettingsProxy)(object)settings).Colors });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private class ThemeSettingsProxy : DispatchProxy
    {
        public PlatformColorValues Colors { get; set; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name == "GetColorValues"
                ? Colors
                : throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
