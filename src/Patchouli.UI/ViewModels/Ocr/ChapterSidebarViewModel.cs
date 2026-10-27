using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Pagination;
using Patchouli.Core.Results;
using Patchouli.UI.Diagnostics;
using Patchouli.UI.ViewModels.Core;

namespace Patchouli.UI.ViewModels;

public sealed record ChapterSidebarServices(
    IDocumentPageMapService Maps,
    IChapterScanService Scanner,
    IPageService Pages,
    IDocumentTreeService Trees);

public sealed record ChapterLogicalTarget(DocumentBoxId? BoxId, string Label)
{
    public override string ToString()
    {
        return Label;
    }
}

/// <summary>Independent committed-data editor; never opens an OCR working revision.</summary>
public sealed partial class ChapterSidebarViewModel : ViewModelBase
{
    private readonly Func<Task<ChapterSidebarServices>> _services;
    private readonly Func<DocumentInstanceId?> _document;
    private readonly Func<int> _currentPage;
    private readonly Func<NavigationPosition, Task> _navigate;
    private readonly Func<Task> _reload;
    private ChapterEditingSnapshot? _snapshot;
    private ChapterScanResult? _scan;
    private string? _editingId;
    private string? _newParent;
    private string? _insertAfter;
    private ChapterScanRowViewModel? _previewEditing;
    private string? _deleteId;
    private CancellationTokenSource? _scanCancellation;
    private int _targetGeneration;
    private int _generation;
    private bool _closed;
    private AsyncOperationLifetime _operations = new();
    private int _previewBatchDepth;

    public ChapterSidebarViewModel(Func<Task<ChapterSidebarServices>> services,
        Func<DocumentInstanceId?> document, Func<int> currentPage,
        Func<NavigationPosition, Task> navigate, Func<Task> reload)
    {
        _services = services;
        _document = document;
        _currentPage = currentPage;
        _navigate = navigate;
        _reload = reload;
        AddCommand = new AsyncCommand(() => BeginAddAsync(null, false));
        ScanCommand = new AsyncCommand(ScanAsync);
        CancelScanCommand = new RelayCommand(_ => _scanCancellation?.Cancel());
        SaveCommand = new AsyncCommand(SaveFormAsync);
        CancelFormCommand = new RelayCommand(_ => CloseForm());
        CancelPreviewCommand = new RelayCommand(_ =>
        {
            if (!IsBusy)
            {
                ClearPreview();
            }
        });
        ApplyScanCommand = new AsyncCommand(ApplyScanAsync);
        ConfirmDeleteCommand = new AsyncCommand(ConfirmDeleteAsync);
        CancelDeleteCommand = new RelayCommand(_ => IsDeletePending = false);
        UseCurrentPositionCommand = new AsyncCommand(async () =>
        {
            TargetPdfPage = _currentPage().ToString();
            await LoadLogicalTargetsAsync();
        });
        LoadLogicalTargetsCommand = new AsyncCommand(() => LoadLogicalTargetsAsync(SelectedLogicalTarget?.BoxId));
        ReloadBaselineCommand = new AsyncCommand(ReloadBaselineAsync);
    }

    public AsyncCommand AddCommand { get; }
    public AsyncCommand ScanCommand { get; }
    public RelayCommand CancelScanCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public RelayCommand CancelFormCommand { get; }
    public RelayCommand CancelPreviewCommand { get; }
    public AsyncCommand ApplyScanCommand { get; }
    public AsyncCommand ConfirmDeleteCommand { get; }
    public RelayCommand CancelDeleteCommand { get; }
    public AsyncCommand UseCurrentPositionCommand { get; }
    public AsyncCommand LoadLogicalTargetsCommand { get; }
    public AsyncCommand ReloadBaselineCommand { get; }
    public ObservableCollection<ChapterLogicalTarget> LogicalTargets { get; } = new();
    public ObservableCollection<ChapterScanRowViewModel> PreviewRows { get; } = new();
    public ObservableCollection<ChapterPreviewTreeRow> SelectedPreviewTree { get; } = new();
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial bool IsScanning { get; private set; }
    [ObservableProperty] public partial bool IsFormOpen { get; private set; }
    [ObservableProperty] public partial bool IsLoadingTargets { get; private set; }
    [ObservableProperty] public partial bool HasPreview { get; private set; }
    [ObservableProperty] public partial bool IsDeletePending { get; private set; }
    [ObservableProperty] public partial bool HasConflict { get; private set; }
    [ObservableProperty] public partial bool ReplaceExisting { get; set; }
    [ObservableProperty] public partial bool ConfirmReplacement { get; set; }
    [ObservableProperty] public partial string Status { get; private set; } = string.Empty;
    [ObservableProperty] public partial string FormTitle { get; private set; } = string.Empty;
    [ObservableProperty] public partial string Title { get; set; } = string.Empty;
    [ObservableProperty] public partial string TargetPdfPage { get; set; } = "1";
    [ObservableProperty] public partial ChapterLogicalTarget? SelectedLogicalTarget { get; set; }
    [ObservableProperty] public partial string DeleteMessage { get; private set; } = string.Empty;
    [ObservableProperty] public partial string? SelectedChapterId { get; set; }

