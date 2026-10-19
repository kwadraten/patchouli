namespace Patchouli.Core.Update;

/// <summary>
/// Issue #15: semantic version value used for update checks. Parsing tolerates the
/// leading <c>v</c> GitHub tags commonly carry (e.g. <c>v0.3.13</c>) and compares
/// version parts numerically — never lexicographically — so that e.g.
/// <c>0.3.9</c> &lt; <c>0.3.13</c> (a plain string comparison would order them
/// the wrong way around). A version without a pre-release label is newer than the
/// same version with one; build metadata is ignored for ordering.
/// </summary>
public sealed record UpdateVersion : IComparable<UpdateVersion>
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

        return string.Compare(PreRelease, other.PreRelease, StringComparison.Ordinal);
    }

    public override string ToString()
    {
        string text = string.Join(".", Parts);
        return PreRelease is null ? text : $"{text}-{PreRelease}";
    }
}
