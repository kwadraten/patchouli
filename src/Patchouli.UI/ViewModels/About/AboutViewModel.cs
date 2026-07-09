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
    public System.Windows.Input.ICommand OpenUrlCommand { get; }

    public AboutViewModel(MainWindowViewModel parent)
    {
        _parent = parent;
        try
        {
            using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Patchouli.UI.LICENSE");
            if (stream != null)
            {
                using var reader = new System.IO.StreamReader(stream);
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

        ThirdPartyLibraries = new()
        {
            new("Avalonia", "MIT", "https://github.com/AvaloniaUI/Avalonia"),
            new("Dapper", "Apache 2.0", "https://github.com/DapperLib/Dapper"),
            new("Microsoft.Data.Sqlite", "MIT", "https://github.com/dotnet/efcore"),
            new("Blake3", "CC0 / Apache 2.0", "https://github.com/BLAKE3-team/BLAKE3"),
            new("MuPDF.NET", "AGPL v3.0", "https://github.com/ArtifexSoftware/mupdf.net"),
            new("Hayagriva", "Apache 2.0 / MIT", "https://github.com/typst/hayagriva")
        };

        OpenUrlCommand = new RelayCommand(url => 
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
    }
}
