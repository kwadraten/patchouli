namespace Patchouli.Core;

public static class BuildInfo
{
    public const string AppName = "Patchouli.Net";
    public const string Version = "0.2.3";
    public const string BuildProfile = "Debug/Release";
    public static int SchemaVersion => AppSchemaVersion.Current;
}
