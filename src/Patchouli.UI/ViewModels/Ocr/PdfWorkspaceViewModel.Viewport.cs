using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;

namespace Patchouli.UI.ViewModels;

/// <summary>How the PDF page viewport chooses its zoom.</summary>
public enum PdfViewportFitMode
{
    /// <summary>User-controlled zoom (toolbar buttons, Ctrl+wheel).</summary>
    Free,

    /// <summary>Zoom so the whole page height fits the viewport.</summary>
    FitHeight,

    /// <summary>Zoom so the whole page width fits the viewport.</summary>
    FitWidth
}

/// <summary>
/// Viewport UX for the PDF workspace: fit modes (free/fit-height/fit-width), display rotation,
/// bounding-box visibility, and the sidebar hover reveal. The page content (image, boxes and all
/// overlays) stays in unrotated page-pixel coordinates; rotation and zoom are pure view
/// transforms, so existing pointer math keeps working through the layout transform.
/// </summary>
public sealed partial class PdfWorkspaceViewModel
{
    // DIPs of empty chrome around the page inside the scroller (page margin + border + shadow).
    // Fit modes leave this much breathing room so the fitted page never touches the viewport edge.
    private const double ViewportFitChromeDip = 48;

    // Viewport extent in DIPs, reported by the view's ScrollViewer. Zero means "not known yet":
    // fit is skipped until the first real size arrives, then applied (converges after attach).
    private double _viewportWidth;
    private double _viewportHeight;
    private bool _viewportWired;
    private bool _applyingFitZoom;
    private PdfBBoxViewModel? _hoverRevealBox;

    /// <summary>The active fit mode. Defaults to fit-height so a freshly opened page is whole.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FitModeText))]
    public partial PdfViewportFitMode FitMode { get; private set; } = PdfViewportFitMode.FitHeight;

    partial void OnFitModeChanged(PdfViewportFitMode value)
    {
        ApplyFitZoom();
    }

    /// <summary>Short label for the toolbar size button.</summary>
    [ExcludeFromDerivedGeneration]
    public string FitModeText => FitMode switch
    {
        PdfViewportFitMode.FitHeight => "适应高度",
        PdfViewportFitMode.FitWidth => "适应宽度",
        _ => "自由"
    };

    /// <summary>Clockwise display rotation in degrees (0/90/180/270). Bound to the page
    /// transform; the content coordinate space itself never rotates.</summary>
    [ObservableProperty]
    public partial double ViewportRotationAngle { get; private set; }

    private bool IsViewportQuarterTurn => ViewportRotationAngle is 90 or 270;

    /// <summary>Bounding-box layer visibility (writable; defaults to shown).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreBoundingBoxesVisible))]
    [NotifyPropertyChangedFor(nameof(IsHoverBoxVisible))]
    public partial bool ShowBoundingBoxes { get; set; } = true;

    /// <summary>Combined visibility for the canvas box layer and its derived overlays
    /// (boxes, continuation links, overlap and cross-page markers).</summary>
    [ExcludeFromDerivedGeneration]
    public bool AreBoundingBoxesVisible => HasImage && ShowBoundingBoxes;

    /// <summary>True while a sidebar-hover box should be drawn over the page: boxes are hidden
    /// and a hovered sidebar row/reading block maps to a box on the current page.</summary>
    [ExcludeFromDerivedGeneration]
    public bool IsHoverBoxVisible => HasImage && !ShowBoundingBoxes && _hoverRevealBox is not null;

    /// <summary>Hover-reveal box placement in (unrotated) page-pixel coordinates. The XAML may
    /// bind <see cref="HoverBoxMargin"/> on a left/top-aligned overlay, or Canvas.Left/Canvas.Top
    /// with <see cref="HoverBoxLeft"/>/<see cref="HoverBoxTop"/> inside a Canvas.</summary>
    [ExcludeFromDerivedGeneration]
    public Thickness HoverBoxMargin => new(_hoverRevealBox?.Left ?? 0, _hoverRevealBox?.Top ?? 0, 0, 0);

    [ExcludeFromDerivedGeneration] public double HoverBoxLeft => _hoverRevealBox?.Left ?? 0;

    [ExcludeFromDerivedGeneration] public double HoverBoxTop => _hoverRevealBox?.Top ?? 0;

    [ExcludeFromDerivedGeneration] public double HoverBoxWidth => _hoverRevealBox?.Width ?? 0;

    [ExcludeFromDerivedGeneration] public double HoverBoxHeight => _hoverRevealBox?.Height ?? 0;

