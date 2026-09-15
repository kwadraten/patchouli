using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.UI.ViewModels.Core;

namespace Patchouli.UI.ViewModels;

public sealed partial class WorkspaceLayoutViewModel : ViewModelBase
{
    [ObservableProperty] public partial WorkspaceTabViewModel? ActiveTab { get; set; }

    [ObservableProperty] public partial bool ShowInspectorPane { get; set; } = true;

    [ObservableProperty] public partial bool HasPdfWorkspaceTab { get; private set; }
    [ObservableProperty] public partial bool HasSettingsTab { get; private set; }
    [ObservableProperty] public partial bool HasItemEditorTab { get; private set; }

    public WorkspaceLayoutViewModel()
        : this(null)
    {
    }

    internal WorkspaceLayoutViewModel(IScheduler? uiScheduler)
    {
        IScheduler actualUiScheduler = uiScheduler ?? (SynchronizationContext.Current != null
            ? (IScheduler)new SynchronizationContextScheduler(SynchronizationContext.Current)
            : ImmediateScheduler.Instance);

        IObservable<EventPattern<NotifyCollectionChangedEventArgs>> tabsChanged =
            Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                h => Tabs.CollectionChanged += h,
                h => Tabs.CollectionChanged -= h);

        tabsChanged.Select(_ => Tabs.Any(tab => tab.Kind == WorkspaceTabKind.PdfWorkspace))
            .BindOutput(this, v => HasPdfWorkspaceTab = v, actualUiScheduler, null, true,
                Tabs.Any(tab => tab.Kind == WorkspaceTabKind.PdfWorkspace));

        tabsChanged.Select(_ => Tabs.Any(tab => tab.Kind == WorkspaceTabKind.Settings))
            .BindOutput(this, v => HasSettingsTab = v, actualUiScheduler, null, true,
                Tabs.Any(tab => tab.Kind == WorkspaceTabKind.Settings));

        tabsChanged.Select(_ => Tabs.Any(tab => tab.Kind == WorkspaceTabKind.ItemEditor))
            .BindOutput(this, v => HasItemEditorTab = v, actualUiScheduler, null, true,
                Tabs.Any(tab => tab.Kind == WorkspaceTabKind.ItemEditor));
    }

    public ObservableCollection<WorkspaceTabViewModel> Tabs { get; } = new();

    public bool ShowSidebar => ActiveTab?.Kind == WorkspaceTabKind.Library;
    public bool IsInspectorVisible => ActiveTab?.Kind == WorkspaceTabKind.Library && ShowInspectorPane;
    public bool IsLibraryActive => ActiveTab?.Kind == WorkspaceTabKind.Library;
    public bool IsReaderActive => ActiveTab?.Kind == WorkspaceTabKind.PdfWorkspace;
    public bool IsSettingsActive => ActiveTab?.Kind == WorkspaceTabKind.Settings;
    public bool IsItemEditorActive => ActiveTab?.Kind == WorkspaceTabKind.ItemEditor;
}
