using Avalonia;
using Avalonia.Media;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;

namespace Patchouli.UI.ViewModels;

public sealed class PdfBBoxViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private bool _isSelected;
    private string? _text;
    private double _normalizedX;
    private double _normalizedY;
    private double _normalizedWidth;
    private double _normalizedHeight;
    private readonly double _imageWidth;
    private readonly double _imageHeight;
    private string _boxType;
    private int? _headingLevel;
    private string? _codeLanguage;
    private string? _assetId;
    private bool _isMergeSelected;
    private bool _isEditorOpen;
    private double _editorOffsetX;
    private double _editorOffsetY;

    public PdfBBoxViewModel(
        MainWindowViewModel main,
        PdfWorkspaceViewModel workspace,
        DocumentBox box,
        double imageWidth,
        double imageHeight,
        bool isStaging,
        int readingOrder = 0,
        int depth = 0)
    {
        _main = main;
        Workspace = workspace;
        BoxId = box.BoxId;
        ParentBoxId = box.ParentBoxId;
        NextSiblingBoxId = box.NextSiblingBoxId;
        _boxType = box.BoxType;
        Payload = box.Payload;
        _headingLevel = box.HeadingLevel;
        _codeLanguage = box.CodeLanguage;
        Text = PayloadText(box.Payload);
        _assetId = (box.Payload as MediaBoxPayload)?.AssetId;
        IsSuppressed = box.Suppressed;
        _imageWidth = imageWidth;
        _imageHeight = imageHeight;
        _normalizedX = box.BBox.X;
        _normalizedY = box.BBox.Y;
        _normalizedWidth = box.BBox.Width;
        _normalizedHeight = box.BBox.Height;
        ReadingOrder = readingOrder;
        Depth = depth;
        IsStaging = isStaging;
        SaveTextCommand = new AsyncCommand(SaveTextAsync);
        IgnoreCommand = new AsyncCommand(IgnoreAsync);
        ToggleSuppressedCommand = new AsyncCommand(ToggleSuppressedAsync);
        DeleteCommand = new AsyncCommand(DeleteAsync);
        SaveBBoxCommand = new AsyncCommand(SaveBBoxAsync);
    }

    public PdfWorkspaceViewModel Workspace { get; }
    public DocumentBoxId BoxId { get; }
    public DocumentBoxPayload? Payload { get; }
    public DocumentBoxId? ParentBoxId { get; }
    public DocumentBoxId? NextSiblingBoxId { get; }
    public double Left => NormalizedX * _imageWidth;
    public double Top => NormalizedY * _imageHeight;
    public double Width => NormalizedWidth * _imageWidth;
    public double Height => NormalizedHeight * _imageHeight;

    public string BoxType
    {
        get => _boxType;
        set
        {
            if (_boxType == value)
            {
                return;
            }

            _boxType = value;
            Raise();
            Raise(nameof(BoxColor));
            Raise(nameof(IsLogicalPage));
            Raise(nameof(IsMedia));
            Raise(nameof(IsTitle));
            Raise(nameof(IsCode));
        }
    }

    public int? HeadingLevel
    {
        get => _headingLevel;
        set
        {
            if (_headingLevel != value)
            {
                _headingLevel = value;
                Raise();
            }
        }
    }

    public string? CodeLanguage
    {
        get => _codeLanguage;
        set
        {
            if (_codeLanguage != value)
            {
                _codeLanguage = value;
                Raise();
            }
        }
    }

    public IBrush BoxColor => ColorFor(BoxType);
    public IBrush VisualBoxColor => IsSuppressed ? Brushes.Gray : BoxColor;
    public bool IsStaging { get; }

    public string? AssetId
    {
        get => _assetId;
        set
        {
            if (_assetId != value)
            {
                _assetId = value;
                Raise();
            }
        }
    }

    public bool IsSuppressed { get; }
    public int ReadingOrder { get; }
    public int Depth { get; }
    public Thickness TreeMargin => new(Depth * 20, 0, 0, 12);
    public string Summary => string.IsNullOrWhiteSpace(Text) ? "（无文本内容）" : Text.ReplaceLineEndings(" ").Trim();
    public bool IsLogicalPage => BoxType == DocumentBoxType.LogicalPage;
    public bool IsMedia => BoxType is DocumentBoxType.Image or DocumentBoxType.Chart;
    public bool IsTitle => BoxType == DocumentBoxType.Title;
    public bool IsCode => BoxType is DocumentBoxType.Code or DocumentBoxType.Algorithm;

    public bool IsMergeSelected
    {
        get => _isMergeSelected;
        set
        {
            if (_isMergeSelected != value)
            {
                _isMergeSelected = value;
                Raise();
            }
        }
    }

    public bool IsEditorOpen
    {
        get => _isEditorOpen;
        set
        {
            if (_isEditorOpen != value)
            {
                _isEditorOpen = value;
                Raise();
            }
        }
    }

    public double EditorOffsetX
    {
        get => _editorOffsetX;
        set
        {
            if (!_editorOffsetX.Equals(value))
            {
                _editorOffsetX = value;
                Raise();
            }
        }
    }

    public double EditorOffsetY
    {
        get => _editorOffsetY;
        set
        {
            if (!_editorOffsetY.Equals(value))
            {
                _editorOffsetY = value;
                Raise();
            }
        }
    }

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
    public AsyncCommand ToggleSuppressedCommand { get; }
    public AsyncCommand DeleteCommand { get; }
    public AsyncCommand SaveBBoxCommand { get; }

    public double NormalizedX
    {
        get => _normalizedX;
        set => SetBBoxValue(ref _normalizedX, value, nameof(Left));
    }

    public double NormalizedY
    {
        get => _normalizedY;
        set => SetBBoxValue(ref _normalizedY, value, nameof(Top));
    }

    public double NormalizedWidth
    {
        get => _normalizedWidth;
        set => SetBBoxValue(ref _normalizedWidth, value, nameof(Width));
    }

    public double NormalizedHeight
    {
        get => _normalizedHeight;
        set => SetBBoxValue(ref _normalizedHeight, value, nameof(Height));
    }

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
            new UpdateLeafCommand(BoxId, BoxType, PayloadForText(Text), HeadingLevel, CodeLanguage));
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

    private DocumentBoxPayload PayloadForText(string? text)
    {
        return BoxType switch
        {
            DocumentBoxType.Code => new CodeBoxPayload(text ?? string.Empty),
            DocumentBoxType.Equation => new EquationBoxPayload(text ?? string.Empty),
            DocumentBoxType.List => new ListBoxPayload(text ?? string.Empty),
            DocumentBoxType.Table => new TableBoxPayload(text ?? string.Empty),
            DocumentBoxType.Image or DocumentBoxType.Chart =>
                new MediaBoxPayload(AssetId, string.IsNullOrWhiteSpace(text) ? null : text),
            _ => new TextBoxPayload(text ?? string.Empty)
        };
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
            await Workspace.RefreshBoxesAsync();
        }
        else
        {
            Workspace.Status = $"抑制 Box 失败: {result.ErrorMessage}";
        }
    }

    private async Task DeleteAsync()
    {
        PageEditSessionId? sessionId = Workspace.EditSessionId;
        if (sessionId is null)
        {
            return;
        }

        Result result = await (await _main.ServicesAsync()).DocumentTreeEditor.DeleteBoxAsync(sessionId.Value, BoxId);
        if (result.IsFailure)
        {
            Workspace.Status = $"删除 Box 失败: {result.ErrorMessage}";
            return;
        }

        await Workspace.RefreshBoxesAsync();
        Workspace.Status = "Box 已从页面草稿删除；提交后才会进入新 revision。";
    }

    private async Task ToggleSuppressedAsync()
    {
        PageEditSessionId? sessionId = Workspace.EditSessionId;
        if (sessionId is null || IsLogicalPage)
        {
            return;
        }

        Result result = await (await _main.ServicesAsync()).DocumentTreeEditor.SetSuppressedAsync(
            sessionId.Value, BoxId, !IsSuppressed);
        if (result.IsFailure)
        {
            Workspace.Status = $"切换文档流状态失败: {result.ErrorMessage}";
            return;
        }

        await Workspace.RefreshBoxesAsync();
        Workspace.Status = IsSuppressed ? "Box 已重新纳入文档流。" : "Box 已从文档流排除。";
    }

    internal async Task SaveBBoxAsync()
    {
        PageEditSessionId? sessionId = Workspace.EditSessionId;
        if (sessionId is null)
        {
            return;
        }

        NormalizedBBox bbox = new(NormalizedX, NormalizedY, NormalizedWidth, NormalizedHeight);
        Result valid = bbox.Validate();
        if (valid.IsFailure)
        {
            Workspace.Status = $"bbox 无效: {valid.ErrorMessage}";
            return;
        }

        Result result = await (await _main.ServicesAsync()).DocumentTreeEditor.UpdateBBoxAsync(
            sessionId.Value, BoxId, bbox);
        if (result.IsFailure)
        {
            Workspace.Status = $"更新 bbox 失败: {result.ErrorMessage}";
            return;
        }

        await Workspace.RefreshBoxesAsync();
        Workspace.Status = "bbox 已写入页面草稿。";
    }

    internal void SetCanvasBBox(double left, double top, double width, double height)
    {
        NormalizedX = Math.Clamp(left / _imageWidth, 0, 1);
        NormalizedY = Math.Clamp(top / _imageHeight, 0, 1);
        NormalizedWidth = Math.Clamp(width / _imageWidth, 0.0001, 1 - NormalizedX);
        NormalizedHeight = Math.Clamp(height / _imageHeight, 0.0001, 1 - NormalizedY);
    }

    private void SetBBoxValue(ref double field, double value, string pixelProperty)
    {
        if (field.Equals(value))
        {
            return;
        }

        field = value;
        Raise();
        Raise(pixelProperty);
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
