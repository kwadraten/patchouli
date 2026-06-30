namespace Patchouli.Core;

public static class BuildInfo
{
    public const string AppName = "Patchouli";
    public const string Version = "0.1.0-alpha-rc1";
    public const string BuildProfile = "Debug/Release";
    public static int SchemaVersion => AppSchemaVersion.Current;
}
