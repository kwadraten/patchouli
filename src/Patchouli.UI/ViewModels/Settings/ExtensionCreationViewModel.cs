using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Composition;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>Template selection creates a source draft, never an active implementation.</summary>
public sealed partial class ExtensionCreationViewModel(MainWindowViewModel main, ExtensionKind kind) : ViewModelBase
{
    public ObservableCollection<ExtensionListItem> Templates { get; } = [];
    [ObservableProperty] public partial ExtensionListItem? SelectedTemplate { get; set; }
    [ObservableProperty] public partial string ExtensionId { get; set; } = "";
    [ObservableProperty] public partial string Error { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }

    partial void OnSelectedTemplateChanged(ExtensionListItem? value)
    {
        if (value is not null)
        {
            ExtensionId = value.Id + ".custom";
        }
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            using ExtensionSettingsViewModel editor = main.Settings.CreateExtensionEditor();
            await editor.LoadAsync();
            foreach (ExtensionListItem template in editor.Extensions.Where(item => item.Kind == kind))
            {
                Templates.Add(template);
            }

            SelectedTemplate = Templates.FirstOrDefault();
            Error = Templates.Count == 0 ? "没有可用的基础扩展。" : "";
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, "extension-create", nameof(LoadAsync));
            Error = "无法读取扩展列表：" + exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<string?> CreateAsync()
    {
        if (IsBusy || SelectedTemplate is not { } template)
        {
            return null;
        }

        IsBusy = true;
        Error = "";
        try
        {
            HostServices services = await main.ServicesAsync();
            int libraryGeneration = main.LibraryGeneration;
            string id = ExtensionId.Trim();
            ExtensionPackageSnapshot copy = await Task.Run(() =>
                services.TranslationExtensions.CreateCustomAsync(template.Id, id));
            if (!main.IsCurrentLibraryContext(services, libraryGeneration))
            {
                Error = "书库已切换，请在原书库查看新建的扩展。";
                return null;
            }

            return copy.Id;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            Error = exception.Message;
            return null;
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, "extension-create", nameof(CreateAsync));
            Error = "创建失败：" + exception.Message;
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