    [ExcludeFromDerivedGeneration] public IBrush HoverBoxColor => _hoverRevealBox?.VisualBoxColor ?? Brushes.Gray;

    private RelayCommand? _cycleFitModeCommand;
    private AsyncCommand? _rotatePageCommand;

    // Optimistic rotation angles whose persistence write is still in flight. A page-list
    // refetch (ReloadAsync clears _pages, then RenderCurrentPageAsync reads pages back from
    // the database) can observe the pre-commit row; these entries overlay the stale read so
    // the viewport and OCR rotation capture never snap back to the old angle.
    private readonly Dictionary<PageId, int> _pendingRotations = [];

    /// <summary>Cycles the size button through Free → FitHeight → FitWidth.</summary>
    [ExcludeFromDerivedGeneration]
    public RelayCommand CycleFitModeCommand => _cycleFitModeCommand ??= new RelayCommand(_ =>
    {
        EnsureViewportWiring();
        FitMode = FitMode switch
        {
            PdfViewportFitMode.Free => PdfViewportFitMode.FitHeight,
            PdfViewportFitMode.FitHeight => PdfViewportFitMode.FitWidth,
            _ => PdfViewportFitMode.Free
        };
    });

    /// <summary>Rotates the displayed page 90° clockwise and persists the angle on the page.</summary>
    [ExcludeFromDerivedGeneration]
    public AsyncCommand RotatePageCommand => _rotatePageCommand ??= new AsyncCommand(RotateCurrentPageAsync);

    private async Task RotateCurrentPageAsync()
    {
        EnsureViewportWiring();
        int angle = ((int)ViewportRotationAngle + 90) % 360;
        if (_currentPageId is not { } pageId || PageIndex < 0 || PageIndex >= _pages.Count ||
            _pages[PageIndex].PageId != pageId)
        {
            // No page is loaded (headless surface): keep the view-only rotation.
            ViewportRotationAngle = angle;
            ApplyFitZoom();
            return;
        }

        int previous = _pages[PageIndex].UserRotation;
        ApplyUserRotation(pageId, angle);
        _pendingRotations[pageId] = angle;
        HostServices services = await _main.ServicesAsync();
        try
        {
            Result<Page> updated = await Task.Run(
                () => services.Pages.UpdateUserRotationAsync(pageId, angle),
                _workspaceCancellation.Token);
            if (updated.IsFailure)
            {
                ApplyUserRotation(pageId, previous);
                Status = $"页面旋转保存失败:{updated.ErrorMessage}";
                return;
            }

            ApplyUserRotation(pageId, updated.Value.UserRotation);
        }
        finally
        {
            _pendingRotations.Remove(pageId);
        }
    }

    private void ApplyUserRotation(PageId pageId, int angle)
    {
        _pages = _pages.Select(candidate => candidate.PageId == pageId
            ? candidate with { UserRotation = angle }
            : candidate).ToArray();
        if (_currentPageId == pageId)
        {
            ViewportRotationAngle = angle;
            ApplyFitZoom();
        }
    }

    /// <summary>
    /// Called by the view once it can report viewport sizes. Wires the self-observation that
    /// keeps the fit mode honest (image/page swaps and externally driven zoom changes) and
    /// applies the current fit. Idempotent; safe to call on every attach/DataContext change.
    /// </summary>
    public void InitializeViewport()
    {
        EnsureViewportWiring();
        ApplyFitZoom();
    }

    /// <summary>
    /// Entry point for "the rendered page changed" — called by the render pipeline after a
    /// page image/swap, and by this partial's own observation of Image/WidthPixels/HeightPixels.
    /// Re-validates the hover target and re-applies the fit for the new page size.
    /// </summary>
    public void OnViewportImageChanged()
    {
        EnsureViewportWiring();
        if (_hoverRevealBox is not null && !BoundingBoxes.Contains(_hoverRevealBox))
        {
            _hoverRevealBox = null;
            RaiseHoverBoxChanged();
        }

        Raise(nameof(AreBoundingBoxesVisible));
        Raise(nameof(IsHoverBoxVisible));
        ApplyFitZoom();
    }

    /// <summary>
    /// Entry point for "a manual zoom is about to be applied" (toolbar +/- or Ctrl+wheel):
    /// any manual zoom drops the fit mode back to Free. Also invoked indirectly — the
    /// viewport self-observation flips to Free on any Zoom change it did not apply itself.
    /// </summary>
    public void OnManualZoomChanging()
    {
        EnsureViewportWiring();
        FitMode = PdfViewportFitMode.Free;
    }

