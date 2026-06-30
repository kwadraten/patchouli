using Patchouli.Core.Ids;

namespace Patchouli.Core.Layout;

public sealed record PlainTextPage(PageId PageId, string Text, LayoutRevisionId RevisionId);
