using Avalonia.Controls;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Views;

public sealed partial class TagNamePromptDialog : Window
{
    public TagNamePromptDialog()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is TagNamePromptDialogViewModel vm)
        {
            vm.RequestClose = result =>
            {
                Close(result);
                vm.RequestClose = null;
            };
        }
    }
}
