using Markdig;
using Patchouli.Core.Documents;

namespace Patchouli.UI.Reading;

// Projects one page's compiled markdown into an HTML fragment for the reading surface
// (AvaloniaRichEditor). Fragments carry no page anchor: page boundaries are shown as badges
// in the view's left rail (tracked through BookReadingPageMap) so a paragraph that spans a
// page break flows uninterrupted.
public static class BookReadingHtml
{
    // Pipe tables cover GFM tables; complex tables already arrive as raw HTML inside the markdown,
    // which Markdig passes through unchanged (HTML blocks are not escaped by default).
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();

    public static string CompilePageHtml(CompiledMarkdown compiled, int pageIndex, int pageCount)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        if (string.IsNullOrWhiteSpace(compiled.Markdown))
        {
            // An empty paragraph keeps the page a line tall so its rail badge does not overlap
            // the next page's badge.
            return "<p></p>\n";
        }

        return Markdown.ToHtml(compiled.Markdown, Pipeline);
    }

    // Fragment for a page that has no committed revision yet (not OCR'd); keeps the reading
    // flow continuous instead of silently skipping pages.
    public static string CompilePlaceholderHtml(int pageIndex, int pageCount)
    {
        return "<p><i>本页尚未识别文字。</i></p>";
    }

    // Projects a page's translated markdown through the same pipeline as the source page, so the
    // two panes render identically structured fragments. A page with no translation (or an empty
    // compiled one) renders as a placeholder rather than a blank column.
    public static string CompileTranslationHtml(TranslatedPageMarkdown? translation)
    {
        if (translation is null || string.IsNullOrWhiteSpace(translation.Markdown))
        {
            return CompileUntranslatedHtml();
        }

        return Markdown.ToHtml(translation.Markdown, Pipeline);
    }

    // Fragment for a page that has no translation yet; keeps the compare pane continuous so the
    // reader can see at a glance that nothing has been translated for this page.
    public static string CompileUntranslatedHtml()
    {
        return "<p><i>本页尚无翻译。</i></p>";
    }
}
