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
    [InlineData("0.3.13-beta.10", "0.3.13-beta.2", 1)] // SemVer: numeric identifiers compare by value
    [InlineData("0.3.13-beta.2", "0.3.13-beta.10", -1)]
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

    [Theory]
    [InlineData("1.0.0-2", "1.0.0-alpha", -1)]       // numeric identifiers rank below alphanumeric
    [InlineData("1.0.0-alpha", "1.0.0-2", 1)]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.1.2", -1)] // longer identifier list wins on ties
    [InlineData("1.0.0-alpha.1.2", "1.0.0-alpha.1", 1)]
    [InlineData("1.0.0-beta.02", "1.0.0-beta.2", 0)] // leading zeros do not change numeric value
    [InlineData("1.0.0-rc.1", "1.0.0-beta.11", 1)]   // "rc" > "beta" ordinally
    [InlineData("1.0.0-alpha", "1.0.0-ALPHA", 1)]    // alphanumeric identifiers compare ordinally
    public void CompareTo_applies_semver_prerelease_rules(string left, string right, int expectedSign)
    {
        UpdateVersion leftVersion = UpdateVersion.Parse(left);
        UpdateVersion rightVersion = UpdateVersion.Parse(right);

        Math.Sign(leftVersion.CompareTo(rightVersion)).Should().Be(expectedSign);
        // Symmetry sanity: reversing the operands flips the sign.
        Math.Sign(rightVersion.CompareTo(leftVersion)).Should().Be(-expectedSign);
    }

    [Fact]
    public void Equal_versions_compare_by_value_not_reference()
    {
        UpdateVersion left = UpdateVersion.Parse("v0.3.13");
        UpdateVersion right = UpdateVersion.Parse("0.3.13");

        // Distinct Parts arrays: the default record equality (reference
        // equality on the array) would fail here.
        ReferenceEquals(left.Parts, right.Parts).Should().BeFalse();

        (left == right).Should().BeTrue();
        left.Equals(right).Should().BeTrue();
        left.GetHashCode().Should().Be(right.GetHashCode());
    }

    [Fact]
    public void Different_prerelease_labels_are_not_equal()
    {
        (UpdateVersion.Parse("0.3.13-beta") == UpdateVersion.Parse("0.3.13-beta.1"))
            .Should().BeFalse();
        (UpdateVersion.Parse("0.3.13-beta") != UpdateVersion.Parse("0.3.13-beta.1"))
            .Should().BeTrue();
    }

    [Fact]
    public void Build_metadata_is_ignored_by_equality()
    {
        (UpdateVersion.Parse("0.3.13+build.1") == UpdateVersion.Parse("0.3.13"))
            .Should().BeTrue();
    }

    [Fact]
    public void Null_is_never_equal()
    {
        UpdateVersion version = UpdateVersion.Parse("0.3.13");

        version.Equals((UpdateVersion?)null).Should().BeFalse();
        (version == (UpdateVersion?)null).Should().BeFalse();
        (version != (UpdateVersion?)null).Should().BeTrue();
    }

    [Fact]
    public void Precedence_equal_but_not_value_equal_for_zero_padded_numeric_identifiers()
    {
        // CompareTo sees the same numeric value; Equals compares the label
        // ordinally.
        UpdateVersion left = UpdateVersion.Parse("1.0.0-beta.02");
        UpdateVersion right = UpdateVersion.Parse("1.0.0-beta.2");

        left.CompareTo(right).Should().Be(0);
        (left == right).Should().BeFalse();
    }

    [Fact]
    public void ToString_round_trips_parse()
    {
        UpdateVersion.Parse("v0.3.13-beta").ToString().Should().Be("0.3.13-beta");
        UpdateVersion.Parse("0.3.9").ToString().Should().Be("0.3.9");
    }
}
