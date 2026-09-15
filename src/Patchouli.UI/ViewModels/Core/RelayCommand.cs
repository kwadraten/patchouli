using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using ToolkitRelayCommand = CommunityToolkit.Mvvm.Input.RelayCommand<object?>;

namespace Patchouli.UI.ViewModels;

public sealed class RelayCommand : IRelayCommand
{
    private readonly ToolkitRelayCommand _inner;

    public RelayCommand(Action<object?> execute)
    {
        _inner = new ToolkitRelayCommand(execute);
    }

    public event EventHandler? CanExecuteChanged
    {
        add => _inner.CanExecuteChanged += value;
        remove => _inner.CanExecuteChanged -= value;
    }

    public bool CanExecute(object? parameter)
    {
        return _inner.CanExecute(parameter);
    }

    public void Execute(object? parameter)
    {
        _inner.Execute(parameter);
    }

    public void NotifyCanExecuteChanged()
    {
        _inner.NotifyCanExecuteChanged();
    }
}
