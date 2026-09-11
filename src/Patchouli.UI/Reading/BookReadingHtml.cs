using Patchouli.Core.Documents;

namespace Patchouli.UI.Reading;

// Projects one page's compiled markdown into an HTML fragment for the reading surface
// (AvaloniaRichEditor). Every page fragment starts with a page-anchor heading carrying
// data-page="{PageIndex + 1}" so the view can reason about page boundaries while scrolling.
public static class BookReadingHtml
{
    public static string CompilePageHtml(CompiledMarkdown compiled, int pageIndex, int pageCount)
    {
        throw new NotImplementedException();
    }

    // Fragment for a page that has no committed revision yet (not OCR'd); keeps the reading
    // flow continuous instead of silently skipping pages.
    public static string CompilePlaceholderHtml(int pageIndex, int pageCount)
    {
        throw new NotImplementedException();
    }
}
