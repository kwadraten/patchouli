using LiteratureApp.Core.Ids;

namespace LiteratureApp.Core.Layout;

public sealed record LayoutRevision(
    LayoutRevisionId LayoutRevisionId,
    DocumentInstanceId DocumentInstanceId,
    LayoutRevisionId? ParentRevisionId,
    string Source,
    bool IsCurrent,
    DateTimeOffset CreatedAt);
