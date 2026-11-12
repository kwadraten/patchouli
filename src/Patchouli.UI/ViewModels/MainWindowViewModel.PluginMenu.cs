using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Mcp;

namespace Patchouli.UI.ViewModels;

/// <summary>
/// The 扩展功能 top-level menu: a fixed 插件与扩展管理 entry followed by one flat row per
/// declared independent plugin view, rebuilt on every submenu open — plugins can be installed,
/// enabled, disabled or removed between opens, so the rows are enumerated fresh from
/// <see cref="IMcpPluginApi"/> each time. Only tab/window views appear here; pdf-sidebar views
/// belong to the PDF workspace sidebar's own menu. With no eligible view the menu shows only the
/// management entry — no placeholder row.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private int _extensionMenuEpoch;

    /// <summary>
    /// Rows of the 扩展功能 submenu: the management <see cref="ExtensionMenuEntryViewModel"/>, an
    /// optional <see cref="Separator"/> control and one flat view row per eligible plugin view.
    /// The collection holds <see cref="object"/> because the separator is a bare control, not a
    /// view-model row.
    /// </summary>
    [ExcludeFromDerivedGeneration]
    public ObservableCollection<object> ExtensionMenuEntries { get; } = new();

    /// <summary>
    /// The flat view rows inside <see cref="ExtensionMenuEntries"/> — kept as a typed projection
    /// so hosts and tests can inspect the declared views without unwrapping the mixed collection.
    /// </summary>
    [ExcludeFromDerivedGeneration]
    public ObservableCollection<ExtensionMenuEntryViewModel> PluginViewMenuEntries { get; } = new();

    /// <summary>True while at least one mounted plugin declares a menu-eligible view.</summary>
    [ObservableProperty]
    public partial bool HasPluginViewMenuEntries { get; private set; }

    /// <summary>
    /// Seeds the submenu with the fixed management row. The collection starts empty, and a MenuItem
    /// whose ItemsSource yields nothing never opens its popup — so the management entry must exist
    /// before the first submenu-open refresh completes.
    /// </summary>
    private void SeedExtensionMenuEntries()
    {
        ExtensionMenuEntries.Add(ExtensionMenuEntryViewModel.Command("插件与扩展管理",
            OpenExtensionManagerCommand));
    }

    /// <summary>
    /// Rebuilds the 扩展功能 menu from every enabled mount's declared views. A mount whose start
    /// failed (state "failed") or whose device grant lacks the view verb can never serve a view,
    /// so it contributes no rows — clicking such a row could only produce the error
    /// <see cref="OpenPluginViewAsync"/> reports anyway. Only the latest refresh may apply: a
    /// slower overlapping read never overwrites a newer one's rows.
    /// </summary>
    public async Task RefreshExtensionMenuAsync()
    {
        int epoch = ++_extensionMenuEpoch;
        IMcpPluginApi plugins;
        try
        {
            plugins = await PluginsAsync();
        }
        catch (OperationCanceledException)
        {
            return;
        }

        List<PluginViewMenuSource> sources = [];
        foreach (McpPluginMountInfo mount in plugins.Mounts)
        {
            if (string.Equals(mount.State, "failed", StringComparison.Ordinal) ||
                !plugins.AllowsVerb(mount.PluginId, PluginViewSession.RequiredVerb))
            {
                continue;
            }

            List<McpPluginViewInfo> views = plugins.Views(mount.PluginId)
                .Where(view => view.Placement is PluginViewPlacementTab or PluginViewPlacementWindow)
                .ToList();
            if (views.Count > 0)
            {
                sources.Add(new PluginViewMenuSource(mount.PluginId, mount.Name, views));
            }
        }

        if (epoch != _extensionMenuEpoch)
        {
            return;
        }

        ExtensionMenuEntries.Clear();
        PluginViewMenuEntries.Clear();
        ExtensionMenuEntries.Add(ExtensionMenuEntryViewModel.Command("插件与扩展管理",
            OpenExtensionManagerCommand));
        if (sources.Count > 0)
        {
            ExtensionMenuEntries.Add(new Separator());
            bool multiplePlugins = sources.Count > 1;
            foreach (PluginViewMenuSource source in sources)
            {
                foreach (McpPluginViewInfo view in source.Views)
                {
                    string header = multiplePlugins
                        ? $"{source.DisplayName} · {view.Title}"
                        : view.Title;
                    ExtensionMenuEntryViewModel row = ExtensionMenuEntryViewModel.View(
                        source.PluginId, view, header, OpenPluginViewAsync);
                    PluginViewMenuEntries.Add(row);
                    ExtensionMenuEntries.Add(row);
                }
            }
        }

        HasPluginViewMenuEntries = sources.Count > 0;
    }

    /// <summary>One mount's contribution: its display name plus its menu-eligible views.</summary>
    private sealed record PluginViewMenuSource(
        string PluginId,
        string? Name,
        IReadOnlyList<McpPluginViewInfo> Views)
    {
        /// <summary>The plugin's manifest name, falling back to the id when the name is blank.</summary>
        public string DisplayName =>
            string.IsNullOrWhiteSpace(Name) ? PluginId : Name;
    }
}
