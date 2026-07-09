using System;
using System.Windows.Input;

namespace Patchouli.UI.ViewModels.Core;

public enum CommandDangerLevel
{
    Normal,
    Warning,
    Destructive
}

public class UiCommandDescriptor : ViewModelBase, ICommand
{
    private readonly ICommand _innerCommand;
    private bool _enabled;
    private string _disabledReason = string.Empty;

    public UiCommandDescriptor(string id, string label, ICommand innerCommand)
    {
        Id = id;
        Label = label;
        _innerCommand = innerCommand ?? throw new ArgumentNullException(nameof(innerCommand));
        
        _innerCommand.CanExecuteChanged += (s, e) => CanExecuteChanged?.Invoke(this, e);
    }

    public string Id { get; }
    public string Label { get; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled != value)
            {
                _enabled = value;
                Raise(nameof(Enabled));
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string DisabledReason
    {
        get => _disabledReason;
        set
        {
            if (_disabledReason != value)
            {
                _disabledReason = value;
                Raise(nameof(DisabledReason));
            }
        }
    }

    public CommandDangerLevel DangerLevel { get; set; } = CommandDangerLevel.Normal;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        return Enabled && _innerCommand.CanExecute(parameter);
    }

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            _innerCommand.Execute(parameter);
        }
    }

    public Task ExecuteAsync()
    {
        if (CanExecute(null) && _innerCommand is Patchouli.UI.ViewModels.AsyncCommand ac)
        {
            return ac.ExecuteAsync();
        }
        return Task.CompletedTask;
    }
}
