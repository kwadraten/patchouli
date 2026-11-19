namespace Patchouli.Core.Library;

public sealed class LibraryRequiresNewerHostException : Exception
{
    public LibraryRequiresNewerHostException(string requiredHostVersion, string currentHostVersion,
        int? foundSchemaEpoch, int supportedSchemaEpoch)
        : base(
            $"Library requires a newer Patchouli host (minimum {requiredHostVersion}; current {currentHostVersion}; " +
            $"schema epoch {foundSchemaEpoch}, supported {supportedSchemaEpoch}). Upgrade Patchouli to open it.")
    {
        RequiredHostVersion = requiredHostVersion;
        CurrentHostVersion = currentHostVersion;
        FoundSchemaEpoch = foundSchemaEpoch;
        SupportedSchemaEpoch = supportedSchemaEpoch;
    }

    public string RequiredHostVersion { get; }
    public string CurrentHostVersion { get; }
    public int? FoundSchemaEpoch { get; }
    public int SupportedSchemaEpoch { get; }
}

public sealed class UnsupportedLibrarySchemaException : Exception
{
    public UnsupportedLibrarySchemaException(IReadOnlyCollection<int> schemaVersions)
        : base(BuildMessage(schemaVersions))
    {
        SchemaVersions = schemaVersions;
    }

    public IReadOnlyCollection<int> SchemaVersions { get; }

    private static string BuildMessage(IReadOnlyCollection<int> schemaVersions)
    {
        string found = schemaVersions.Count == 0 ? "unknown" : string.Join(", ", schemaVersions.Order());
        return $"Library schema epoch {found} is too old for Patchouli {BuildInfo.Version}. " +
               "Create a new library and re-import the source documents.";
    }
}
