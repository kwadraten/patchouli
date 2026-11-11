using System.Xml.Linq;
using FluentAssertions;

namespace Patchouli.Tests;

/// <summary>
/// Guards the fix for the native WebView crash: Avalonia's Win32 NativeControlHost throws
/// "Application manifest with supported OS list might be required" when the process has no
/// RT_MANIFEST compatibility section. Patchouli.UI must embed an unconditional application
/// manifest declaring the supported OS GUIDs in both Debug and Release/publish builds.
/// </summary>
public class WindowsApplicationManifestTests
{
    private static readonly string ProjectPath =
        TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Patchouli.UI.csproj");

    private static readonly string ManifestPath =
        TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "app.manifest");

    [Fact]
    public void UiProjectEmbedsApplicationManifestUnconditionally()
    {
        XDocument project = XDocument.Load(ProjectPath);
        XElement? manifest = project.Root?
            .Elements("PropertyGroup")
            .Where(group => group.Attribute("Condition") is null)
            .Elements("ApplicationManifest")
            .FirstOrDefault();

        manifest.Should().NotBeNull(
            "Patchouli.UI must embed app.manifest in an unconditional PropertyGroup so Debug and Release both carry it");
        manifest!.Value.Trim().Should().Be("app.manifest");
    }

    [Fact]
    public void ApplicationManifestDeclaresSupportedOsCompatibilityList()
    {
        string manifest = File.ReadAllText(ManifestPath);

        // Without this list the OS reports Windows Vista-era version info and
        // Win32NativeControlHost refuses to create the child window (the observed crash).
        manifest.Should().Contain("{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}", "Windows 10/11");
        manifest.Should().Contain("{1f676c76-80e1-4239-95bb-83d0f6d0da78}", "Windows 8.1");
        manifest.Should().Contain("{4a2f28e3-53b9-4441-ba9c-d69d4a4a6e38}", "Windows 8");
        manifest.Should().Contain("compatibility");
        manifest.Should().Contain("supportedOS");
    }

    [Fact]
    public void ApplicationManifestRunsAsInvokerWithoutUiAccess()
    {
        string manifest = File.ReadAllText(ManifestPath);

        manifest.Should().Contain("level=\"asInvoker\"");
        manifest.Should().Contain("uiAccess=\"false\"");
    }
}
