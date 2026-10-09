using Avalonia.Controls;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Views;

public sealed partial class TagNamePromptDialog : Window
{
    public TagNamePromptDialog()
    {
        InitializeComponent();
        DialogCloseBinding.Bind<TagNamePromptDialogViewModel, string?>(
            this, static (vm, close) => vm.RequestClose = close);
    }
}
