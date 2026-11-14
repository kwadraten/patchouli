using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Patchouli.UI.Services;

/// <summary>Visible, non-activating feedback for background protocol activation. Windows flashes
/// the taskbar button; other platforms flash the window title and the tray tooltip so a minimized
/// or tray-resident window never silently absorbs an external link.</summary>
internal static partial class WindowAttentionService
{
    private static readonly TimeSpan FeedbackDuration = TimeSpan.FromSeconds(4);

    internal static Action<Window>? TestHook { get; set; }

    internal static void RequestAttention(Window window)
    {
        if (TestHook is { } hook)
        {
            hook(window);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            FlashTaskbar(window);
        }

        FlashTitle(window);
        App.ShowTrayAttention("Patchouli：已打开文献虚拟链接");
    }

    private static void FlashTitle(Window window)
    {
        string originalTitle = window.Title ?? "";
        const string suffix = " • 已打开文献虚拟链接";
        if (originalTitle.EndsWith(suffix, StringComparison.Ordinal))
        {
            return;
        }

        window.Title = originalTitle + suffix;
        DispatcherTimer timer = new() { Interval = FeedbackDuration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (window.Title == originalTitle + suffix)
            {
                window.Title = originalTitle;
            }
        };
        timer.Start();
    }

    private static void FlashTaskbar(Window window)
    {
        try
        {
            IntPtr handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            FlashWindowInfo info = new()
            {
                Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
                WindowHandle = handle,
                Flags = FlashWindowAll | FlashWindowTimers,
                Count = 3,
                Timeout = 0
            };
            _ = FlashWindowEx(ref info);
        }
        catch (Exception)
        {
            // Attention feedback is best-effort and never blocks navigation.
        }
    }

    private const uint FlashWindowAll = 0x00000003;
    private const uint FlashWindowTimers = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FlashWindowInfo info);
}
