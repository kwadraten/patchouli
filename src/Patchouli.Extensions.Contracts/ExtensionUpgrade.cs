namespace Patchouli.Extensions.Contracts;

public enum ExtensionUpgradeDecision
{
    Upgrade,
    Normalize,
    RetainModified,
    RejectDowngrade
}

/// <summary>Shared automatic installation policy; explicit draft edits and recovery bypass it.</summary>
public static class ExtensionUpgrade
{
    public static ExtensionUpgradeDecision Decide(ExtensionPackageSnapshot installed,
        ExtensionPackageSnapshot incoming)
    {
        if (installed.Id != incoming.Id)
        {
            throw new ArgumentException("An upgrade must retain the installed extension identity.", nameof(incoming));
        }

        int precedence = ExtensionVersion.Parse(ExtensionEntryMetadata.Parse(incoming).Version)
            .CompareTo(ExtensionVersion.Parse(ExtensionEntryMetadata.Parse(installed).Version));
        return precedence switch
        {
            > 0 => ExtensionUpgradeDecision.Upgrade,
            < 0 => ExtensionUpgradeDecision.RejectDowngrade,
            _ => installed.Fingerprint == incoming.Fingerprint
                ? ExtensionUpgradeDecision.Normalize
                : ExtensionUpgradeDecision.RetainModified
        };
    }

    public static string RetainedWarning(string id)
    {
        return $"Extension '{id}' has different installed and incoming contents at the same version; " +
               "the user-editable installed package was retained. Publish a higher version to upgrade.";
    }
}
