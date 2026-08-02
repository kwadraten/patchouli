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

namespace Patchouli.UI.ViewModels;

public sealed class McpPreviewViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    public string Query { get; set; } = "";
    public string Output { get; set; } = "";
    public string Safety { get; set; } = "";
    public string SpecificPath { get; set; } = "";
    public string SpecificSecret { get; set; } = "";
    public AsyncCommand SearchCommand { get; }
    public AsyncCommand SafetyCommand { get; }

    public McpPreviewViewModel(MainWindowViewModel m)
    {
        _main = m;
        SearchCommand = new AsyncCommand(async () =>
        {
            Result<McpSearchLibraryResponse> r =
                await (await _main.ServicesAsync()).Mcp.SearchLibraryAsync(new McpSearchLibraryRequest(Query));
            Output = r.IsSuccess
                ? JsonSerializer.Serialize(r.Value, new JsonSerializerOptions { WriteIndented = true })
                : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            Raise(nameof(Output));
        });
        SafetyCommand = new AsyncCommand(() =>
        {
            List<string> tokens = new[]
                { "original_path", "resolved_path", "file://", "/Users/", "model_path", "cache" }.ToList();
            if (!string.IsNullOrWhiteSpace(SpecificPath))
            {
                tokens.Add(SpecificPath);
            }

            if (!string.IsNullOrWhiteSpace(SpecificSecret))
            {
                tokens.Add(SpecificSecret);
            }

            string? hit = tokens.FirstOrDefault(x => Output.Contains(x, StringComparison.Ordinal));
            Safety = hit is null
                ? "No obvious local path or secret exposure detected."
                : $"Warning: output contains sensitive token: {hit}";
            Raise(nameof(Safety));
            return Task.CompletedTask;
        });
    }
}
