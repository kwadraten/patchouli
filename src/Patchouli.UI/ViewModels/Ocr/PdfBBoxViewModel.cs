using Avalonia.Media;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.UI.ViewModels;

public sealed class PdfBBoxViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private bool _isSelected;
    private string? _text;

    public PdfBBoxViewModel(
        MainWindowViewModel main,
        PdfWorkspaceViewModel workspace,
        DocumentBox box,
        double imageWidth,
        double imageHeight,
        bool isStaging)
    {
        _main = main;
        Workspace = workspace;
        BoxId = box.BoxId;
        BoxType = box.BoxType;
        Text = PayloadText(box.Payload);
        Left = box.BBox.X * imageWidth;
        Top = box.BBox.Y * imageHeight;
        Width = box.BBox.Width * imageWidth;
        Height = box.BBox.Height * imageHeight;
        BoxColor = ColorFor(box.BoxType);
        IsStaging = isStaging;
        SaveTextCommand = new AsyncCommand(SaveTextAsync);
        IgnoreCommand = new AsyncCommand(IgnoreAsync);
    }

    public PdfWorkspaceViewModel Workspace { get; }
    public DocumentBoxId BoxId { get; }
    public double Left { get; }
    public double Top { get; }
    public double Width { get; }
    public double Height { get; }
    public string BoxType { get; }
    public IBrush BoxColor { get; }
    public bool IsStaging { get; }
    public bool IsLogicalPage => BoxType == DocumentBoxType.LogicalPage;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            Raise();
        }
    }

    public string? Text
    {
        get => _text;
        set
        {
            if (_text == value)
            {
                return;
            }

            _text = value;
            Raise();
        }
    }

    public AsyncCommand SaveTextCommand { get; }
    public AsyncCommand IgnoreCommand { get; }

    private async Task SaveTextAsync()
    {
        PageEditSessionId? sessionId = Workspace.EditSessionId;
        if (sessionId is null)
        {
            return;
        }

        if (IsLogicalPage)
        {
            return;
        }

        Result result = await (await _main.ServicesAsync()).DocumentTreeEditor.UpdateLeafAsync(
            sessionId.Value,
            new UpdateLeafCommand(BoxId, BoxType, new TextBoxPayload(Text ?? string.Empty)));
        if (result.IsFailure)
        {
            Workspace.Status = $"更新文本失败: {result.ErrorMessage}";
        }
        else
        {
            Workspace.Status = "文本已写入页面草稿。";
            await Workspace.RefreshPreviewAsync();
        }
    }

    private async Task IgnoreAsync()
    {
        if (BoxType == DocumentBoxType.LogicalPage)
        {
            Workspace.Status = "logical_page 不能作为普通内容抑制；请移动或删除其子 Box。";
            return;
        }

        PageEditSessionId? sessionId = Workspace.EditSessionId;
        if (sessionId is null)
        {
            return;
        }

        Result result = await (await _main.ServicesAsync()).DocumentTreeEditor.SetSuppressedAsync(
            sessionId.Value, BoxId, true);
        if (result.IsSuccess)
        {
            Workspace.RemoveBBox(this);
        }
        else
        {
            Workspace.Status = $"抑制 Box 失败: {result.ErrorMessage}";
        }
    }

    private static string? PayloadText(DocumentBoxPayload? payload)
    {
        return payload switch
        {
            TextBoxPayload value => value.Markdown,
            ListBoxPayload value => value.Markdown,
            TableBoxPayload value => value.Markdown,
            EquationBoxPayload value => value.Latex,
            CodeBoxPayload value => value.Code,
            MediaBoxPayload value => value.Description,
            _ => null
        };
    }

    private static IBrush ColorFor(string boxType)
    {
        return boxType switch
        {
            DocumentBoxType.Title => Brushes.Blue,
            DocumentBoxType.Text => Brushes.Green,
            DocumentBoxType.Table => Brushes.Orange,
            DocumentBoxType.Image => Brushes.Red,
            DocumentBoxType.Equation => Brushes.Purple,
            DocumentBoxType.LogicalPage => Brushes.Teal,
            _ => Brushes.Gray
        };
    }
}
