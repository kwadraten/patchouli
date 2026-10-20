using Avalonia.Controls;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.Views;

public sealed partial class OcrQueuePage : UserControl
{
    public OcrQueuePage()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => (DataContext as OcrQueueViewModel)?.StartObserving();
        DetachedFromVisualTree += (_, _) => (DataContext as OcrQueueViewModel)?.StopObserving();
    }
}
