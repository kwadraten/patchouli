namespace Patchouli.Core.Documents;

public static class DocumentInstanceStatus
{
    public const string Active = "active";
    public const string Deprecated = "deprecated";
    public const string Partial = "partial";
    public const string MissingSource = "missing_source";

    public static bool IsKnown(string status)
    {
        return status is Active or Deprecated or Partial or MissingSource;
    }
}
