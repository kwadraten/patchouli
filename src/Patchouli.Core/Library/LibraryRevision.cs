using Patchouli.Core.Ids;

namespace Patchouli.Core.Library;

/// <summary>
/// Formats and parses the persistent, monotonic Library revision. The public protocol exposes
/// the revision as <c>lib:&lt;positive decimal integer&gt;</c> and it survives host handoffs.
/// </summary>
public static class LibraryRevisionFormatter
{
    public static string Format(long revision)
    {
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), "Library revision cannot be negative.");
        }

        return $"lib:{revision}";
    }

    public static bool TryParse(string? text, out long revision)
    {
        revision = 0;
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith("lib:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return long.TryParse(text["lib:".Length..], out revision);
    }
}

/// <summary>
/// The typed payload a host write service publishes after a successful, protocol-visible commit.
/// It identifies which resources changed and carries the new Library revision; subscribers must
/// never run long database queries while handling it.
/// </summary>
public sealed record LibraryChangeSet(
    long NewRevision,
    IReadOnlyCollection<ItemId> ItemIds,
    IReadOnlyCollection<DocumentInstanceId> DocumentInstanceIds,
    IReadOnlyCollection<string> StyleIds,
    IReadOnlyCollection<PageId> PageIds,
    IReadOnlyCollection<OcrRunId> OcrRunIds)
{
    public static readonly LibraryChangeSet Empty = new(0, [], [], [], [], []);

    public bool IsEmpty => ItemIds.Count == 0 && DocumentInstanceIds.Count == 0 && StyleIds.Count == 0 &&
                           PageIds.Count == 0 && OcrRunIds.Count == 0;
}

public sealed class LibraryRevisionCommittedEventArgs : EventArgs
{
    public LibraryRevisionCommittedEventArgs(LibraryChangeSet changeSet)
    {
        ChangeSet = changeSet;
    }

    public LibraryChangeSet ChangeSet { get; }
}
