using Patchouli.Core.Results;

namespace Patchouli.Core.Csl;

public interface ICslStyleStore
{
    Task<Result<IReadOnlyList<CslStyle>>> ListInstalledStylesAsync(CancellationToken cancellationToken = default);
    Task<Result<CslStyle>> GetStyleAsync(string styleId, CancellationToken cancellationToken = default);
    Task<Result<string>> GetStyleContentAsync(string styleId, CancellationToken cancellationToken = default);
    Task<Result<CslStyle>> InstallStyleAsync(CslCatalogStyle catalogStyle, string contentXml, CancellationToken cancellationToken = default);
    Task<Result<CslStyle>> DisableStyleAsync(string styleId, CancellationToken cancellationToken = default);
    Task<Result> RemoveStyleAsync(string styleId, CancellationToken cancellationToken = default);
    Task<Result<CslSettings>> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task<Result<CslSettings>> SaveSettingsAsync(string? defaultStyleId, string? locale, CancellationToken cancellationToken = default);
}