    public bool CanStartOperation => !IsBusy && !IsFormOpen && !HasPreview && !IsDeletePending;
    public bool CanEditForm => !IsBusy;
    public bool CanSaveForm => !IsBusy && !IsLoadingTargets;

    private bool IsCurrent(DocumentInstanceId id, int generation)
    {
        return !_closed && generation == _generation && _document() == id;
    }

    partial void OnTargetPdfPageChanged(string value)
    {
        _targetGeneration++;
        LogicalTargets.Clear();
        ChapterLogicalTarget physical = new(null, "物理页（不指定逻辑页）");
        LogicalTargets.Add(physical);
        SelectedLogicalTarget = physical;
        if (IsFormOpen)
        {
            LoadLogicalTargetsAsync().Observe(nameof(ChapterSidebarViewModel), "chapter-logical-targets");
        }
    }

    public async Task RefreshAsync()
    {
        using IDisposable? operation = _operations.TryEnter();
        if (operation is null)
        {
            return;
        }

        if (!CanStartOperation || _document() is not { } id)
        {
            return;
        }

        int generation = _generation;
        ChapterSidebarServices services = await _services();
        Result<ChapterEditingSnapshot> result = await Task.Run(() => services.Maps.GetChapterEditingSnapshotAsync(id));
        if (!IsCurrent(id, generation) || !CanStartOperation)
        {
            return;
        }

        if (result.IsSuccess)
        {
            _snapshot = result.Value;
        }
        else
        {
            Status = result.ErrorMessage ?? "章节加载失败。";
        }
    }

    public async Task BeginAddAsync(string? relatedId, bool asChild)
    {
        if (!CanStartOperation)
        {
            return;
        }

        DocumentInstanceId? document = _document();
        int generation = _generation;
        await RefreshAsync();
        if (!CanStartOperation || _snapshot is null || document is not { } id || !IsCurrent(id, generation))
        {
            return;
        }

        if (relatedId is not null && Find(_snapshot.Definition.Chapters, relatedId) is null)
        {
            Status = "相关章节已被删除，请重新选择。";
            return;
        }

        _editingId = null;
        _previewEditing = null;
        _newParent = asChild ? relatedId : FindParent(_snapshot.Definition.Chapters, relatedId);
        _insertAfter = asChild ? null : relatedId;
        Title = string.Empty;
        TargetPdfPage = _currentPage().ToString();
        FormTitle = asChild ? "添加子章节" : "添加章节";
        IsFormOpen = true;
        HasConflict = false;
        Status = string.Empty;
        await LoadLogicalTargetsAsync();
    }

    public async Task BeginEditAsync(string id)
    {
        if (!CanStartOperation)
        {
            return;
        }

        DocumentInstanceId? document = _document();
        int generation = _generation;
        await RefreshAsync();
        if (!CanStartOperation || document is not { } current || !IsCurrent(current, generation))
        {
            return;
        }

        ChapterNode? node = _snapshot is null ? null : Find(_snapshot.Definition.Chapters, id);
        if (node is null)
        {
            return;
        }

        SelectedChapterId = _editingId = id;
        _previewEditing = null;
        FormTitle = "编辑章节";
        Title = node.Title;
        TargetPdfPage = node.Target.PdfPage.ToString();
        IsFormOpen = true;
        HasConflict = false;
        Status = string.Empty;
        await LoadLogicalTargetsAsync(node.Target.LogicalPage);
    }

    public async Task SetCurrentPositionAsync(string id)
    {
        DocumentInstanceId? document = _document();
        int generation = _generation;
        await BeginEditAsync(id);
        if (document is not { } current || !IsCurrent(current, generation))
        {
            return;
        }

        if (IsFormOpen && _editingId == id)
        {
            TargetPdfPage = _currentPage().ToString();
            await LoadLogicalTargetsAsync();
        }
    }

    private Task LoadLogicalTargetsAsync()
    {
        return LoadLogicalTargetsAsync(null);
    }

