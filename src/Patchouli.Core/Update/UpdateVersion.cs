namespace Patchouli.Core.Update;

/// <summary>
/// Issue #15: semantic version value used for update checks. Parsing tolerates the
/// leading <c>v</c> GitHub tags commonly carry (e.g. <c>v0.3.13</c>) and compares
/// version parts numerically — never lexicographically — so that e.g.
/// <c>0.3.9</c> &lt; <c>0.3.13</c> (a plain string comparison would order them
/// the wrong way around). Pre-release labels follow SemVer §11: dot-separated
/// identifiers compare numerically when both sides are digits (so
/// <c>beta.10</c> &gt; <c>beta.2</c>), numeric identifiers rank below
/// alphanumeric ones, and a longer identifier list wins when all shared
/// identifiers are equal. A version without a pre-release label is newer than
/// the same version with one; build metadata is ignored for ordering.
/// Equality is by value: two versions with equal parts and equal pre-release
/// labels are <c>==</c> even though <c>Parts</c> is an array.
/// </summary>
public sealed record UpdateVersion : IComparable<UpdateVersion>, IEquatable<UpdateVersion>
{
    private UpdateVersion(int[] parts, string? preRelease)
    {
        Parts = parts;
        PreRelease = preRelease;
    }

    /// <summary>Numeric version parts, e.g. [0, 3, 13].</summary>
    public int[] Parts { get; }

    /// <summary>Pre-release label (e.g. "beta"), or null for a final release.</summary>
    public string? PreRelease { get; }

    public static bool TryParse(string? text, out UpdateVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string normalized = text.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        if (normalized.Length == 0)
        {
            return false;
        }

        // Split off build metadata (+...) which never affects ordering.
        int buildIndex = normalized.IndexOf('+');
        if (buildIndex >= 0)
        {
            normalized = normalized[..buildIndex];
        }

        string? preRelease = null;
        int preReleaseIndex = normalized.IndexOf('-');
        if (preReleaseIndex >= 0)
        {
            preRelease = normalized[(preReleaseIndex + 1)..];
            normalized = normalized[..preReleaseIndex];
            if (preRelease.Length == 0)
            {
                return false;
            }
        }

        string[] rawParts = normalized.Split('.');
        if (rawParts.Length == 0 || rawParts.Length > 4)
        {
            return false;
        }

        int[] parts = new int[rawParts.Length];
        for (int i = 0; i < rawParts.Length; i++)
        {
            if (rawParts[i].Length == 0 || !int.TryParse(rawParts[i], out int part) || part < 0)
            {
                return false;
            }

            parts[i] = part;
        }

        version = new UpdateVersion(parts, preRelease);
        return true;
    }

    public static UpdateVersion Parse(string text)
    {
        if (!TryParse(text, out UpdateVersion? version) || version is null)
        {
            throw new FormatException($"Invalid version string: '{text}'.");
        }

        return version;
    }

    public int CompareTo(UpdateVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int length = Math.Max(Parts.Length, other.Parts.Length);
        for (int i = 0; i < length; i++)
        {
            int left = i < Parts.Length ? Parts[i] : 0;
            int right = i < other.Parts.Length ? other.Parts[i] : 0;
            int compared = left.CompareTo(right);
            if (compared != 0)
            {
                return compared;
            }
        }

        // A final release outranks the same version with a pre-release label.
        if (PreRelease is null && other.PreRelease is null)
        {
            return 0;
        }

        if (PreRelease is null)
        {
            return 1;
        }

        if (other.PreRelease is null)
        {
            return -1;
        }

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    /// <summary>
    /// Value equality: compares <see cref="Parts"/> element-wise (the default
    /// record equality would use reference equality for the array) and the
    /// pre-release label ordinally. The synthesized <c>==</c>/<c>!=</c>
    /// operators and <see cref="GetHashCode"/> are consistent with this.
    /// </summary>
    public bool Equals(UpdateVersion? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Parts.SequenceEqual(other.Parts) &&
               string.Equals(PreRelease, other.PreRelease, StringComparison.Ordinal);
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (int part in Parts)
        {
            hash.Add(part);
        }

        hash.Add(PreRelease, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    /// <summary>
    /// SemVer §11 pre-release precedence: identifiers consisting only of digits
    /// compare numerically (longer digit run wins, then lexicographic on the
    /// zero-stripped digits); numeric identifiers have lower precedence than
    /// alphanumeric ones; alphanumeric identifiers compare ordinally; when all
    /// shared identifiers are equal, the longer set has higher precedence.
    /// </summary>
    private static int ComparePreRelease(string left, string right)
    {
        string[] leftFields = left.Split('.');
        string[] rightFields = right.Split('.');
        int shared = Math.Min(leftFields.Length, rightFields.Length);
        for (int i = 0; i < shared; i++)
        {
            bool leftNumeric = IsNumericIdentifier(leftFields[i]);
            bool rightNumeric = IsNumericIdentifier(rightFields[i]);
            int compared;
            if (leftNumeric && rightNumeric)
            {
                string leftDigits = leftFields[i].TrimStart('0');
                string rightDigits = rightFields[i].TrimStart('0');
                compared = leftDigits.Length != rightDigits.Length
                    ? leftDigits.Length.CompareTo(rightDigits.Length)
                    : string.CompareOrdinal(leftDigits, rightDigits);
            }
            else if (leftNumeric)
            {
                compared = -1;
            }
            else if (rightNumeric)
            {
                compared = 1;
            }
            else
            {
                compared = string.CompareOrdinal(leftFields[i], rightFields[i]);
            }

            if (compared != 0)
            {
                return compared;
            }
        }

        return leftFields.Length.CompareTo(rightFields.Length);
    }

    private static bool IsNumericIdentifier(string field)
    {
        return field.Length > 0 && field.All(char.IsAsciiDigit);
    }

    public override string ToString()
    {
        string text = string.Join(".", Parts);
        return PreRelease is null ? text : $"{text}-{PreRelease}";
    }
}
