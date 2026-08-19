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
using Patchouli.Infrastructure.Snapshots;
using Patchouli.Infrastructure.Workflows;
using Patchouli.Mcp;
using Patchouli.McpServer;
using Patchouli.Ocr;
using Patchouli.Core.Search;

namespace Patchouli.UI.ViewModels;

public sealed record SidebarFileSearchRootViewModel(
    string RootPath,
    bool IsAvailable,
    DateTimeOffset UpdatedAt,
    int FileCount)
{
    public string AvailabilityText => IsAvailable ? "可用" : "离线";
    public string UpdatedAtText => UpdatedAt.ToLocalTime().ToString("g");
    public string FileCountText => $"{FileCount} 个文件";
}
