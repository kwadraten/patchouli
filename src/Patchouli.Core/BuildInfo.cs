namespace Patchouli.Core;

public static class BuildInfo
{
    public const string AppName = "Patchouli.NET";
    public const string Version = "0.3.12";
    public const string BuildProfile = "Debug/Release";
    public static int SchemaVersion => AppSchemaVersion.Current;
}
