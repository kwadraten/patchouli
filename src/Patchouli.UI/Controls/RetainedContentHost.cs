using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Patchouli.UI.Controls;

/// <summary>Retains a lazily created control for each selected item identity.</summary>
public class RetainedContentHost : Panel
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<RetainedContentHost, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<RetainedContentHost, object?>(nameof(SelectedItem));

    public static readonly StyledProperty<IDataTemplate?> ContentTemplateProperty =
        AvaloniaProperty.Register<RetainedContentHost, IDataTemplate?>(nameof(ContentTemplate));

    private readonly RetainedContentHostBehavior _behavior;

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public IDataTemplate? ContentTemplate
    {
        get => GetValue(ContentTemplateProperty);
        set => SetValue(ContentTemplateProperty, value);
    }

    public RetainedContentHost()
    {
        _behavior = new RetainedContentHostBehavior(
            this,
            () => ItemsSource,
            () => SelectedItem,
            ResolveContent,
            () => ContentTemplate);
    }

    static RetainedContentHost()
    {
        AffectsMeasure<RetainedContentHost>(ItemsSourceProperty, SelectedItemProperty, ContentTemplateProperty);
        AffectsArrange<RetainedContentHost>(ItemsSourceProperty, SelectedItemProperty, ContentTemplateProperty);
    }

    /// <summary>Resolves the content whose data context is assigned to a realized page.</summary>
    protected virtual object? ResolveContent(object item)
    {
        return item;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsSourceProperty || change.Property == SelectedItemProperty ||
            change.Property == ContentTemplateProperty)
        {
            _behavior.Refresh();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _behavior.Attach();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _behavior.Detach();
        base.OnDetachedFromVisualTree(e);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return _behavior.Measure(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        return _behavior.Arrange(finalSize);
    }
}

/// <summary>Shared retained-page ownership and lifecycle logic for typed host surfaces.</summary>
internal sealed class RetainedContentHostBehavior
{
    private readonly Panel _owner;
    private readonly Func<IEnumerable?> _itemsSource;
    private readonly Func<object?> _selectedItem;
    private readonly Func<object, object?> _resolveContent;
    private readonly Func<IDataTemplate?> _contentTemplate;
    private readonly Dictionary<object, Control> _pages = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object> _buildingItems = new(ReferenceEqualityComparer.Instance);
    private INotifyCollectionChanged? _observedCollection;
    private bool _collectionSubscribed;
    private bool _isAttached = true;
    private bool _isUpdatingActive;
    private bool _activeUpdateRequested;
    private bool _isReleasingAll;
    private object? _activePageItem;

    public RetainedContentHostBehavior(
        Panel owner,
        Func<IEnumerable?> itemsSource,
        Func<object?> selectedItem,
        Func<object, object?> resolveContent,
        Func<IDataTemplate?> contentTemplate)
    {
        _owner = owner;
        _itemsSource = itemsSource;
        _selectedItem = selectedItem;
        _resolveContent = resolveContent;
        _contentTemplate = contentTemplate;
    }

    public void Attach()
    {
        _isAttached = true;
        Refresh();
    }

    public void Detach()
    {
        _isAttached = false;
        ObserveCollection(null);
        ReleaseAllPages();
    }

    public void Refresh()
    {
        ObserveCollection(_itemsSource() as INotifyCollectionChanged);
        ReconcilePages();
        if (_isAttached && TopLevel.GetTopLevel(_owner) is not null)
        {
            EnsureSelectedPage();
        }
    }

    public Size Measure(Size availableSize)
    {
        EnsureSelectedPage();
        Size result = default;
        foreach (Control child in _owner.Children)
        {
            if (child.IsVisible)
            {
                child.Measure(availableSize);
                result = child.DesiredSize;
            }
        }

        return result;
    }

    public Size Arrange(Size finalSize)
    {
        EnsureSelectedPage();
        foreach (Control child in _owner.Children)
        {
            if (child.IsVisible)
            {
                child.Arrange(new Rect(finalSize));
            }
        }

        return finalSize;
    }

