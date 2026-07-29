using Patchouli.Core.Ids;

namespace Patchouli.Core.Bibliography;

public static class ItemDateRoles
{
    public const string Issued = "issued";
    public const string Accessed = "accessed";
    public const string OriginalDate = "original-date";
    public const string EventDate = "event-date";
    public const string Submitted = "submitted";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        Issued,
        Accessed,
        OriginalDate,
        EventDate,
        Submitted
    };
}

public sealed record ItemDate(
    string DateId,
    ItemId ItemId,
    string Role,
    string DatePartsJson,
    bool Circa,
    string? Season,
    string? Literal,
    DateTimeOffset CreatedAt);

public sealed record ItemDateInput(
    string Role,
    string DatePartsJson = "[]",
    bool Circa = false,
    string? Season = null,
    string? Literal = null);