    private async Task LoadLogicalTargetsAsync(DocumentBoxId? preferred)
    {
        using IDisposable? operation = _operations.TryEnter();
        if (operation is null)
        {
            return;
        }

        int generation = ++_targetGeneration;
        IsLoadingTargets = true;
        try
        {
            LogicalTargets.Clear();
            ChapterLogicalTarget physical = new(null, "物理页（不指定逻辑页）");
            LogicalTargets.Add(physical);
            SelectedLogicalTarget = physical;
            ChapterLogicalTarget? original = null;
            if (preferred.HasValue)
            {
                original = new ChapterLogicalTarget(preferred, "原逻辑页（正在检查已提交版本）");
                LogicalTargets.Add(original);
                SelectedLogicalTarget = original;
            }

            if (_document() is not { } id || !int.TryParse(TargetPdfPage, out int pdfPage))
            {
                return;
            }

            int documentGeneration = _generation;
            ChapterSidebarServices services = await _services();
            if (!IsCurrent(id, documentGeneration) || generation != _targetGeneration)
            {
                return;
            }

            Result<IReadOnlyList<Page>> pages = await Task.Run(() => services.Pages.ListPagesAsync(id));
            Page? page = pages.IsSuccess ? pages.Value.FirstOrDefault(p => p.PageIndex == pdfPage - 1) : null;
            if (page is null || generation != _targetGeneration || !IsCurrent(id, documentGeneration))
            {
                return;
            }

            Result<DocumentTreeRevision> revision =
                await Task.Run(() => services.Trees.GetCurrentRevisionAsync(id, page.PageId));
            if (revision.IsSuccess)
            {
                Result<IReadOnlyList<DocumentBox>> boxes =
                    await Task.Run(() => services.Trees.ListBoxesAsync(revision.Value.TreeRevisionId));
                if (generation != _targetGeneration || !IsCurrent(id, documentGeneration))
                {
                    return;
                }

                if (boxes.IsSuccess)
                {
                    int index = 0;
                    foreach (DocumentBox box in boxes.Value.Where(b => b.BoxType == DocumentBoxType.LogicalPage))
                    {
                        ChapterLogicalTarget target = new(box.BoxId, $"已提交逻辑页 {++index}");
                        LogicalTargets.Add(target);
                        if (box.BoxId == preferred)
                        {
                            SelectedLogicalTarget = target;
                        }
                    }
                }
            }

            if (generation != _targetGeneration || !IsCurrent(id, documentGeneration))
            {
                return;
            }

            if (original is not null)
            {
                bool unresolved = ReferenceEquals(SelectedLogicalTarget, original);
                LogicalTargets.Remove(original);
                if (unresolved)
                {
                    ChapterLogicalTarget stale = new(preferred, "原逻辑页已失效（可保留或改为物理页）");
                    LogicalTargets.Add(stale);
                    SelectedLogicalTarget = stale;
                }
            }
        }
        finally
        {
            if (generation == _targetGeneration)
            {
                IsLoadingTargets = false;
            }
        }
    }

    private async Task SaveFormAsync()
    {
        if (!CanSaveForm || !IsFormOpen || _snapshot is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Title) || !int.TryParse(TargetPdfPage, out int pdfPage) || pdfPage < 1)
        {
            Status = "请填写章节标题和有效的 PDF 页码。";
            return;
        }

        NavigationPosition target = new(pdfPage, SelectedLogicalTarget?.BoxId);
        if (_previewEditing is not null)
        {
            _previewEditing.Title = Title.Trim();
            _previewEditing.Target = target;
            CloseForm();
            return;
        }

        IReadOnlyList<ChapterNode> chapters = _snapshot.Definition.Chapters;
        if (_editingId is not null && Find(chapters, _editingId) is null)
        {
            Status = "此章节已被其他操作删除，输入已保留；请取消后重新添加。";
            return;
        }

        if (_editingId is null && ((_newParent is not null && Find(chapters, _newParent) is null) ||
                                   (_insertAfter is not null && (Find(chapters, _insertAfter) is null ||
                                                                 FindParent(chapters, _insertAfter) != _newParent))))
        {
            Status = "原插入位置已改变，输入已保留；请取消后重新选择添加位置。";
            return;
        }

        string id = _editingId ?? $"manual-{Guid.NewGuid():N}";
        if (_editingId is not null)
        {
            chapters = Transform(chapters, id, n => n with { Title = Title.Trim(), Target = target });
        }
        else
        {
            chapters = Insert(chapters, _newParent, _insertAfter, ChapterNode.Leaf(id, Title.Trim(), target));
        }

        string? previousSelection = SelectedChapterId;
        int generation = _generation;
        DocumentInstanceId? document = _document();
        SelectedChapterId = id;
        bool saved = await SaveAsync(chapters);
        if (document is not { } current || !IsCurrent(current, generation))
        {
            return;
        }

