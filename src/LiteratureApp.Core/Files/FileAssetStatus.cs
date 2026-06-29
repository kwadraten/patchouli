namespace LiteratureApp.Core.Files;

public static class FileAssetStatus
{
    public const string Available = "available";
    public const string MovedCandidate = "moved_candidate";
    public const string Missing = "missing";
    public const string OfflineRoot = "offline_root";
    public const string Conflict = "conflict";
    public const string Changed = "changed";

    public static bool IsKnown(string status)
    {
        return status is Available or MovedCandidate or Missing or OfflineRoot or Conflict or Changed;
    }
}
