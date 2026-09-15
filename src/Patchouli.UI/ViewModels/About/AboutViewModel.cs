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
            new("CommunityToolkit.Mvvm", "MIT", "https://github.com/CommunityToolkit/dotnet", openUrlCommand),
            new("System.Reactive (Rx.NET)", "MIT", "https://github.com/dotnet/reactive", openUrlCommand),
            new("Lucide", "ISC", "https://github.com/lucide-icons/lucide", openUrlCommand),
            new("Dapper", "Apache-2.0", "https://github.com/DapperLib/Dapper", openUrlCommand),
            new("Microsoft.Data.Sqlite", "MIT", "https://github.com/dotnet/efcore", openUrlCommand),
            new("SQLitePCLRaw", "Apache-2.0", "https://github.com/ericsink/SQLitePCL.raw", openUrlCommand),
            new("SQLite", "Public Domain", "https://sqlite.org/", openUrlCommand),
            new("Blake3", "CC0 / Apache-2.0", "https://github.com/BLAKE3-team/BLAKE3", openUrlCommand),
            new("SkiaSharp", "MIT", "https://github.com/mono/SkiaSharp", openUrlCommand),
            new("PDFiumCore / PDFium", "Apache-2.0 / BSD-3-Clause",
                "https://github.com/Dtronix/PDFiumCore", openUrlCommand),
            new("Microsoft.ML.OnnxRuntime", "MIT", "https://github.com/microsoft/onnxruntime", openUrlCommand),
            new("Fsharp.Citeproc", "MIT", "https://github.com/kwadraten/Fsharp.Citeproc", openUrlCommand),
            new("Markdig", "BSD-2-Clause", "https://github.com/xoofx/markdig", openUrlCommand),
            new("OpenccNetLib", "MIT", "https://github.com/laisuk/OpenccNet", openUrlCommand),
            new("Corvus.Toon.SystemTextJson", "Apache-2.0", "https://github.com/corvus-dotnet/Corvus.JsonSchema",
                openUrlCommand),
            new("typst/biblatex", "MIT / Apache-2.0", "https://github.com/typst/biblatex",
                openUrlCommand),
            new("NDL Koten OCR Lite", "CC-BY-4.0", "https://github.com/ndl-lab/ndlkotenocr-lite",
                openUrlCommand),
            new("NDLOCR-Lite", "CC-BY-4.0", "https://github.com/ndl-lab/ndlocr-lite",
                openUrlCommand),
            new("RapidOCR", "Apache-2.0", "https://github.com/RapidAI/RapidOCR",
                openUrlCommand),
            new("RapidOCR Models (PP-OCRv6, ModelScope)", "Apache-2.0",
                "https://www.modelscope.cn/models/RapidAI/RapidOCR", openUrlCommand),
            new("ProDataGrid", "MIT", "https://github.com/wieslawsoltes/ProDataGrid", openUrlCommand),
            new("AvaloniaRichEditor", "MIT",
                "https://github.com/centwon/AvaloniaRichEditor", openUrlCommand)
        };
    }
}
