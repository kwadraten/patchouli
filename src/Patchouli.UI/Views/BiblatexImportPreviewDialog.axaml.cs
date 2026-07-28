using Avalonia.Controls;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Views;

public partial class BiblatexImportPreviewDialog : Window
{
    public BiblatexImportPreviewDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is BiblatexImportPreviewDialogViewModel vm)
            {
                vm.RequestClose = result => Close(result);
            }
        };
    }
}
