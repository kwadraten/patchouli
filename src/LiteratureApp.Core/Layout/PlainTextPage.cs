using LiteratureApp.Core.Ids;

namespace LiteratureApp.Core.Layout;

public sealed record PlainTextPage(PageId PageId, string Text, LayoutRevisionId RevisionId);
