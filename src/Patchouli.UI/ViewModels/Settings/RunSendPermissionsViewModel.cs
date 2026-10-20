using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Mcp;
using Patchouli.UI.ViewModels.Core;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>The parent is a selection aggregate, never a persisted permission or inheritance rule.</summary>
public sealed class RunSendPermissionsViewModel : ViewModelBase
{
    public RunSendPermissionsViewModel(IEnumerable<string> domains, McpServerSettings settings,
        Action<string, bool> update)
    {
        Children = domains.Select(domain => new RunSendPermissionViewModel(domain,
            settings.DomainPermissions.Any(value => value.Domain == domain && value.Verb == "send" && value.Enabled) &&
            !settings.DomainPermissions.Any(value => value.Domain == domain && value.Verb == "send" && !value.Enabled),
            enabled =>
            {
                update(domain, enabled);
                Raise(nameof(Checked));
            })).ToArray();
        ToggleCommand = new RelayCommand(_ =>
        {
            bool enabled = Checked != true;
            foreach (RunSendPermissionViewModel child in Children)
            {
                child.Enabled = enabled;
            }
        });
    }

    public IReadOnlyList<RunSendPermissionViewModel> Children { get; }

    [ExcludeFromDerivedGeneration]
    public bool? Checked => Children.Count == 0 || Children.All(value => !value.Enabled)
        ? false
        : Children.All(value => value.Enabled)
            ? true
            : null;

    public RelayCommand ToggleCommand { get; }
}

public sealed partial class RunSendPermissionViewModel : ViewModelBase
{
    private readonly Action<bool> _update;

    public RunSendPermissionViewModel(string domain, bool enabled, Action<bool> update)
    {
        Domain = domain;
        _update = _ => { };
        Enabled = enabled;
        _update = update;
    }

    public string Domain { get; }

    [ObservableProperty] public partial bool Enabled { get; set; }

    partial void OnEnabledChanged(bool value)
    {
        _update(value);
    }
}
