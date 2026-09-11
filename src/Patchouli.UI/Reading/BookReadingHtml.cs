using System.Text;
using Markdig;
using Patchouli.Core.Documents;

namespace Patchouli.UI.Reading;

// Projects one page's compiled markdown into an HTML fragment for the reading surface
// (AvaloniaRichEditor). Every page fragment starts with a page-anchor heading carrying
// data-page="{PageIndex + 1}" so the view can reason about page boundaries while scrolling.
public static class BookReadingHtml
{
    // Pipe tables cover GFM tables; complex tables already arrive as raw HTML inside the markdown,
    // which Markdig passes through unchanged (HTML blocks are not escaped by default).
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();

    public static string CompilePageHtml(CompiledMarkdown compiled, int pageIndex, int pageCount)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        StringBuilder html = new();
        AppendAnchor(html, pageIndex, pageCount);
        if (!string.IsNullOrWhiteSpace(compiled.Markdown))
        {
            html.Append(Markdown.ToHtml(compiled.Markdown, Pipeline));
        }

        return html.ToString();
    }

    // Fragment for a page that has no committed revision yet (not OCR'd); keeps the reading
    // flow continuous instead of silently skipping pages.
    public static string CompilePlaceholderHtml(int pageIndex, int pageCount)
    {
        StringBuilder html = new();
        AppendAnchor(html, pageIndex, pageCount);
        html.Append("<p><i>本页尚未识别文字。</i></p>");
        return html.ToString();
    }

    private static void AppendAnchor(StringBuilder html, int pageIndex, int pageCount)
    {
        int pageNumber = pageIndex + 1;
        html.Append("<h2 data-page=\"").Append(pageNumber).Append("\">第 ").Append(pageNumber)
            .Append(" 页 / 共 ").Append(pageCount).Append(" 页</h2>\n");
    }
}
