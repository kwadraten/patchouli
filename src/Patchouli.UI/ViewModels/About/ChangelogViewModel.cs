using LiveMarkdown.Avalonia;

namespace Patchouli.UI.ViewModels;

public sealed class ChangelogViewModel : ViewModelBase
{
    public ChangelogViewModel()
    {
        using Stream stream = typeof(ChangelogViewModel).Assembly
                                  .GetManifestResourceStream("Patchouli.UI.Changelog.md")
                              ?? throw new InvalidOperationException("未找到内嵌更新日志资源。");
        using StreamReader reader = new(stream);
        Markdown = reader.ReadToEnd();
        MarkdownBuilder.Append(Markdown);
    }

    public string Markdown { get; }
    public ObservableStringBuilder MarkdownBuilder { get; } = new();
}
