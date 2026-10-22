using System.Windows.Input;
using LiveMarkdown.Avalonia;
using Patchouli.UI.Diagnostics;
using Patchouli.UI.Services;

namespace Patchouli.UI.ViewModels;

public sealed class ChangelogViewModel : ViewModelBase
{
    private readonly LinkNavigator _links;
    private readonly RelayCommand _openLinkCommand;

    public ChangelogViewModel(LinkNavigator links)
    {
        _links = links;
        _openLinkCommand = new RelayCommand(OpenLink);
        using Stream stream = typeof(ChangelogViewModel).Assembly
                                  .GetManifestResourceStream("Patchouli.UI.Changelog.md")
                              ?? throw new InvalidOperationException("未找到内嵌更新日志资源。");
        using StreamReader reader = new(stream);
        Markdown = reader.ReadToEnd();
        MarkdownBuilder.Append(Markdown);
    }

    public string Markdown { get; }
    public ObservableStringBuilder MarkdownBuilder { get; } = new();

    /// <summary>
    ///     Issue #21: bound to <c>MarkdownRenderer.LinkCommand</c>. Every rendered link click
    ///     arrives here with the actual target <see cref="LinkClickedEventArgs.HRef" /> and is
    ///     handed to the shared <see cref="LinkNavigator" />, which applies the protocol allowlist
    ///     and the external-link confirmation. The page deliberately leaves
    ///     <c>MarkdownRenderer.LinkContextMenu</c> unset: the component ships no default menu, so
    ///     right-clicking a link shows nothing and no path can bypass the navigator through the
    ///     component's <c>Link.OpenCommand</c> (which would call the OS launcher directly).
    /// </summary>
    public ICommand OpenLinkCommand => _openLinkCommand;

    private void OpenLink(object? parameter)
    {
        if (parameter is not LinkClickedEventArgs { HRef: { } href })
        {
            return;
        }

        // HRef comes from the rendered link target itself, never from the display text. The
        // component parses it with UriKind.RelativeOrAbsolute, so hand relative targets over as
        // their original string and let the navigator reject them explicitly.
        string url = href.IsAbsoluteUri ? href.AbsoluteUri : href.OriginalString;
        _links.NavigateAsync(url).Observe(nameof(ChangelogViewModel), "open-changelog-link");
    }
}
