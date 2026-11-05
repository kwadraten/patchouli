using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Patchouli.Extensions.Contracts;

/// <summary>Strict SemVer 2.0.0 precedence; numeric identifiers are not limited to Int32.</summary>
public sealed class ExtensionVersion : IComparable<ExtensionVersion>
{
    private static readonly Regex Syntax = new(
        @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly string[] _core;
    private readonly string[] _prerelease;

    private ExtensionVersion(Match match)
    {
        _core = [match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value];
        _prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
    }

    public static bool TryParse(string? value, [NotNullWhen(true)] out ExtensionVersion? version)
    {
        version = null;
        if (value is null)
        {
            return false;
        }

        Match match = Syntax.Match(value);
        if (!match.Success)
        {
            return false;
        }

        ExtensionVersion parsed = new(match);
        if (parsed._prerelease.Any(part => Numeric(part) && part.Length > 1 && part[0] == '0'))
        {
            return false;
        }

        version = parsed;
        return true;
    }

    public static ExtensionVersion Parse(string value)
    {
        return TryParse(value, out ExtensionVersion? version)
            ? version
            : throw new ArgumentException("version must be a valid SemVer 2.0.0 version.", nameof(value));
    }

    private static bool Numeric(string value)
    {
        return value.All(char.IsAsciiDigit);
    }

    private static int CompareNumeric(string left, string right)
    {
        return left.Length != right.Length
            ? left.Length.CompareTo(right.Length)
            : string.CompareOrdinal(left, right);
    }

    public int CompareTo(ExtensionVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        for (int index = 0; index < _core.Length; index++)
        {
            int comparison = CompareNumeric(_core[index], other._core[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        if (_prerelease.Length == 0 || other._prerelease.Length == 0)
        {
            return (_prerelease.Length == 0).CompareTo(other._prerelease.Length == 0);
        }

        for (int index = 0; index < Math.Min(_prerelease.Length, other._prerelease.Length); index++)
        {
            string left = _prerelease[index], right = other._prerelease[index];
            bool leftNumeric = Numeric(left), rightNumeric = Numeric(right);
            int comparison = leftNumeric && rightNumeric ? CompareNumeric(left, right)
                : leftNumeric != rightNumeric ? leftNumeric ? -1 : 1 : string.CompareOrdinal(left, right);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }
}
