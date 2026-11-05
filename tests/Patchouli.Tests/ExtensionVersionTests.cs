using FluentAssertions;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Tests;

public sealed class ExtensionVersionTests
{
    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.0.0")]
    [InlineData("1.0.0-alpha.1+sha.001")]
    [InlineData("999999999999999999999999.0.0")]
    public void Accepts_strict_semver(string value)
    {
        ExtensionVersion.TryParse(value, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("01.0.0")]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0-alpha..x")]
    [InlineData("1.0.0+")]
    [InlineData("1.0.0 ")]
    [InlineData("1.0.0\n")]
    public void Rejects_invalid_semver(string value)
    {
        ExtensionVersion.TryParse(value, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("999999999999999999999998.0.0", "999999999999999999999999.0.0")]
    public void Orders_numeric_and_prerelease_components(string lower, string higher)
    {
        ExtensionVersion.Parse(lower).CompareTo(ExtensionVersion.Parse(higher)).Should().BeNegative();
    }

    [Fact]
    public void Build_metadata_does_not_change_precedence()
    {
        ExtensionVersion.Parse("1.2.3+build.1").CompareTo(ExtensionVersion.Parse("1.2.3+build.2")).Should().Be(0);
    }
}
