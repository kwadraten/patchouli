using Avalonia.Media;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;

namespace Patchouli.UI;

public sealed class PdfBBoxViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private readonly PdfWorkspaceViewModel _workspace;
    private bool _isSelected;
    private string? _text;
    
    public PdfWorkspaceViewModel Workspace => _workspace;
    
    public LayoutNodeId NodeId { get; }
    public double Left { get; }
    public double Top { get; }
    public double Width { get; }
    public double Height { get; }
    public string NodeType { get; }
    public IBrush BoxColor { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; Raise(); }
    }

    public string? Text
    {
        get => _text;
        set { if (_text == value) return; _text = value; Raise(); }
    }

    public AsyncCommand SaveTextCommand { get; }
    public AsyncCommand IgnoreCommand { get; }

    public PdfBBoxViewModel(MainWindowViewModel main, PdfWorkspaceViewModel workspace, LayoutNode node, double imageWidth, double imageHeight)
    {
        _main = main;
        _workspace = workspace;
        NodeId = node.NodeId;
        NodeType = node.NodeType;
        Text = node.OwnText;
        
        if (node.BBox.HasValue)
        {
            Left = node.BBox.Value.X * imageWidth;
            Top = node.BBox.Value.Y * imageHeight;
            Width = node.BBox.Value.Width * imageWidth;
            Height = node.BBox.Value.Height * imageHeight;
        }

        BoxColor = GetColorForNodeType(node.NodeType);

        SaveTextCommand = new AsyncCommand(SaveTextAsync);
        IgnoreCommand = new AsyncCommand(IgnoreAsync);
    }

    private IBrush GetColorForNodeType(string nodeType)
    {
        return nodeType switch
        {
            LayoutNodeType.Heading => Brushes.Blue,
            LayoutNodeType.Paragraph => Brushes.Green,
            LayoutNodeType.Table => Brushes.Orange,
            "figure" => Brushes.Red,
            "formula" => Brushes.Purple,
            _ => Brushes.Gray
        };
    }

    private async Task SaveTextAsync()
    {
        var services = await _main.ServicesAsync();
        var result = await services.Layout.UpdateNodeTextAsync(NodeId, Text);
        if (result.IsFailure)
        {
            _workspace.Status = $"更新文本失败: {result.ErrorMessage}";
        }
    }

    private async Task IgnoreAsync()
    {
        var services = await _main.ServicesAsync();
        var result = await services.Layout.MarkIgnoredAsync(NodeId, true);
        if (result.IsSuccess)
        {
            _workspace.RemoveBBox(this);
        }
        else
        {
            _workspace.Status = $"忽略节点失败: {result.ErrorMessage}";
        }
    }
}
