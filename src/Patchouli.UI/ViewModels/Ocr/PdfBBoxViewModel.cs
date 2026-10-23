using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Documents;
using Patchouli.UI.Themes;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Markdig;

namespace Patchouli.UI.ViewModels;

public sealed partial class PdfBBoxViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private readonly string? _tableHtml;
    private readonly bool _isConstructing;

    public double ImageWidth { get; }
    public double ImageHeight { get; }

    public PdfBBoxViewModel(
        MainWindowViewModel main,
        PdfWorkspaceViewModel workspace,
        DocumentBox box,
        double imageWidth,
        double imageHeight,
        bool isDraft,
        int readingOrder = 0,
        int depth = 0)
    {
        _isConstructing = true;
        _main = main;
        Workspace = workspace;
        BoxId = box.BoxId;
        ParentBoxId = box.ParentBoxId;
        NextSiblingBoxId = box.NextSiblingBoxId;
        ContinuesFromBoxId = box.ContinuesFromBoxId;
        BoxType = box.BoxType;
        Payload = box.Payload;
        HeadingLevel = box.HeadingLevel;
        CodeLanguage = box.CodeLanguage;
        Text = PayloadText(box.Payload);
        AssetId = (box.Payload as MediaBoxPayload)?.AssetId;
        _tableHtml = (box.Payload as TableBoxPayload)?.Html;
        IsSuppressed = box.Suppressed;
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
        NormalizedX = box.BBox.X;
        NormalizedY = box.BBox.Y;
        NormalizedWidth = box.BBox.Width;
        NormalizedHeight = box.BBox.Height;
        ReadingOrder = readingOrder;
        Depth = depth;
        IsDraft = isDraft;
        SaveTextCommand = new AsyncCommand(SaveTextAsync, () => Workspace.IsEditMode && !IsLogicalPage);
        DeleteCommand = new AsyncCommand(DeleteAsync, () => Workspace.IsEditMode);
        SaveBBoxCommand = new AsyncCommand(SaveBBoxAsync, () => Workspace.IsEditMode);
        JumpToContinuationSourceCommand = new AsyncCommand(JumpToContinuationSourceAsync, () => IsContinuation);

        _isConstructing = false;
    }

    public PdfWorkspaceViewModel Workspace { get; }
    public DocumentBoxId BoxId { get; }
    public DocumentBoxPayload? Payload { get; }
    public DocumentBoxId? ParentBoxId { get; }
    public DocumentBoxId? NextSiblingBoxId { get; }
    public DocumentBoxId? ContinuesFromBoxId { get; }
    public bool IsContinuation => ContinuesFromBoxId is not null;

    [ObservableProperty] public partial string? ContinuationHeadText { get; internal set; }

    partial void OnContinuationHeadTextChanged(string? value)
    {
        Raise(nameof(Summary));
    }

    [ObservableProperty] public partial string? ContinuationSourceLabel { get; internal set; }

    public double Left => NormalizedX * ImageWidth;

    public double Top => NormalizedY * ImageHeight;

    public double Width => NormalizedWidth * ImageWidth;

    public double Height => NormalizedHeight * ImageHeight;

    [ObservableProperty] public partial string BoxType { get; set; } = string.Empty;

    partial void OnBoxTypeChanged(string value)
    {
        if (!_isConstructing && value == DocumentBoxType.Title && HeadingLevel is null)
        {
            HeadingLevel = 1;
        }

        Raise(nameof(BoxTone));
        Raise(nameof(VisualBoxTone));
        Raise(nameof(BoxColor));
        Raise(nameof(VisualBoxColor));
        SaveTextCommand?.NotifyCanExecuteChanged();
    }

    [ObservableProperty] public partial int? HeadingLevel { get; set; }

    [ObservableProperty] public partial string? CodeLanguage { get; set; }

    // Excluded from derived generation: tone/brush resolution goes through pure mapping
    // helpers; change notification is raised manually from OnBoxTypeChanged.
    /// <summary>The pure display tone for this box's type; <see cref="BoxColor"/> resolves it.</summary>
    [ExcludeFromDerivedGeneration]
    public UiTone BoxTone => UiToneMap.ForBoxType(BoxType);

    [ExcludeFromDerivedGeneration] public UiTone VisualBoxTone => UiToneMap.ForVisualBox(BoxType, IsSuppressed);

    [ExcludeFromDerivedGeneration] public IBrush BoxColor => UiToneBrushes.Resolve(BoxTone);

    [ExcludeFromDerivedGeneration] public IBrush VisualBoxColor => UiToneBrushes.Resolve(VisualBoxTone);

    public bool IsDraft { get; }

    [ObservableProperty] public partial string? AssetId { get; set; }

    public bool IsSuppressed { get; }
    public int ReadingOrder { get; }
    public int Depth { get; }
    public Thickness TreeMargin => new(Depth * 20, 0, 0, 4);

    [ExcludeFromDerivedGeneration]
    public string Summary => IsContinuation
        ? "↳ " + (string.IsNullOrWhiteSpace(ContinuationHeadText)
            ? "（续接区域，文字在源框）"
            : ContinuationHeadText!.ReplaceLineEndings(" ").Trim())
        : string.IsNullOrWhiteSpace(Text)
            ? "（无文本内容）"
            : Text.ReplaceLineEndings(" ").Trim();

    public bool IsLogicalPage => BoxType == DocumentBoxType.LogicalPage;
    public bool IsTable => BoxType == DocumentBoxType.Table;
    public bool IsMedia => BoxType is DocumentBoxType.Image or DocumentBoxType.Chart;
    public bool IsTitle => BoxType == DocumentBoxType.Title;
    public bool IsCode => BoxType is DocumentBoxType.Code or DocumentBoxType.Algorithm;

    [ObservableProperty] public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        Raise(nameof(ShowHandles));
    }

    public int ZIndex => IsSelected ? 1 : 0;

    [ExcludeFromDerivedGeneration] public bool ShowHandles => IsSelected && Workspace.IsEditMode;

    [ObservableProperty] public partial bool HasOverlapWarning { get; internal set; }

    [ObservableProperty] public partial bool HasChildren { get; internal set; }

    [ObservableProperty] public partial bool IsTreeExpanded { get; internal set; } = true;

    public double TreeChevronAngle => IsTreeExpanded ? 90 : 0;

    [ObservableProperty] public partial string? Text { get; set; }

    partial void OnTextChanged(string? value)
    {
        Raise(nameof(Summary));
    }

    public AsyncCommand SaveTextCommand { get; }
    public AsyncCommand DeleteCommand { get; }
    public AsyncCommand SaveBBoxCommand { get; }
    public AsyncCommand JumpToContinuationSourceCommand { get; }

    [ObservableProperty] public partial double NormalizedX { get; set; }
    [ObservableProperty] public partial double NormalizedY { get; set; }
    [ObservableProperty] public partial double NormalizedWidth { get; set; }
    [ObservableProperty] public partial double NormalizedHeight { get; set; }

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
            new UpdateLeafCommand(BoxId, BoxType, PayloadForText(Text), HeadingLevel, CodeLanguage,
                Payload is AlgorithmBoxPayload ? DocumentBoxType.Algorithm : null));
        if (result.IsFailure)
        {
            Workspace.Status = $"更新文本失败: {result.ErrorMessage}";
        }
        else
        {
            Workspace.Status = "文本已写入页面草稿。";
            Workspace.RefreshContinuationDependents(BoxId, Text);
            await Workspace.RefreshPreviewAsync();
        }
    }

    private DocumentBoxPayload PayloadForText(string? text)
    {
        return BoxType switch
        {
            DocumentBoxType.Code when Payload is AlgorithmBoxPayload => new AlgorithmBoxPayload(text ?? string.Empty),
            DocumentBoxType.Code => new CodeBoxPayload(text ?? string.Empty),
            DocumentBoxType.Equation => new EquationBoxPayload(text ?? string.Empty,
                Payload is EquationBoxPayload { HasImage: true }),
            DocumentBoxType.List or DocumentBoxType.Index => new ListBoxPayload(text ?? string.Empty),
            DocumentBoxType.Table => new TableBoxPayload(
                text ?? string.Empty,
                string.Equals(text?.Trim(), "[Table]", StringComparison.Ordinal) ? _tableHtml : null),
            DocumentBoxType.Image or DocumentBoxType.Chart =>
                new MediaBoxPayload(AssetId, string.IsNullOrWhiteSpace(text) ? null : text),
            _ => new TextBoxPayload(text ?? string.Empty)
        };
    }

    public Result<string> GetTableEditorHtml()
    {
        if (Payload is not TableBoxPayload table)
        {
            return Result<string>.Failure(AppErrorCodes.ValidationFailed, "选中的边界框没有表格内容。");
        }

        if (TableContentAdapter.TryGetHtml(table, out string storedHtml))
        {
            return Result<string>.Success(storedHtml);
        }

        if (TableContentAdapter.IsHtml(table))
        {
            return Result<string>.Failure(AppErrorCodes.ValidationFailed,
                "表格包含当前可视编辑器不支持的 HTML 结构。为避免丢失内容，已阻止编辑和保存。");
        }

        if (string.Equals(table.Markdown.Trim(), "[Table]", StringComparison.Ordinal))
        {
            return Result<string>.Failure(AppErrorCodes.ValidationFailed,
                "旧表格没有可恢复的 HTML 内容，无法安全打开可视编辑器。");
        }

        string converted = Markdown.ToHtml(table.Markdown,
            new MarkdownPipelineBuilder().UsePipeTables().Build());
        Result<TableBoxPayload> normalized = TableContentAdapter.CreateHtml(converted);
        return normalized.IsSuccess
            ? Result<string>.Success(normalized.Value.Html!)
            : Result<string>.Failure(normalized.ErrorCode!, normalized.ErrorMessage!);
    }

    public async Task<Result> SaveTableHtmlAsync(string html)
    {
        PageEditSessionId? sessionId = Workspace.EditSessionId;
        if (sessionId is null || !Workspace.IsEditMode)
        {
            return Result.Failure(AppErrorCodes.InvalidState, "请先在 PDF 工作台中打开页面编辑会话。");
        }

        Result<TableBoxPayload> payload = TableContentAdapter.CreateHtml(html);
        if (payload.IsFailure)
        {
            Workspace.Status = $"表格包含不受支持的结构，未保存：{payload.ErrorMessage}";
            return Result.Failure(payload.ErrorCode!, payload.ErrorMessage!);
        }

        Result result = await (await _main.ServicesAsync()).DocumentTreeEditor.UpdateLeafAsync(sessionId.Value,
            new UpdateLeafCommand(BoxId, DocumentBoxType.Table, payload.Value));
        if (result.IsFailure)
        {
            Workspace.Status = $"表格未写入页面草稿：{result.ErrorMessage}";
            return result;
        }

        Workspace.Status = "表格已写入页面草稿；提交前不会影响当前版本。";
        await Workspace.RefreshBoxesAsync();
        await Workspace.RefreshPreviewAsync();
        return Result.Success();
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
            Workspace.Status = $"删除边界框失败: {result.ErrorMessage}";
            return;
        }

        await Workspace.RefreshBoxesAsync();
        Workspace.Status = "边界框已从页面草稿删除；提交后才会生成新版本。";
    }

    internal async Task ToggleSuppressedAsync()
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
        Workspace.Status = IsSuppressed ? "边界框已重新纳入文档流。" : "边界框已从文档流排除。";
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
            Workspace.Status = $"区域无效: {valid.ErrorMessage}";
            return;
        }

        Result result = await (await _main.ServicesAsync()).DocumentTreeEditor.UpdateBBoxAsync(
            sessionId.Value, BoxId, bbox);
        if (result.IsFailure)
        {
            Workspace.Status = $"更新区域失败: {result.ErrorMessage}";
            return;
        }

        await Workspace.RefreshBoxesAsync();
        Workspace.Status = "区域已写入页面草稿。";
    }

    private async Task JumpToContinuationSourceAsync()
    {
        await Workspace.JumpToContinuationSourceAsync(this);
    }

    internal void NotifyEditModeChanged()
    {
        Raise(nameof(ShowHandles));
        SaveTextCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        SaveBBoxCommand.NotifyCanExecuteChanged();
    }

    internal void SetCanvasBBox(double left, double top, double width, double height)
    {
        NormalizedX = Math.Clamp(left / ImageWidth, 0, 1);
        NormalizedY = Math.Clamp(top / ImageHeight, 0, 1);
        NormalizedWidth = Math.Clamp(width / ImageWidth, 0.0001, 1 - NormalizedX);
        NormalizedHeight = Math.Clamp(height / ImageHeight, 0.0001, 1 - NormalizedY);
    }


    internal static string? PayloadText(DocumentBoxPayload? payload)
    {
        return payload switch
        {
            TextBoxPayload value => value.Markdown,
            ListBoxPayload value => value.Markdown,
            TableBoxPayload value => TableContentAdapter.GetPlainText(value),
            AlgorithmBoxPayload algorithm => algorithm.Markdown,
            EquationBoxPayload value => value.Latex,
            CodeBoxPayload value => value.Code,
            MediaBoxPayload value => value.Description,
            _ => null
        };
    }
}
