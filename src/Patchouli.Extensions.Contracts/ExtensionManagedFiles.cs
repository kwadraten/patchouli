namespace Patchouli.Extensions.Contracts;

/// <summary>
/// One host-managed file an FSX package declares on a <see cref="ConfigValueKind.FilePath"/> or
/// <see cref="ConfigValueKind.DirectoryPath"/> settings field. The host injects the declared
/// managed location as the field's editable default and can verify/download the file under
/// explicit user confirmation. All data is static declaration metadata — reading it never runs
/// package code, creates directories, or touches the network.
/// </summary>
/// <param name="RelativePath">
/// Portable relative path of the file inside its managed directory, '/'-normalized. Bounded: no
/// rooted/absolute forms, no drive letters, no dot/parent/empty segments.
/// </param>
/// <param name="DownloadUrl">Absolute http(s) location the confirmed download pulls from.</param>
/// <param name="ExpectedBytes">Pinned byte length when known.</param>
/// <param name="Sha256">
/// Pinned lowercase-or-uppercase hex digest when the package can vouch for one. Null is honest
/// "unknown" (for example the legacy NDL Koten manifest) — the downloader then verifies size
/// only and never fabricates a hash.
/// </param>
/// <param name="Version">Public manifest version/commit the declaration is pinned to, when known.</param>
/// <param name="DirectoryKind">
/// Managed-directory alias resolved through host directory metadata
/// (<c>OcrStorageLocations.ModelDirectoryFor</c>). Unknown or absent kinds resolve safely below
/// <c>{modelsRoot}/extensions/{extensionId}/{fieldKey}</c>.
/// </param>
/// <param name="WhenKey">
/// Selection-condition key: this file is the applicable declaration only when the resolved
/// configuration value for <paramref name="WhenKey"/> equals <paramref name="WhenValue"/>
/// (for example <c>layout-model=doclayout-yolo</c> chooses the YOLO weights over the PP ones).
/// </param>
/// <param name="WhenValue">Selection-condition value; must be set together with <paramref name="WhenKey"/>.</param>
/// <param name="RequiredWhenKey">
/// Requirement-condition key: the file is actually required only when the resolved value for
/// <paramref name="RequiredWhenKey"/> equals <paramref name="RequiredWhenValue"/> (for example
/// <c>use-local-layout=true</c>). When unset the file is required whenever it applies.
/// </param>
/// <param name="RequiredWhenValue">Requirement-condition value; set together with <paramref name="RequiredWhenKey"/>.</param>
/// <param name="RequiredScopes">
/// OCR execution scope strings (<c>OcrRunExecutionScope</c> values) for which the file is
/// required. Null/empty means every scope; a supplied scope outside the list bypasses the
/// requirement (for example <c>region</c> crops never pull full-page layout weights). A null
/// scope argument cannot disprove the list, so the file stays required.
/// </param>
public sealed record ExtensionManagedFile(
    string RelativePath,
    string DownloadUrl,
    long? ExpectedBytes = null,
    string? Sha256 = null,
    string? Version = null,
    string? DirectoryKind = null,
    string? WhenKey = null,
    string? WhenValue = null,
    string? RequiredWhenKey = null,
    string? RequiredWhenValue = null,
    IReadOnlyList<string>? RequiredScopes = null);
