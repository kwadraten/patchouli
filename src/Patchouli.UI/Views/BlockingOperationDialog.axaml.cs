using Avalonia.Controls;
using System.Collections.Specialized;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Views;

public partial class BlockingOperationDialog : Window
{
    public BlockingOperationDialog()
    {
        InitializeComponent();
        
        DataContextChanged += (s, e) =>
        {
            if (DataContext is BlockingOperationDialogViewModel vm)
            {
                vm.Logs.CollectionChanged += Logs_CollectionChanged;
            }
        };
    }

    private void Logs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            var scrollViewer = this.FindControl<ScrollViewer>("LogScrollViewer");
            scrollViewer?.ScrollToEnd();
        }
    }
}