    private void ObserveCollection(INotifyCollectionChanged? collection)
    {
        if (!ReferenceEquals(_observedCollection, collection))
        {
            if (_collectionSubscribed && _observedCollection is not null)
            {
                _observedCollection.CollectionChanged -= OnCollectionChanged;
                _collectionSubscribed = false;
            }

            _observedCollection = collection;
        }

        if (_isAttached && !_collectionSubscribed && _observedCollection is not null)
        {
            _observedCollection.CollectionChanged += OnCollectionChanged;
            _collectionSubscribed = true;
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ReconcilePages();
    }

    private void ReconcilePages()
    {
        HashSet<object> current = new(EnumerateItems(), ReferenceEqualityComparer.Instance);
        foreach (object item in _pages.Keys.Where(item => !current.Contains(item)).ToArray())
        {
            ReleasePage(item);
        }

        UpdateActivePage();
        _owner.InvalidateMeasure();
    }

    private IEnumerable<object> EnumerateItems()
    {
        if (_itemsSource() is not { } source)
        {
            return Enumerable.Empty<object>();
        }

        HashSet<object> seen = new(ReferenceEqualityComparer.Instance);
        List<object> items = [];
        foreach (object? item in source)
        {
            if (item is not null && seen.Add(item))
            {
                items.Add(item);
            }
        }

        return items;
    }

    private void EnsureSelectedPage()
    {
        object? item = _selectedItem();
        if (item is null || !EnumerateItems().Any(candidate => ReferenceEquals(candidate, item)))
        {
            UpdateActivePage();
            return;
        }

        if (_pages.ContainsKey(item))
        {
            UpdateActivePage();
            return;
        }

        object? content = _resolveContent(item);
        if (content is null || _owner.FindDataTemplate(content, _contentTemplate()) is not { } template)
        {
            return;
        }

        if (!_buildingItems.Add(item))
        {
            return;
        }

        Control? page;
        try
        {
            page = template.Build(content);
        }
        finally
        {
            _buildingItems.Remove(item);
        }

        if (page is null)
        {
            return;
        }

        page.DataContext = content;
        page.IsVisible = false;
        if (!ReferenceEquals(_selectedItem(), item) ||
            !EnumerateItems().Any(candidate => ReferenceEquals(candidate, item)))
        {
            if (page is IWorkspaceTabPage closeable)
            {
                closeable.OnTabClosed();
            }

            page.DataContext = null;
            return;
        }

        _pages.Add(item, page);
        _owner.Children.Add(page);
        UpdateActivePage();
    }

    private void UpdateActivePage()
    {
        if (_isUpdatingActive)
        {
            _activeUpdateRequested = true;
            return;
        }

        _isUpdatingActive = true;
        try
        {
            do
            {
                _activeUpdateRequested = false;
                object? active = _selectedItem();
                bool isValidActive = !_isReleasingAll && active is not null &&
                                     EnumerateItems().Any(candidate => ReferenceEquals(candidate, active));
                object? nextActive = isValidActive && active is not null && _pages.ContainsKey(active)
                    ? active
                    : null;
                object? previousActive = _activePageItem;
                KeyValuePair<object, Control>[] pages = _pages.ToArray();

                _activePageItem = nextActive;
                foreach ((object item, Control page) in pages)
                {
                    page.IsVisible = ReferenceEquals(item, nextActive);
                }

                if (previousActive is not null && !ReferenceEquals(previousActive, nextActive) &&
                    pages.FirstOrDefault(pair => ReferenceEquals(pair.Key, previousActive)).Value
                        is IWorkspaceTabPage previousLifecycle)
                {
                    previousLifecycle.OnTabDeactivated();
                }

                if (!_activeUpdateRequested && nextActive is not null && !ReferenceEquals(previousActive, nextActive) &&
                    pages.FirstOrDefault(pair => ReferenceEquals(pair.Key, nextActive)).Value
                        is IWorkspaceTabPage nextLifecycle)
                {
                    nextLifecycle.OnTabActivated();
                }
            } while (_activeUpdateRequested);
        }
        finally
        {
            _isUpdatingActive = false;
        }
    }

    private void ReleasePage(object item)
    {
        if (!_pages.Remove(item, out Control? page) || page is null)
        {
            return;
        }

        if (ReferenceEquals(_activePageItem, item))
        {
            _activePageItem = null;
            if (page is IWorkspaceTabPage lifecycle)
            {
                lifecycle.OnTabDeactivated();
            }
        }

        page.IsVisible = false;
        if (page is IWorkspaceTabPage closeable)
        {
            closeable.OnTabClosed();
        }

        _owner.Children.Remove(page);
        page.DataContext = null;
    }

    private void ReleaseAllPages()
    {
        _isReleasingAll = true;
        try
        {
            foreach (object item in _pages.Keys.ToArray())
            {
                ReleasePage(item);
            }
        }
        finally
        {
            _isReleasingAll = false;
        }
    }
}
