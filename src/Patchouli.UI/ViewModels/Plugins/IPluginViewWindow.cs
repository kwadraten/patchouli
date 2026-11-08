using Avalonia.Controls;

namespace Patchouli.UI.ViewModels;

/// <summary>
/// Non-modal plugin-view window abstraction owned by <see cref="MainWindowViewModel"/>'s
/// window-placement registry. The seam lets headless routing tests substitute a fake
/// (they never construct <see cref="Views.PluginViewWindow"/> or any NativeWebView).
/// </summary>
internal interface IPluginViewWindow
{
    PluginViewSession Session { get; }

    /// <summary>Shows the window non-modally, owned by <paramref name="owner"/> when provided.</summary>
    void Show(Window? owner);

    void Activate();

    void Close();

    /// <summary>Raised once after the window closes; the registry removes and releases it.</summary>
    event EventHandler? Closed;
}
