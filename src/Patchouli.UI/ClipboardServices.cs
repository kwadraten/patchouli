using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace Patchouli.UI;

public interface IClipboardService
{
    Task SetTextAsync(string text);
    Task<string?> GetTextAsync();
}

public interface IFilePickerService
{
    Task<string?> OpenFileAsync(string title, string filterName, IReadOnlyList<string> patterns);

    Task<string?> SaveFileAsync(string title, string suggestedFileName, string filterName,
        IReadOnlyList<string> patterns);
}

public sealed class AvaloniaClipboardService : IClipboardService
{
    public async Task SetTextAsync(string text)
    {
        IClipboard? clipboard = GetClipboard();
        if (clipboard is null)
        {
            throw new InvalidOperationException("Clipboard is unavailable on this platform.");
        }

        await clipboard.SetTextAsync(text);
    }

    public async Task<string?> GetTextAsync()
    {
        IClipboard? clipboard = GetClipboard();
        if (clipboard is null)
        {
            return null;
        }

        return await clipboard.TryGetTextAsync();
    }

    private static IClipboard? GetClipboard()
    {
        return Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow?.Clipboard
            : null;
    }
}

public sealed class AvaloniaFilePickerService : IFilePickerService
{
    public async Task<string?> OpenFileAsync(string title, string filterName, IReadOnlyList<string> patterns)
    {
        IStorageProvider? storage = GetStorage();
        if (storage is null)
        {
            return null;
        }

        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(filterName) { Patterns = patterns.ToArray() },
                FilePickerFileTypes.All
            ]
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedFileName, string filterName,
        IReadOnlyList<string> patterns)
    {
        IStorageProvider? storage = GetStorage();
        if (storage is null)
        {
            return null;
        }

        IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = patterns.FirstOrDefault()?.TrimStart('*', '.') ?? "bib",
            FileTypeChoices =
            [
                new FilePickerFileType(filterName) { Patterns = patterns.ToArray() },
                FilePickerFileTypes.All
            ]
        });
        return file?.TryGetLocalPath();
    }

    private static IStorageProvider? GetStorage()
    {
        return Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow?.StorageProvider
            : null;
    }
}
