using Patchouli.Core.Update;

namespace Patchouli.Core.Library;

public enum LibraryCompatibilityStatus
{
    Compatible,
    RequiresNewerHost,
    TooOld,
    InvalidOrCorrupt
}

/// <summary>Pure compatibility policy, independent of database and host composition.</summary>
public static class LibraryCompatibility
{
    public const string NewLibraryMinHostVersion = "0.4.2";
    public const string LegacyMinHostVersion = "0.4.1";
    public const int MinimumSchemaEpoch = 2;

    public static LibraryCompatibilityStatus Evaluate(int? schemaEpoch, string? minHostVersion,
        string currentHostVersion, bool invalidStructure = false)
    {
        if (invalidStructure || schemaEpoch is null ||
            !UpdateVersion.TryParse(minHostVersion, out UpdateVersion? minimum) ||
            !UpdateVersion.TryParse(currentHostVersion, out UpdateVersion? current))
        {
            return LibraryCompatibilityStatus.InvalidOrCorrupt;
        }

        if (current!.CompareTo(minimum) < 0 || schemaEpoch > AppSchemaVersion.Current)
        {
            return LibraryCompatibilityStatus.RequiresNewerHost;
        }

        return schemaEpoch < MinimumSchemaEpoch
            ? LibraryCompatibilityStatus.TooOld
            : LibraryCompatibilityStatus.Compatible;
    }

    public static string SnapshotMinimum(string libraryMinimum)
    {
        return UpdateVersion.Parse(libraryMinimum).CompareTo(UpdateVersion.Parse(NewLibraryMinHostVersion)) < 0
            ? NewLibraryMinHostVersion
            : libraryMinimum;
    }
}
