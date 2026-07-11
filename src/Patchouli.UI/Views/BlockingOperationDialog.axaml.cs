using Avalonia.Controls;
using Avalonia.Threading;
using System.Collections.Specialized;
using System.ComponentModel;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Views;

public partial class BlockingOperationDialog : Window
{
    private const double CompactHeight = 260;
    private const double DetailsHeight = 430;
    private BlockingOperationDialogViewModel? _viewModel;

    public BlockingOperationDialog()
    {
        InitializeComponent();
        
        DataContextChanged += (_, _) =>
        {
            DetachViewModel();
            if (DataContext is BlockingOperationDialogViewModel vm)
            {
                _viewModel = vm;
                vm.RequestClose = result => Close(result);
                vm.Logs.CollectionChanged += Logs_CollectionChanged;
                vm.PropertyChanged += ViewModel_PropertyChanged;
                Height = vm.IsDetailsVisible ? DetailsHeight : CompactHeight;
            }
        };
        Closing += OnClosing;
        Closed += (_, _) => DetachViewModel();
    }

    private void Logs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var textBox = this.FindControl<TextBox>("LogTextBox");
                if (textBox is not null)
                    textBox.CaretIndex = textBox.Text?.Length ?? 0;
            });
        }
    }

    private void DetachViewModel()
    {
        if (_viewModel is null) return;
        _viewModel.Logs.CollectionChanged -= Logs_CollectionChanged;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.RequestClose = null;
        _viewModel = null;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_viewModel?.IsRunning == true)
            e.Cancel = true;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BlockingOperationDialogViewModel.IsDetailsVisible) &&
            sender is BlockingOperationDialogViewModel vm)
        {
            Height = vm.IsDetailsVisible ? DetailsHeight : CompactHeight;
        }
    }
}
