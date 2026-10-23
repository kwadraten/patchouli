using Patchouli.Extensions;

namespace Patchouli.Tests;

public sealed class ExtensionDependenciesTests
{
    [Fact]
    public void Package_asset_paths_match_fcs_forward_slash_references()
    {
        // FCS emits forward-slash -r: paths on Windows while findPackageRoot returns a
        // platform-separator DirectoryInfo path; unnormalized prefix checks drop every asset.
        string packageRoot = Path.GetFullPath(Path.Combine("cache", "newtonsoft.json", "13.0.3"));
        string forwardSlashReference =
            packageRoot.Replace('\\', '/') + "/lib/net6.0/Newtonsoft.Json.dll";
        string outsideReference = Path.GetFullPath(
                Path.Combine("cache", "other.package", "9.9.9") + "/lib/net6.0/Other.dll")
            .Replace('\\', '/');

        string[] matched = ExtensionDependencies.packageAssetPaths(
            packageRoot,
            new[] { forwardSlashReference, outsideReference });

        Assert.Equal(new[] { forwardSlashReference }, matched);
    }
}
