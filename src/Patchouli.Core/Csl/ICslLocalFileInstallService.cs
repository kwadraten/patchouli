using Patchouli.Core.Results;

namespace Patchouli.Core.Csl;

/// <summary>Outcome of installing one local CSL style file.</summary>
/// <param name="Success">True when the style was installed or reused.</param>
/// <param name="StyleId">Portable style id extracted from the style XML.</param>
/// <param name="StyleUri">Canonical style URI: patchouli://csl-styles/{style-id}.csl.</param>
/// <param name="Title">Style title from &lt;info&gt;.</param>
/// <param name="Dependencies">Parent/template style ids this style depends on.</param>
/// <param name="Reused">True when the same id with identical content was already installed.</param>
/// <param name="Conflict">True when the id exists with different content (update via put).</param>
/// <param name="Renderable">False when a dependent style's parent is not installed.</param>
/// <param name="MissingParent">The missing parent style id, if any.</param>
/// <param name="ErrorMessage">Failure detail; null on success.</param>
public sealed record CslLocalInstallResult(
    bool Success,
    string StyleId,
    string StyleUri,
    string Title,
    IReadOnlyList<string> Dependencies,
    bool Reused,
    bool Conflict,
    bool Renderable,
    string? MissingParent,
    string? ErrorMessage);

/// <summary>
/// Issue #11: installs a CSL style from a host-local .csl file. New id → install;
/// same id + same content → reuse; same id + different content → conflict (updated
/// later via <c>put</c>). Create/reuse/conflict semantics live in the domain service,
/// not as check-then-write in the MCP layer; concurrent installs are serialized here.
/// </summary>
public interface ICslLocalFileInstallService
{
    Task<Result<CslLocalInstallResult>> InstallFromFileAsync(
        string path,
        CancellationToken cancellationToken = default);
}
