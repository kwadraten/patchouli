using Avalonia;
using Avalonia.Controls;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.Controls;

/// <summary>Retains one independently realized page for each open workspace tab.</summary>
public sealed class WorkspaceTabContentHost : Panel
{
    public static readonly StyledProperty<IEnumerable<WorkspaceTabViewModel>?> ItemsSourceProperty =
        AvaloniaProperty.Register<WorkspaceTabContentHost, IEnumerable<WorkspaceTabViewModel>?>(nameof(ItemsSource));

    public static readonly StyledProperty<WorkspaceTabViewModel?> ActiveTabProperty =
        AvaloniaProperty.Register<WorkspaceTabContentHost, WorkspaceTabViewModel?>(nameof(ActiveTab));

    private readonly RetainedContentHostBehavior _behavior;

    public IEnumerable<WorkspaceTabViewModel>? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public WorkspaceTabViewModel? ActiveTab
    {
        get => GetValue(ActiveTabProperty);
        set => SetValue(ActiveTabProperty, value);
    }

    public WorkspaceTabContentHost()
    {
        _behavior = new RetainedContentHostBehavior(
            this,
            () => ItemsSource,
            () => ActiveTab,
            tab => ((WorkspaceTabViewModel)tab).Content,
            () => null);
    }

    static WorkspaceTabContentHost()
    {
        AffectsMeasure<WorkspaceTabContentHost>(ItemsSourceProperty, ActiveTabProperty);
        AffectsArrange<WorkspaceTabContentHost>(ItemsSourceProperty, ActiveTabProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsSourceProperty || change.Property == ActiveTabProperty)
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
