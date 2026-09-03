namespace Patchouli.UI.ViewModels;

/// <summary>
/// Computes graph metadata for the version-history tables: first/last row markers and,
/// for revert rows, the distance to the row they restored so the view can draw a connector.
/// </summary>
public static class HistoryRowMetadata
{
    /// <param name="rows">History rows in display order, newest first.</param>
    /// <param name="idOf">Returns the row's own revision/commit id string.</param>
    /// <param name="revertedFromOf">Returns the id this row restored, or null for non-revert rows.</param>
    /// <param name="apply">Applies (isNewest, isOldest, revertRowOffset) to a row.</param>
    public static void Apply<TRow>(
        IReadOnlyList<TRow> rows,
        Func<TRow, string> idOf,
        Func<TRow, string?> revertedFromOf,
        Action<TRow, bool, bool, int?> apply)
    {
        Dictionary<string, int> indexById = new();
        for (int i = 0; i < rows.Count; i++)
        {
            indexById[idOf(rows[i])] = i;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            // The list is newest-first, so a revert target (an older revision) sits further down.
            int? revertOffset = revertedFromOf(rows[i]) is { } target
                                && indexById.TryGetValue(target, out int targetIndex)
                                && targetIndex > i
                ? targetIndex - i
                : null;
            apply(rows[i], i == 0, i == rows.Count - 1, revertOffset);
        }
    }
}
