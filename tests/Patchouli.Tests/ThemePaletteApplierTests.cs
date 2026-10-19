using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using FluentAssertions;
using Patchouli.Performance;
using Patchouli.UI;
using Patchouli.UI.Themes;
using Patchouli.UI.ViewModels;
using Patchouli.UI.Views;
using Xunit;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class ThemePaletteApplierTests : IDisposable
{
    public void Dispose()
    {
        ThemePaletteApplier.DetachWatcher();
    }

    [Fact]
    public async Task Watcher_lifecycle_attaches_and_detaches_cleanly()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
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
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
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
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
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
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
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
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
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
