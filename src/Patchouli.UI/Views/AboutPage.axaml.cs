using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Patchouli.UI.Views;

public partial class AboutPage : UserControl
{
    public AboutPage()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
