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

public class AboutViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _parent;
    public string VersionInfo => _parent.VersionInfo;

    public string LicenseText { get; }
    public ObservableCollection<ThirdPartyLibrary> ThirdPartyLibraries { get; }
    public ICommand OpenUrlCommand { get; }

    public AboutViewModel(MainWindowViewModel parent)
    {
        _parent = parent;
        try
        {
            using Stream? stream = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("Patchouli.UI.LICENSE");
            if (stream != null)
            {
                using StreamReader reader = new(stream);
                LicenseText = reader.ReadToEnd();
            }
            else
            {
                LicenseText = "错误：未找到内嵌许可证资源。";
            }
        }
        catch (Exception ex)
        {
            LicenseText = "加载许可证失败：" + ex.Message;
        }

        RelayCommand openUrlCommand = new(url =>
        {
            if (url is string s && !string.IsNullOrWhiteSpace(s))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = s,
                    UseShellExecute = true
                });
            }
        });
        OpenUrlCommand = openUrlCommand;
        ThirdPartyLibraries = new ObservableCollection<ThirdPartyLibrary>
        {
            new("Avalonia", "MIT", "https://github.com/AvaloniaUI/Avalonia", openUrlCommand),
            new("Dapper", "Apache 2.0", "https://github.com/DapperLib/Dapper", openUrlCommand),
            new("Microsoft.Data.Sqlite", "MIT", "https://github.com/dotnet/efcore", openUrlCommand),
            new("Blake3", "CC0 / Apache 2.0", "https://github.com/BLAKE3-team/BLAKE3", openUrlCommand),
            new("PDFiumCore / PDFium", "Apache-2.0 / BSD-3-Clause",
                "https://github.com/Dtronix/PDFiumCore", openUrlCommand),
            new("Hayagriva", "Apache 2.0 / MIT", "https://github.com/typst/hayagriva", openUrlCommand)
        };
    }
}
