using System;
using System.Collections.Generic;

namespace Patchouli.Core.Pagination;

public sealed record PageCursor(string? SortKey, string StableId)
{
    public const char Delimiter = '\t';

    public string Encode()
    {
        return $"{SortKey ?? string.Empty}{Delimiter}{StableId}";
    }

    public static PageCursor? Decode(string? cursorText)
    {
        if (string.IsNullOrWhiteSpace(cursorText))
        {
            return null;
        }

        int delimiterIndex = cursorText.IndexOf(Delimiter);
        if (delimiterIndex >= 0)
        {
            string sortKey = cursorText.Substring(0, delimiterIndex);
            string stableId = cursorText.Substring(delimiterIndex + 1);
            return new PageCursor(string.IsNullOrEmpty(sortKey) ? null : sortKey, stableId);
        }

        return new PageCursor(null, cursorText);
    }
}

public sealed record CursorPageRequest(string? AfterCursor, int PageSize = 100)
{
    public const int DefaultPageSize = 100;
}

public sealed record CursorPageResult<T>(
    IReadOnlyList<T> Items,
    string? NextCursor,
    int TotalCount,
    bool HasMore);