    /// <summary>Reports the ScrollViewer's viewport extent (DIPs) so fit modes can compute the
    /// zoom. Called by the view on viewport-size changes; repeat calls with the same size are
    /// cheap no-ops.</summary>
    public void UpdateViewportSize(double width, double height)
    {
        EnsureViewportWiring();
        width = Math.Max(0, width);
        height = Math.Max(0, height);
        if (Math.Abs(width - _viewportWidth) < 0.5 && Math.Abs(height - _viewportHeight) < 0.5)
        {
            return;
        }

        _viewportWidth = width;
        _viewportHeight = height;
        ApplyFitZoom();
    }

    /// <summary>Hover-reveal entry point for sidebar rows that carry a box view model.</summary>
    public void SetSidebarHoverBox(PdfBBoxViewModel? box)
    {
        EnsureViewportWiring();
        PdfBBoxViewModel? target = box is not null && BoundingBoxes.Contains(box) ? box : null;
        if (ReferenceEquals(_hoverRevealBox, target))
        {
            return;
        }

        _hoverRevealBox = target;
        RaiseHoverBoxChanged();
    }

    /// <summary>Hover-reveal entry point for sidebar surfaces that only know the box id
    /// (reading view blocks, search results). Unknown or other-page ids clear the reveal.</summary>
    public void SetSidebarHoverBoxId(DocumentBoxId? boxId)
    {
        SetSidebarHoverBox(boxId is { } id
            ? BoundingBoxes.FirstOrDefault(candidate => candidate.BoxId == id)
            : null);
    }

    // Self-observation keeps the viewport honest through the normal render/zoom entry points
    // even where the main view model does not call us explicitly: page swaps arrive as
    // Image/WidthPixels/HeightPixels changes, and any Zoom write this class did not perform
    // itself is by definition a manual zoom that cancels the fit.
    private void EnsureViewportWiring()
    {
        if (_viewportWired)
        {
            return;
        }

        _viewportWired = true;
        PropertyChanged += OnViewportPropertyChanged;
        BoundingBoxes.CollectionChanged += OnBoundingBoxesCollectionChanged;
        Register(System.Reactive.Disposables.Disposable.Create(() =>
        {
            PropertyChanged -= OnViewportPropertyChanged;
            BoundingBoxes.CollectionChanged -= OnBoundingBoxesCollectionChanged;
        }));
    }

    private void OnViewportPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Image):
            case nameof(WidthPixels):
            case nameof(HeightPixels):
                OnViewportImageChanged();
                break;
            case nameof(Zoom):
                if (!_applyingFitZoom && FitMode != PdfViewportFitMode.Free)
                {
                    FitMode = PdfViewportFitMode.Free;
                }

                break;
        }
    }

    private void OnBoundingBoxesCollectionChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_hoverRevealBox is not null && !BoundingBoxes.Contains(_hoverRevealBox))
        {
            _hoverRevealBox = null;
            RaiseHoverBoxChanged();
        }
    }

    private void ApplyFitZoom()
    {
        if (FitMode == PdfViewportFitMode.Free)
        {
            return;
        }

        double contentWidth = IsViewportQuarterTurn ? HeightPixels : WidthPixels;
        double contentHeight = IsViewportQuarterTurn ? WidthPixels : HeightPixels;
        double availableWidth = _viewportWidth - ViewportFitChromeDip;
        double availableHeight = _viewportHeight - ViewportFitChromeDip;
        if (contentWidth <= 0 || contentHeight <= 0 || availableWidth <= 0 || availableHeight <= 0)
        {
            return;
        }

        double fitZoom = FitMode == PdfViewportFitMode.FitWidth
            ? availableWidth / contentWidth
            : availableHeight / contentHeight;
        double zoom = Math.Clamp(fitZoom, MinZoom, MaxZoom);
        if (Math.Abs(zoom - Zoom) < 0.0005)
        {
            return;
        }

        _applyingFitZoom = true;
        try
        {
            Zoom = zoom;
        }
        finally
        {
            _applyingFitZoom = false;
        }
    }

    private void RaiseHoverBoxChanged()
    {
        Raise(nameof(IsHoverBoxVisible));
        Raise(nameof(HoverBoxMargin));
        Raise(nameof(HoverBoxLeft));
        Raise(nameof(HoverBoxTop));
        Raise(nameof(HoverBoxWidth));
        Raise(nameof(HoverBoxHeight));
        Raise(nameof(HoverBoxColor));
    }
}