        if (saved)
        {
            CloseForm();
        }
        else
        {
            SelectedChapterId = previousSelection;
        }
    }

    public void RequestDelete(string id)
    {
        if (!CanStartOperation || _snapshot is null)
        {
            return;
        }

        ChapterNode? node = Find(_snapshot.Definition.Chapters, id);
        if (node is null)
        {
            return;
        }

        _deleteId = id;
        DeleteMessage = $"删除“{node.Title}”及其 {Count(node.Children)} 个子章节？";
        IsDeletePending = true;
    }

    private async Task ConfirmDeleteAsync()
    {
        if (IsBusy || !IsDeletePending || _snapshot is null || _deleteId is null)
        {
            return;
        }

        if (await SaveAsync(Remove(_snapshot.Definition.Chapters, _deleteId)))
        {
            IsDeletePending = false;
        }
    }

    public async Task MoveAsync(string id, int direction)
    {
        if (!CanStartOperation)
        {
            return;
        }

        DocumentInstanceId? document = _document();
        int generation = _generation;
        await RefreshAsync();
        if (!CanStartOperation || _snapshot is null || document is not { } current || !IsCurrent(current, generation))
        {
            return;
        }

        string? parent = FindParent(_snapshot.Definition.Chapters, id);
        IReadOnlyList<ChapterNode> siblings = parent is null
            ? _snapshot.Definition.Chapters
            : Find(_snapshot.Definition.Chapters, parent)!.Children;
        List<ChapterNode> reordered = siblings.ToList();
        int from = reordered.FindIndex(n => n.ChapterId == id);
        int to = from + direction;
        if (from < 0 || to < 0 || to >= reordered.Count)
        {
            return;
        }

        (reordered[from], reordered[to]) = (reordered[to], reordered[from]);
        await SaveAsync(parent is null
            ? reordered
            : Transform(_snapshot.Definition.Chapters, parent, n => n with { Children = reordered }));
    }

    public async Task IndentAsync(string id)
    {
        if (!CanStartOperation)
        {
            return;
        }

        DocumentInstanceId? document = _document();
        int generation = _generation;
        await RefreshAsync();
        if (!CanStartOperation || _snapshot is null || document is not { } current || !IsCurrent(current, generation))
        {
            return;
        }

        string? parent = FindParent(_snapshot.Definition.Chapters, id);
        IReadOnlyList<ChapterNode> siblings = parent is null
            ? _snapshot.Definition.Chapters
            : Find(_snapshot.Definition.Chapters, parent)!.Children;
        int index = siblings.ToList().FindIndex(n => n.ChapterId == id);
        if (index > 0)
        {
            await MoveToAsync(id, siblings[index - 1].ChapterId, null);
        }
    }

    public async Task OutdentAsync(string id)
    {
        if (!CanStartOperation)
        {
            return;
        }

        DocumentInstanceId? document = _document();
        int generation = _generation;
        await RefreshAsync();
        if (!CanStartOperation || _snapshot is null || document is not { } current || !IsCurrent(current, generation))
        {
            return;
        }

        string? parent = FindParent(_snapshot.Definition.Chapters, id);
        if (parent is not null)
        {
            await MoveToAsync(id, FindParent(_snapshot.Definition.Chapters, parent), parent);
        }
    }

    /// <summary>Drag/drop and keyboard moves share descendant-cycle prevention.</summary>
    public async Task MoveToAsync(string id, string? parent, string? after)
    {
        if (!CanStartOperation || _snapshot is null)
        {
            return;
        }

        ChapterNode? node = Find(_snapshot.Definition.Chapters, id);
        if (node is null || id == parent || id == after ||
            (parent is not null && Find(node.Children, parent) is not null) ||
            (after is not null && Find(node.Children, after) is not null))
        {
            Status = "不能将章节移动到自身或其子章节中。";
            return;
        }

        if (parent is not null && Find(_snapshot.Definition.Chapters, parent) is null)
        {
            Status = "目标父章节已被删除，请重新选择。";
            return;
        }

        IReadOnlyList<ChapterNode> siblings = parent is null
            ? _snapshot.Definition.Chapters
            : Find(_snapshot.Definition.Chapters, parent)!.Children;
        if (after is not null && siblings.All(n => n.ChapterId != after))
        {
            Status = "目标章节已改变位置，请重新选择。";
            return;
        }

        await SaveAsync(Insert(Remove(_snapshot.Definition.Chapters, id), parent, after, node));
    }

    public async Task DropAsync(string id, string target, bool asChild)
    {
        if (!CanStartOperation)
        {
            return;
        }

        DocumentInstanceId? document = _document();
        int generation = _generation;
        await RefreshAsync();
        if (!CanStartOperation || _snapshot is null || document is not { } current || !IsCurrent(current, generation))
        {
            return;
        }

        await MoveToAsync(id, asChild ? target : FindParent(_snapshot.Definition.Chapters, target),
            asChild ? null : target);
    }

    private async Task<bool> SaveAsync(IReadOnlyList<ChapterNode> chapters,
        IReadOnlyList<ChapterPageRevisionExpectation>? versions = null)
    {
        using IDisposable? operation = _operations.TryEnter();
        if (operation is null)
        {
            return false;
        }

        if (IsBusy || _snapshot is null || _document() is not { } id)
        {
            return false;
        }

        int generation = _generation;
        ChapterEditingSnapshot snapshot = _snapshot;
        IsBusy = true;
        try
        {
            ChapterSidebarServices services = await _services();
            if (!IsCurrent(id, generation))
            {
                return false;
            }

            ChapterSaveRequest request = new(snapshot.VersionToken, chapters, versions);
            Result<ChapterEditingSnapshot> result = await Task.Run(() => services.Maps.SaveChaptersAsync(id, request));
            if (!IsCurrent(id, generation))
            {
                return false;
            }

            if (result.IsFailure)
            {
                Status = $"保存失败：{result.ErrorMessage}。输入已保留；若映射已更新，请重新加载后核对再保存。";
                HasConflict = true;
                return false;
            }

            _snapshot = result.Value;
            HasConflict = false;
            Status = "章节已保存。";
            await _reload();
            return IsCurrent(id, generation);
        }
        finally
        {
            if (IsCurrent(id, generation))
            {
                IsBusy = false;
            }
        }
    }

    private async Task ReloadBaselineAsync()
    {
        using IDisposable? operation = _operations.TryEnter();
        if (operation is null)
        {
            return;
        }

        if (IsBusy || _document() is not { } id)
        {
            return;
        }

        int generation = _generation;
        ChapterSidebarServices services = await _services();
        Result<ChapterEditingSnapshot> result = await Task.Run(() => services.Maps.GetChapterEditingSnapshotAsync(id));
        if (!IsCurrent(id, generation))
        {
            return;
        }

        if (result.IsFailure)
        {
            Status = result.ErrorMessage ?? "重新加载失败。";
            return;
        }

        _snapshot = result.Value;
        HasConflict = false;
        Status = HasPreview ? "映射已重新加载，预览已保留；页面内容变化时请重新扫描。" : "映射已重新加载，输入已保留，请核对后再保存。";
        await _reload();
    }

    private async Task ScanAsync()
    {
        using IDisposable? operation = _operations.TryEnter();
        if (operation is null)
        {
            return;
        }

        if (!CanStartOperation || _document() is not { } id)
        {
            return;
        }

        int generation = _generation;
        await RefreshAsync();
        if (!CanStartOperation || _snapshot is null || !IsCurrent(id, generation))
        {
            return;
        }

        IsBusy = IsScanning = true;
        using CancellationTokenSource cancellation = new();
        _scanCancellation = cancellation;
        try
        {
            ChapterSidebarServices services = await _services();
            if (!IsCurrent(id, generation))
            {
                return;
            }

            Progress<ChapterScanProgress> progress = new(p =>
            {
                if (IsCurrent(id, generation) && IsScanning)
                {
                    Status = $"正在扫描章节：{p.CompletedPages}/{p.TotalPages} 页";
                }
            });
            Result<ChapterScanResult> result = await Task.Run(() => services.Scanner.ScanAsync(id,
                new ChapterScanOptions(), progress, cancellation.Token), cancellation.Token);
            if (!IsCurrent(id, generation))
            {
                return;
            }

            if (cancellation.IsCancellationRequested)
            {
                Status = "扫描已取消，已有章节未改变。";
                return;
            }

            if (result.IsFailure)
            {
                Status = $"扫描失败：{result.ErrorMessage}";
                return;
            }

            _scan = result.Value;
            _previewBatchDepth++;
            try
            {
                PreviewRows.Clear();
                ChapterNode[] existingNodes = Flatten(_snapshot.Definition.Chapters).ToArray();
                HashSet<string> existing = existingNodes.Select(n => n.ChapterId)
                    .ToHashSet(StringComparer.Ordinal);
                HashSet<(string Title, int PdfPage)> existingPositions =
                    existingNodes.Select(n => (n.Title, n.Target.PdfPage)).ToHashSet();
                Dictionary<string, ChapterScanCandidate> sources =
                    _scan.Candidates.ToDictionary(c => c.Chapter.ChapterId, StringComparer.Ordinal);
                bool hasUnmatchedPriorScan = existingNodes.Any(node =>
                    node.ChapterId.StartsWith("scan-", StringComparison.Ordinal) &&
                    !sources.ContainsKey(node.ChapterId));
                foreach ((ChapterNode node, int depth) in FlattenWithDepth(_scan.Chapters, 0))
                {
                    sources.TryGetValue(node.ChapterId, out ChapterScanCandidate? source);
                    bool duplicate = existing.Contains(node.ChapterId);
                    bool ambiguousPriorScan = !duplicate && hasUnmatchedPriorScan;
                    bool possibleDuplicate =
                        !duplicate && (ambiguousPriorScan ||
                                       existingPositions.Contains((node.Title, node.Target.PdfPage)));
                    PreviewRows.Add(new ChapterScanRowViewModel(this, node, depth, source?.HeadingLevel ?? depth + 1,
                        !duplicate && !possibleDuplicate,
                        duplicate
                            ? "已存在：追加时保留人工修改"
                            : ambiguousPriorScan
                                ? "旧扫描章节无法对应当前结果，疑似重复：默认不选中，请核对。"
                                : possibleDuplicate
                                    ? "疑似重复：默认不选中，请核对"
                                    : string.Empty));
                }
            }
            finally
            {
                _previewBatchDepth--;
            }

            HasPreview = PreviewRows.Count > 0;
            RefreshSelectedPreview();
            ReplaceExisting = ConfirmReplacement = false;
            int scannedPages = _scan.PageVersions.Count;
            Status = $"已扫描 {scannedPages} 页，发现 {PreviewRows.Count} 个标题；{_scan.MissingTextPages.Count} 页缺少文本。" +
                     (_scan.MissingTextPages.Count > 0
                         ? $"未扫描页：{string.Join("、", _scan.MissingTextPages)}。"
                         : string.Empty) +
                     string.Join("；", _scan.Diagnostics.Select(d => d.Message)
                         .Concat(PreviewRows.Select(row => row.Diagnostic)
                             .Where(message => !string.IsNullOrWhiteSpace(message))).Distinct());
            if (!HasPreview)
            {
                Status += "未找到可导入标题，已有目录保持不变。";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (IsCurrent(id, generation))
            {
                Status = "扫描已取消，已有章节未改变。";
            }
        }
        finally
        {
            if (IsCurrent(id, generation))
            {
                _scanCancellation = null;
                IsBusy = IsScanning = false;
            }
        }
    }

    public async Task EditPreviewAsync(ChapterScanRowViewModel row)
    {
        if (IsBusy || IsFormOpen)
        {
            return;
        }

        _previewEditing = row;
        _editingId = null;
        Title = row.Title;
        TargetPdfPage = row.Target.PdfPage.ToString();
        FormTitle = "修订扫描章节";
        IsFormOpen = true;
        await LoadLogicalTargetsAsync(row.Target.LogicalPage);
    }

    public Task NavigatePreviewAsync(ChapterScanRowViewModel row)
    {
        return _navigate(row.Target);
    }

    public void DropPreview(ChapterScanRowViewModel source, ChapterScanRowViewModel target,
        bool after, bool asChild, bool outdent = false)
    {
        if (IsBusy || IsFormOpen || source == target)
        {
            return;
        }

        int from = PreviewRows.IndexOf(source), destination = PreviewRows.IndexOf(target);
        if (from < 0 || destination < 0)
        {
            return;
        }

        int end = from + 1;
        while (end < PreviewRows.Count && PreviewRows[end].Depth > source.Depth)
        {
            end++;
        }

        if (destination >= from && destination < end)
        {
            return;
        }

        int depth = Math.Max(0, target.Depth + (asChild ? 1 : outdent ? -1 : 0));
        if (after || asChild)
        {
            destination++;
            while (destination < PreviewRows.Count && PreviewRows[destination].Depth > target.Depth)
            {
                destination++;
            }
        }

        ChapterScanRowViewModel[] subtree = PreviewRows.Skip(from).Take(end - from).ToArray();
        if (destination > from)
        {
            destination -= subtree.Length;
        }

        _previewBatchDepth++;
        try
        {
            for (int index = end - 1; index >= from; index--)
            {
                PreviewRows.RemoveAt(index);
            }

            int maximumDepth = destination == 0 ? 0 : PreviewRows[destination - 1].Depth + 1;
            depth = Math.Min(depth, maximumDepth);
            int delta = Math.Min(depth - source.Depth, 31 - subtree.Max(row => row.Depth));
            foreach (ChapterScanRowViewModel row in subtree)
            {
                row.Depth += delta;
                PreviewRows.Insert(destination++, row);
            }
        }
        finally
        {
            _previewBatchDepth--;
        }

        RefreshSelectedPreview();
    }

    public void RefreshSelectedPreview()
    {
        if (_previewBatchDepth > 0)
        {
            return;
        }

        SelectedPreviewTree.Clear();
        foreach ((ChapterNode node, int depth) in FlattenWithDepth(BuildSelectedPreview(PreviewRows), 0))
        {
            SelectedPreviewTree.Add(new ChapterPreviewTreeRow(node.Title, $"PDF 第 {node.Target.PdfPage} 页",
                new Thickness(depth * 12, 0, 0, 0)));
        }
    }

    /// <summary>Unchecked parents promote selected descendants, without inventing headings.</summary>
    public static IReadOnlyList<ChapterNode> BuildSelectedPreview(IEnumerable<ChapterScanRowViewModel> rows)
    {
        List<(ChapterNode Node, int Depth)> selected = new();
        Stack<(int Depth, bool Included)> ancestors = new();
        foreach (ChapterScanRowViewModel row in rows)
        {
            int depth = Math.Clamp(row.Depth, 0, 31);
            while (ancestors.Count > 0 && ancestors.Peek().Depth >= depth)
            {
                ancestors.Pop();
            }

            if (row.IsIncluded)
            {
                selected.Add((ChapterNode.Leaf(row.ChapterId, row.Title.Trim(), row.Target),
                    ancestors.Count(a => a.Included)));
            }

            ancestors.Push((depth, row.IsIncluded));
        }

        int cursor = 0;
        return BuildPreviewLevel(selected, ref cursor, -1);
    }

    private static IReadOnlyList<ChapterNode> BuildPreviewLevel(IReadOnlyList<(ChapterNode Node, int Depth)> rows,
        ref int cursor, int parentDepth)
    {
        List<ChapterNode> result = new();
        while (cursor < rows.Count && rows[cursor].Depth > parentDepth)
        {
            (ChapterNode node, int depth) = rows[cursor++];
            result.Add(node with { Children = BuildPreviewLevel(rows, ref cursor, depth) });
        }

        return result;
    }

    private async Task ApplyScanAsync()
    {
        if (IsBusy || IsFormOpen || !HasPreview || _snapshot is null || _scan is null)
        {
            return;
        }

        if (ReplaceExisting && !ConfirmReplacement)
        {
            Status = "请确认替换全部章节。";
            return;
        }

        IReadOnlyList<ChapterNode> selected = BuildSelectedPreview(PreviewRows);
        if (selected.Count == 0)
        {
            Status = "请选择至少一个章节，已有目录未改变。";
            return;
        }

        IReadOnlyList<ChapterNode> chapters =
            ReplaceExisting ? selected : AppendUnique(_snapshot.Definition.Chapters, selected);
        ChapterPageRevisionExpectation[] versions = _scan.PageVersions
            .Select(p => new ChapterPageRevisionExpectation(p.PageId, p.PdfPage, p.RevisionId)).ToArray();
        if (await SaveAsync(chapters, versions))
        {
            ClearPreview();
        }
    }

    private static IReadOnlyList<ChapterNode> AppendUnique(IReadOnlyList<ChapterNode> existing,
        IReadOnlyList<ChapterNode> incoming)
    {
        HashSet<string> ids = Flatten(existing).Select(n => n.ChapterId).ToHashSet(StringComparer.Ordinal);
        return existing.Concat(PruneDuplicateNodes(incoming, ids)).ToArray();
    }

    private static IEnumerable<ChapterNode> PruneDuplicateNodes(IEnumerable<ChapterNode> nodes, HashSet<string> ids)
    {
        foreach (ChapterNode node in nodes)
        {
            ChapterNode[] children = PruneDuplicateNodes(node.Children, ids).ToArray();
            if (ids.Add(node.ChapterId))
            {
                yield return node with { Children = children };
            }
            else
            {
                foreach (ChapterNode child in children)
                {
                    yield return child;
                }
            }
        }
    }

    private void CloseForm()
    {
        if (IsBusy)
        {
            return;
        }

        IsFormOpen = false;
        _previewEditing = null;
        _targetGeneration++;
    }

    private void ClearPreview()
    {
        HasPreview = false;
        PreviewRows.Clear();
        SelectedPreviewTree.Clear();
        _scan = null;
        ConfirmReplacement = false;
    }

    public void Reset()
    {
        _generation++;
        _targetGeneration++;
        _scanCancellation?.Cancel();
        _scanCancellation = null;
        IsBusy = IsScanning = IsFormOpen = IsLoadingTargets = false;
        _previewEditing = null;
        ClearPreview();
        _snapshot = null;
        IsDeletePending = HasConflict = false;
        SelectedChapterId = null;
        Status = string.Empty;
    }

    internal async Task DrainAsync()
    {
        Reset();
        await _operations.DisposeAsync();
    }

    internal void ResumeAfterFailedClose()
    {
        _operations = new AsyncOperationLifetime();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _operations.Dispose();
            _closed = true;
            Reset();
        }

        base.Dispose(disposing);
    }

    private static IEnumerable<ChapterNode> Flatten(IEnumerable<ChapterNode> nodes)
    {
        return nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));
    }

    private static IEnumerable<(ChapterNode Node, int Depth)> FlattenWithDepth(IEnumerable<ChapterNode> nodes,
        int depth)
    {
        return nodes.SelectMany(n => new[] { (n, depth) }.Concat(FlattenWithDepth(n.Children, depth + 1)));
    }

    private static int Count(IEnumerable<ChapterNode> nodes)
    {
        return Flatten(nodes).Count();
    }

    private static ChapterNode? Find(IEnumerable<ChapterNode> nodes, string? id)
    {
        return id is null ? null : Flatten(nodes).FirstOrDefault(n => n.ChapterId == id);
    }

    private static string? FindParent(IEnumerable<ChapterNode> nodes, string? id)
    {
        return Flatten(nodes).FirstOrDefault(n => n.Children.Any(c => c.ChapterId == id))?.ChapterId;
    }

    private static IReadOnlyList<ChapterNode> Transform(IEnumerable<ChapterNode> nodes, string id,
        Func<ChapterNode, ChapterNode> transform)
    {
        return nodes.Select(n =>
                n.ChapterId == id ? transform(n) : n with { Children = Transform(n.Children, id, transform) })
            .ToArray();
    }

    private static IReadOnlyList<ChapterNode> Remove(IEnumerable<ChapterNode> nodes, string id)
    {
        return nodes.Where(n => n.ChapterId != id).Select(n => n with { Children = Remove(n.Children, id) }).ToArray();
    }

    private static IReadOnlyList<ChapterNode> Insert(IReadOnlyList<ChapterNode> nodes, string? parent, string? after,
        ChapterNode node)
    {
        if (parent is not null)
        {
            return Transform(nodes, parent, n => n with { Children = Insert(n.Children, null, after, node) });
        }

        List<ChapterNode> result = nodes.ToList();
        int index = after is null ? result.Count : result.FindIndex(n => n.ChapterId == after) + 1;
        result.Insert(index, node);
        return result;
    }
}

