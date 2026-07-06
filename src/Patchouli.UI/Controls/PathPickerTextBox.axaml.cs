using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Media;

namespace Patchouli.UI.Controls;

public enum PathPickerMode
{
    OpenFile,
    SaveFile,
    Folder
}

public sealed partial class PathPickerTextBox : UserControl
{
    public static readonly StyledProperty<string?> PathProperty =
        AvaloniaProperty.Register<PathPickerTextBox, string?>(nameof(Path), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<string?> PlaceholderTextProperty =
        AvaloniaProperty.Register<PathPickerTextBox, string?>(nameof(PlaceholderText));

    public static readonly StyledProperty<PathPickerMode> PickerModeProperty =
        AvaloniaProperty.Register<PathPickerTextBox, PathPickerMode>(nameof(PickerMode), PathPickerMode.OpenFile);

    public static readonly StyledProperty<string?> DialogTitleProperty =
        AvaloniaProperty.Register<PathPickerTextBox, string?>(nameof(DialogTitle));

    public static readonly StyledProperty<string?> FileFilterNameProperty =
        AvaloniaProperty.Register<PathPickerTextBox, string?>(nameof(FileFilterName));

    public static readonly StyledProperty<string?> FileFilterPatternsProperty =
        AvaloniaProperty.Register<PathPickerTextBox, string?>(nameof(FileFilterPatterns));

    public string? Path
    {
        get => GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    public string? PlaceholderText
    {
        get => GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public PathPickerMode PickerMode
    {
        get => GetValue(PickerModeProperty);
        set => SetValue(PickerModeProperty, value);
    }

    public string? DialogTitle
    {
        get => GetValue(DialogTitleProperty);
        set => SetValue(DialogTitleProperty, value);
    }

    public string? FileFilterName
    {
        get => GetValue(FileFilterNameProperty);
        set => SetValue(FileFilterNameProperty, value);
    }

    public string? FileFilterPatterns
    {
        get => GetValue(FileFilterPatternsProperty);
        set => SetValue(FileFilterPatternsProperty, value);
    }

    public static readonly StyledProperty<StreamGeometry?> IconDataProperty =
        AvaloniaProperty.Register<PathPickerTextBox, StreamGeometry?>(nameof(IconData));

    public StreamGeometry? IconData
    {
        get => GetValue(IconDataProperty);
        private set => SetValue(IconDataProperty, value);
    }

    private static readonly StreamGeometry _folderIcon = StreamGeometry.Parse("M10 4H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z");
    private static readonly StreamGeometry _fileIcon = StreamGeometry.Parse("M14 2H6c-1.1 0-1.99.9-1.99 2L4 20c0 1.1.89 2 1.99 2H18c1.1 0 2-.9 2-2V8l-6-6zm2 16H8V4h5v5h5v11z");

    public PathPickerTextBox()
    {
        InitializeComponent();
        IconData = _fileIcon;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PickerModeProperty)
        {
            IconData = PickerMode == PathPickerMode.Folder ? _folderIcon : _fileIcon;
        }
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        if (PickerMode == PathPickerMode.Folder)
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = DialogTitle ?? "选择文件夹",
                AllowMultiple = false
            });

            if (folders.Count > 0 && folders[0].Path.LocalPath is { Length: > 0 } path)
            {
                Path = path;
            }
        }
        else if (PickerMode == PathPickerMode.OpenFile)
        {
            var options = new FilePickerOpenOptions
            {
                Title = DialogTitle ?? "选择文件",
                AllowMultiple = false
            };
            
            if (!string.IsNullOrWhiteSpace(FileFilterName) && !string.IsNullOrWhiteSpace(FileFilterPatterns))
            {
                options.FileTypeFilter = new[]
                {
                    new FilePickerFileType(FileFilterName) { Patterns = FileFilterPatterns.Split(',').Select(x => x.Trim()).ToArray() },
                    FilePickerFileTypes.All
                };
            }

            var files = await storage.OpenFilePickerAsync(options);
            if (files.Count > 0 && files[0].Path.LocalPath is { Length: > 0 } path)
            {
                Path = path;
            }
        }
        else if (PickerMode == PathPickerMode.SaveFile)
        {
            var options = new FilePickerSaveOptions
            {
                Title = DialogTitle ?? "保存文件"
            };

            if (!string.IsNullOrWhiteSpace(FileFilterName) && !string.IsNullOrWhiteSpace(FileFilterPatterns))
            {
                var patterns = FileFilterPatterns.Split(',').Select(x => x.Trim()).ToArray();
                options.FileTypeChoices = new[]
                {
                    new FilePickerFileType(FileFilterName) { Patterns = patterns },
                    FilePickerFileTypes.All
                };
                if (patterns.Length > 0 && patterns[0].StartsWith("*."))
                {
                    options.DefaultExtension = patterns[0].Substring(2);
                }
            }

            var file = await storage.SaveFilePickerAsync(options);
            if (file?.Path.LocalPath is { Length: > 0 } path)
            {
                Path = path;
            }
        }
    }
}
