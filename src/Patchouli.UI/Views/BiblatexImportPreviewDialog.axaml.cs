using Avalonia.Controls;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Views;

public partial class BiblatexImportPreviewDialog : Window
{
    public BiblatexImportPreviewDialog()
    {
        InitializeComponent();
        DialogCloseBinding.Bind<BiblatexImportPreviewDialogViewModel, object?>(
            this, static (vm, close) => vm.RequestClose = close);
    }
}
