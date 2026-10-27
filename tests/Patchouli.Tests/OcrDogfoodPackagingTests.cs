using FluentAssertions;

namespace Patchouli.Tests;

public sealed class OcrDogfoodPackagingTests
{
    [Fact]
    public void Dogfood_outputs_are_isolated_and_carry_uncommitted_source_identity()
    {
        string source = File.ReadAllText(TestPaths.FromRepositoryRoot("scripts", "package-windows.ps1"));
        source.Should().Contain("$PublishDirectory").And.Contain("$InstallerDirectory")
            .And.Contain("$BuildLabel").And.Contain("$OutputBaseName")
            .And.Contain("Packaging outputs must be inside the repository .tmp directory")
            .And.Contain("--others").And.Contain("Get-FileHash")
            .And.Contain("source_fingerprint").And.Contain("installer_sha256")
            .And.Contain("installed_by_packaging_script = $false");
    }
}
