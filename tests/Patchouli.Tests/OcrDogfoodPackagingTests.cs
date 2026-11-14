using FluentAssertions;

namespace Patchouli.Tests;

public sealed class OcrDogfoodPackagingTests
{
    [Fact]
    public void Dogfood_outputs_are_isolated_and_carry_uncommitted_source_identity()
    {
        string source = File.ReadAllText(TestPaths.FromRepositoryRoot("scripts", "package-windows.fsx"));
        source.Should().Contain("--publish-directory").And.Contain("--installer-directory")
            .And.Contain("--build-label").And.Contain("--output-base-name")
            .And.Contain("insideTmp")
            .And.Contain("--others").And.Contain("sha256")
            .And.Contain("source_fingerprint").And.Contain("installer_sha256")
            .And.Contain("\"installed_by_packaging_script\", jvalue false");
    }
}
