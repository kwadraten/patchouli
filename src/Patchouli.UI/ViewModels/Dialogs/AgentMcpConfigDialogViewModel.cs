using Patchouli.UI.ViewModels;

namespace Patchouli.UI.ViewModels.Dialogs;

/// <summary>
/// Read-only confirmation surface for writing a generated Patchouli MCP connection file. It shows the
/// exact content that will be written (including comments when the format supports them) and the
/// permission/revocation notes. Closing the dialog without confirming returns false.
/// </summary>
public sealed class AgentMcpConfigDialogViewModel : ViewModelBase
{
    public AgentMcpConfigDialogViewModel(
        string agentName,
        string fileName,
        string preview,
        IReadOnlyList<string> permissionNotes,
        string environmentNote)
    {
        Title = $"写入 Patchouli MCP 配置 — {agentName}";
        FileName = fileName;
        Preview = preview;
        PermissionNotes = permissionNotes;
        EnvironmentNote = environmentNote;
        ConfirmCommand = new RelayCommand(_ => RequestClose?.Invoke(true));
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(false));
    }

    public string Title { get; }
    public string FileName { get; }
    public string Preview { get; }
    public IReadOnlyList<string> PermissionNotes { get; }
    public string EnvironmentNote { get; }

    public RelayCommand ConfirmCommand { get; }
    public RelayCommand CancelCommand { get; }
    public Action<bool>? RequestClose { get; set; }
}
