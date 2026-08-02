using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Media;
using Dapper;
using Patchouli.Core.Credentials;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Evidence;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.Infrastructure.Workflows;
using Patchouli.Mcp;
using Patchouli.McpServer;
using Patchouli.Ocr;
using Patchouli.Core.Search;
using Patchouli.UI.Diagnostics;

namespace Patchouli.UI.ViewModels;

public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _run;
    private readonly string _operation;
    private readonly IUnexpectedExceptionSink _unexpectedExceptions;

    public AsyncCommand(
        Func<Task> run,
        IUnexpectedExceptionSink? unexpectedExceptions = null,
        [CallerArgumentExpression(nameof(run))]
        string? operation = null)
    {
        _run = run;
        _operation = string.IsNullOrWhiteSpace(operation) ? "unknown-command" : operation;
        _unexpectedExceptions = unexpectedExceptions ?? UnexpectedExceptions.Sink;
    }

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter)
    {
        return true;
    }

    public async void Execute(object? parameter)
    {
        try
        {
            await _run();
        }
        catch (OperationCanceledException exception) when (exception.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _unexpectedExceptions.Report(exception, "ui-command", _operation);
        }
    }

    public Task ExecuteAsync()
    {
        return _run();
    }
}
