using Patchouli.Core.Ids;

namespace Patchouli.Core.Library;

public sealed record LibraryColumnPreference(
    string ColumnKey,
    int Order,
    bool Visible,
    double? Width = null);

public sealed record LibraryPreferences(
    LibraryId LibraryId,
    string Scope,
    IReadOnlyList<LibraryColumnPreference> Columns,
    DateTimeOffset UpdatedAt);
