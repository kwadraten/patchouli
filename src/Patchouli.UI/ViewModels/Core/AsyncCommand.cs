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
using Patchouli.Evidence;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.Infrastructure.Workflows;
using Patchouli.Mcp;
using Patchouli.McpServer;
using Patchouli.Ocr;
using Patchouli.Search;

namespace Patchouli.UI.ViewModels;

public sealed class AsyncCommand : System.Windows.Input.ICommand
{
    private readonly Func<Task> _run; public AsyncCommand(Func<Task> run) => _run = run;
    public event EventHandler? CanExecuteChanged { add { } remove { } } public bool CanExecute(object? parameter) => true;
    public async void Execute(object? parameter) => await _run();
    public Task ExecuteAsync() => _run();
}

