using Avalonia;
using Avalonia.Headless;
using Patchouli.UI;

namespace Patchouli.Performance;

public static class HeadlessTestApp
{
    public static AppBuilder BuildAvaloniaApp()
    {
        // GlyphRun caches its first render interface for the process; never mix stub and Skia backends.
        return AppBuilder.Configure<App>().UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
