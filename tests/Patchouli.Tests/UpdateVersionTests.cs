using FluentAssertions;
using Patchouli.Core.Update;

namespace Patchouli.Tests;

/// <summary>
/// Issue #15: semantic version parsing and comparison for update checks.
/// Versions compare numerically (never lexicographically), tolerate the
/// leading "v" of GitHub tags, rank pre-releases below final releases, and
/// never treat an equal or older version as an update.
/// </summary>
public sealed class UpdateVersionTests
{
    [Theory]
    [InlineData("0.3.9", "0.3.13", -1)]   // numeric, not lexicographic ("9" > "13" as strings)
    [InlineData("0.3.13", "0.3.9", 1)]
    [InlineData("0.3.13", "0.3.13", 0)]
    [InlineData("v0.3.13", "0.3.13", 0)]  // leading "v" tolerated
    [InlineData("V0.3.13", "0.3.13", 0)]
    [InlineData("0.3.13-beta", "0.3.13", -1)] // pre-release < final
    [InlineData("0.3.13", "0.3.13-beta", 1)]
    [InlineData("0.3.13-alpha", "0.3.13-beta", -1)]
    [InlineData("0.3.10", "0.3.9", 1)]    // 10 > 9 numerically
    [InlineData("0.3.12", "0.3.12", 0)]
    [InlineData("1.0", "1.0.0", 0)]       // missing parts are zero
    [InlineData("0.3.13+build.1", "0.3.13", 0)] // build metadata ignored
    public void CompareTo_orders_versions_numerically(string left, string right, int expectedSign)
    {
        UpdateVersion leftVersion = UpdateVersion.Parse(left);
        UpdateVersion rightVersion = UpdateVersion.Parse(right);

        int compared = leftVersion.CompareTo(rightVersion);

        Math.Sign(compared).Should().Be(expectedSign);
        // Symmetry sanity: reversing the operands flips the sign.
        Math.Sign(rightVersion.CompareTo(leftVersion)).Should().Be(-expectedSign);
    }

    [Fact]
    public void Newer_patch_is_detected_as_update_candidate()
    {
        UpdateVersion current = UpdateVersion.Parse("0.3.12");
        UpdateVersion latest = UpdateVersion.Parse("0.3.13");

        latest.CompareTo(current).Should().BePositive();
    }

    [Fact]
    public void Downgrade_or_same_version_is_never_an_update_candidate()
    {
        UpdateVersion current = UpdateVersion.Parse("0.3.12");

        UpdateVersion.Parse("0.3.10").CompareTo(current).Should().BeNegative();
        UpdateVersion.Parse("v0.3.12").CompareTo(current).Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("v")]
    [InlineData("1.2.3.4.5")]  // too many parts
    [InlineData("1..3")]
    [InlineData("1.-2")]
    [InlineData("0.3.13-")]    // dangling pre-release separator
    public void TryParse_rejects_invalid_versions(string text)
    {
        UpdateVersion.TryParse(text, out UpdateVersion? version).Should().BeFalse();
        version.Should().BeNull();
    }

    [Fact]
    public void Parse_throws_for_invalid_version()
    {
        FluentActions.Invoking(() => UpdateVersion.Parse("not-a-version"))
            .Should().Throw<FormatException>();
    }

    [Fact]
    public void ToString_round_trips_parse()
    {
        UpdateVersion.Parse("v0.3.13-beta").ToString().Should().Be("0.3.13-beta");
        UpdateVersion.Parse("0.3.9").ToString().Should().Be("0.3.9");
    }
}
