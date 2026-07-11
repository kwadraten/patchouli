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
        Window? mainWindow = GetMainWindow();
        if (mainWindow == null)
        {
            return;
        }

        Type viewType = _mappings[viewModel.GetType()];
        Window dialog = (Window)Activator.CreateInstance(viewType)!;
        dialog.DataContext = viewModel;
        Control? mainContent = mainWindow.Content as Control;
        bool wasEnabled = mainContent?.IsEnabled ?? true;
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        EventHandler? closed = null;
        closed = (_, _) =>
        {
            dialog.Closed -= closed;
            if (mainContent is not null)
            {
                mainContent.IsEnabled = wasEnabled;
            }

            completion.TrySetResult();
        };

        dialog.Closed += closed;
        if (mainContent is not null)
        {
            mainContent.IsEnabled = false;
        }

        try
        {
            dialog.Show(mainWindow);
            dialog.Activate();
            await completion.Task;
        }
        catch
        {
            dialog.Closed -= closed;
            if (mainContent is not null)
            {
                mainContent.IsEnabled = wasEnabled;
            }

            throw;
        }
    }

    public async Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
    {
        Window? mainWindow = GetMainWindow();
        if (mainWindow == null)
        {
            return default;
        }

        Type viewType = _mappings[viewModel.GetType()];
        Window dialog = (Window)Activator.CreateInstance(viewType)!;
        dialog.DataContext = viewModel;

        return await dialog.ShowDialog<TResult>(mainWindow);
    }
}
