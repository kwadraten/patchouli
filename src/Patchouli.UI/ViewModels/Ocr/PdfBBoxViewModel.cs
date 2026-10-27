using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Markdig;
using Patchouli.Host.Composition;

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
        ContinuationState = box.ContinuationState;
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
        BeginSetContinuationCommand = new AsyncCommand(BeginSetContinuationAsync, () => Workspace.IsEditMode);
        ClearContinuationCommand = new AsyncCommand(ClearContinuationAsync,
            () => Workspace.IsEditMode && IsContinuation);

        _isConstructing = false;
    }

    public PdfWorkspaceViewModel Workspace { get; }
    public DocumentBoxId BoxId { get; }
    public DocumentBoxPayload? Payload { get; }
    public DocumentBoxId? ParentBoxId { get; }
    public DocumentBoxId? NextSiblingBoxId { get; }
    public DocumentBoxId? ContinuesFromBoxId { get; }

    /// <summary>接续兼容状态；非空仅见于迁移标记的旧版未恢复接续（<c>legacy_unrecovered</c>）。</summary>
    public string? ContinuationState { get; }

    /// <summary>本框是否为旧版推断产生、文字尚未恢复分配的接续框。</summary>
    public bool IsLegacyContinuation => ContinuationState == DocumentContinuationState.LegacyUnrecovered;

    public bool IsContinuation => ContinuesFromBoxId is not null;

    [ObservableProperty] public partial string? ContinuationSourceLabel { get; internal set; }

    partial void OnContinuationSourceLabelChanged(string? value)
    {
        Raise(nameof(HasContinuationHint));
    }

    /// <summary>接续关系提示是否可见（<see cref="ContinuationSourceLabel" /> 非空）。</summary>
    [ExcludeFromDerivedGeneration]
    public bool HasContinuationHint => !string.IsNullOrWhiteSpace(ContinuationSourceLabel);

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

        SaveTextCommand?.NotifyCanExecuteChanged();
    }

    [ObservableProperty] public partial int? HeadingLevel { get; set; }

    [ObservableProperty] public partial string? CodeLanguage { get; set; }

    public IBrush BoxColor => BoxType switch
    {
        DocumentBoxType.Title => Brushes.Blue,
        DocumentBoxType.Text => Brushes.Green,
        DocumentBoxType.Table => Brushes.Orange,
        DocumentBoxType.Image => Brushes.Red,
        DocumentBoxType.Equation => Brushes.Purple,
        DocumentBoxType.LogicalPage => Brushes.Teal,
        _ => Brushes.Gray
    };

    public IBrush VisualBoxColor => IsSuppressed ? Brushes.Gray : BoxColor;

    public bool IsDraft { get; }

    [ObservableProperty] public partial string? AssetId { get; set; }

    public bool IsSuppressed { get; }
    public int ReadingOrder { get; }
    public int Depth { get; }
    public Thickness TreeMargin => new(Depth * 20, 0, 0, 4);

    // 摘要永远显示本框自己的文字：接续是独立关系（见 ContinuationSourceLabel 的
    // 关系提示），不借源框文字冒充本框内容；本框无文字时如实显示为空。
    [ExcludeFromDerivedGeneration]
    public string Summary => IsContinuation
        ? "↳ " + (string.IsNullOrWhiteSpace(Text)
            ? "（无文本内容）"
            : Text.ReplaceLineEndings(" ").Trim())
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
        Raise(nameof(HandleOwner));
    }

    public int ZIndex => IsSelected ? 1 : 0;

    [ExcludeFromDerivedGeneration] public bool ShowHandles => IsSelected && Workspace.IsEditMode;

    [ExcludeFromDerivedGeneration] public PdfBBoxViewModel? HandleOwner => ShowHandles ? this : null;

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

    /// <summary>进入接续源框挑选（建立新接续或纠正现有接续的目标）。</summary>
    public AsyncCommand BeginSetContinuationCommand { get; }

    /// <summary>解除本框的接续关系；不改动任何框的文字。</summary>
    public AsyncCommand ClearContinuationCommand { get; }

    [ObservableProperty] public partial double NormalizedX { get; set; }
    [ObservableProperty] public partial double NormalizedY { get; set; }
    [ObservableProperty] public partial double NormalizedWidth { get; set; }
    [ObservableProperty] public partial double NormalizedHeight { get; set; }

    private async Task SaveTextAsync()
    {
        using IDisposable? operation = Workspace.EnterOperation();
        if (operation is null)
        {
            return;
        }

        PageEditSessionId? sessionId = Workspace.EditSessionId;
        if (sessionId is null)
        {
            return;
        }

        if (IsLogicalPage)
        {
            return;
        }

        HostServices services = await _main.ServicesAsync();
        int generation = _main.LibraryGeneration;
        UpdateLeafCommand command = new(BoxId, BoxType, PayloadForText(Text), HeadingLevel, CodeLanguage,
            Payload is AlgorithmBoxPayload ? DocumentBoxType.Algorithm : null);
        Result result = await Task.Run(() => services.DocumentTreeEditor.UpdateLeafAsync(sessionId.Value, command));
        if (!_main.IsCurrentLibraryContext(services, generation) || Workspace.EditSessionId != sessionId)
        {
            return;
        }

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
        using IDisposable? operation = Workspace.EnterOperation();
        if (operation is null)
        {
            return Result.Failure(AppErrorCodes.InvalidState, "工作区已关闭。");
        }

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

        HostServices services = await _main.ServicesAsync();
        int generation = _main.LibraryGeneration;
        UpdateLeafCommand command = new(BoxId, DocumentBoxType.Table, payload.Value);
        Result result = await Task.Run(() => services.DocumentTreeEditor.UpdateLeafAsync(sessionId.Value, command));
        if (!_main.IsCurrentLibraryContext(services, generation) || Workspace.EditSessionId != sessionId)
        {
            return result;
        }

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
        using IDisposable? operation = Workspace.EnterOperation();
        if (operation is null)
        {
            return;
        }

        PageEditSessionId? sessionId = Workspace.EditSessionId;
        if (sessionId is null)
        {
            return;
        }

        HostServices services = await _main.ServicesAsync();
        int generation = _main.LibraryGeneration;
        DocumentBoxId boxId = BoxId;
        Result result = await Task.Run(() => services.DocumentTreeEditor.DeleteBoxAsync(sessionId.Value, boxId));
        if (!_main.IsCurrentLibraryContext(services, generation) || Workspace.EditSessionId != sessionId)
        {
            return;
        }

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
        using IDisposable? operation = Workspace.EnterOperation();
        if (operation is null)
        {
            return;
        }

        PageEditSessionId? sessionId = Workspace.EditSessionId;
        if (sessionId is null || IsLogicalPage)
        {
            return;
        }

        HostServices services = await _main.ServicesAsync();
        int generation = _main.LibraryGeneration;
        DocumentBoxId boxId = BoxId;
        bool suppressed = !IsSuppressed;
        Result result = await Task.Run(() => services.DocumentTreeEditor.SetSuppressedAsync(
            sessionId.Value, boxId, suppressed));
        if (!_main.IsCurrentLibraryContext(services, generation) || Workspace.EditSessionId != sessionId)
        {
            return;
        }

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
        using IDisposable? operation = Workspace.EnterOperation();
        if (operation is null)
        {
            return;
        }

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

        HostServices services = await _main.ServicesAsync();
        int generation = _main.LibraryGeneration;
        DocumentBoxId boxId = BoxId;
        Result result = await Task.Run(() => services.DocumentTreeEditor.UpdateBBoxAsync(
            sessionId.Value, boxId, bbox));
        if (!_main.IsCurrentLibraryContext(services, generation) || Workspace.EditSessionId != sessionId)
        {
            return;
        }

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

    private async Task BeginSetContinuationAsync()
    {
        await Workspace.BeginContinuationPickAsync(this);
    }

    private async Task ClearContinuationAsync()
    {
        await Workspace.ClearContinuationAsync(this);
    }

    internal void NotifyEditModeChanged()
    {
        Raise(nameof(ShowHandles));
        Raise(nameof(HandleOwner));
        SaveTextCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        SaveBBoxCommand.NotifyCanExecuteChanged();
        BeginSetContinuationCommand.NotifyCanExecuteChanged();
        ClearContinuationCommand.NotifyCanExecuteChanged();
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
