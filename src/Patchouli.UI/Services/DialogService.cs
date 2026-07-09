using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Patchouli.UI.Services;

public class DialogService : IDialogService
{
    private readonly Dictionary<Type, Type> _mappings = new();

    public void Register<TViewModel, TView>() where TView : Window
    {
        _mappings[typeof(TViewModel)] = typeof(TView);
    }

    private Window? GetMainWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }
        return null;
    }

    public async Task ShowDialogAsync(object viewModel)
    {
        var mainWindow = GetMainWindow();
        if (mainWindow == null) return;

        var viewType = _mappings[viewModel.GetType()];
        var dialog = (Window)Activator.CreateInstance(viewType)!;
        dialog.DataContext = viewModel;

        await dialog.ShowDialog(mainWindow);
    }

    public async Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
    {
        var mainWindow = GetMainWindow();
        if (mainWindow == null) return default;

        var viewType = _mappings[viewModel.GetType()];
        var dialog = (Window)Activator.CreateInstance(viewType)!;
        dialog.DataContext = viewModel;

        return await dialog.ShowDialog<TResult>(mainWindow);
    }
}