public sealed partial class ChapterScanRowViewModel : ViewModelBase
{
    private readonly ChapterSidebarViewModel _owner;

    public ChapterScanRowViewModel(ChapterSidebarViewModel owner, ChapterNode node, int depth, int headingLevel,
        bool included, string diagnostic)
    {
        _owner = owner;
        ChapterId = node.ChapterId;
        Title = node.Title;
        Target = node.Target;
        Depth = depth;
        HeadingLevel = headingLevel;
        IsIncluded = included;
        Diagnostic = diagnostic;
        EditCommand = new AsyncCommand(() => owner.EditPreviewAsync(this));
        NavigateCommand = new AsyncCommand(() => owner.NavigatePreviewAsync(this));
    }

    public string ChapterId { get; }
    public int HeadingLevel { get; }
    public string Diagnostic { get; }

    [ExcludeFromDerivedGeneration] public string SourceText => $"PDF 第 {Target.PdfPage} 页 · H{HeadingLevel}";

    [ExcludeFromDerivedGeneration] public Thickness Indent => new(Math.Min(Depth, 8) * 12, 0, 0, 0);
    [ObservableProperty] public partial string Title { get; set; }
    [ObservableProperty] public partial NavigationPosition Target { get; set; }
    [ObservableProperty] public partial bool IsIncluded { get; set; }
    [ObservableProperty] public partial int Depth { get; set; }

    partial void OnDepthChanged(int value)
    {
        Raise(nameof(Indent));
        Raise(nameof(SourceText));
        _owner.RefreshSelectedPreview();
    }

    partial void OnTargetChanged(NavigationPosition value)
    {
        Raise(nameof(SourceText));
        _owner.RefreshSelectedPreview();
    }

    partial void OnTitleChanged(string value)
    {
        _owner.RefreshSelectedPreview();
    }

    partial void OnIsIncludedChanged(bool value)
    {
        _owner.RefreshSelectedPreview();
    }

    public AsyncCommand EditCommand { get; }
    public AsyncCommand NavigateCommand { get; }
}

public sealed record ChapterPreviewTreeRow(string Title, string Position, Thickness Indent);
