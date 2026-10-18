using Patchouli.Core.Results;

namespace Patchouli.Core.Csl;

public interface ICslStyleStore
{
    Task<Result<IReadOnlyList<CslStyle>>> ListInstalledStylesAsync(CancellationToken cancellationToken = default);
    Task<Result<CslStyle>> GetStyleAsync(string styleId, CancellationToken cancellationToken = default);
    Task<Result<string>> GetStyleContentAsync(string styleId, CancellationToken cancellationToken = default);

    Task<Result<CslStyle>> InstallStyleAsync(CslCatalogStyle catalogStyle, string contentXml,
        CancellationToken cancellationToken = default);

    Task<Result<CslStyle>> ReplaceStyleAsync(string styleId, string contentXml, string expectedRevision,
        CancellationToken cancellationToken = default);

    Task<Result<CslStyle>> DisableStyleAsync(string styleId, CancellationToken cancellationToken = default);
    Task<Result> RemoveStyleAsync(string styleId, CancellationToken cancellationToken = default);
    Task<Result<CslSettings>> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task<Result<CslSettings>> SaveSettingsAsync(string? defaultStyleId, string? locale,
        CancellationToken cancellationToken = default);

    Task<Result<CslSettings>> SaveSettingsAsync(string? defaultStyleId, string? locale,
        IReadOnlyDictionary<string, string>? languageLocaleMappings,
        CancellationToken cancellationToken = default);

    Task<Result<CslSettings>> SaveLanguageLocaleMappingsAsync(
        IReadOnlyDictionary<string, string>? languageLocaleMappings,
        CancellationToken cancellationToken = default);

    Task<Result<CslSettings>> SaveDefaultStyleAsync(string? defaultStyleId,
        CancellationToken cancellationToken = default);

    Task<Result<CslSettings>> SaveLocalizationAsync(string? locale,
        IReadOnlyDictionary<string, string>? languageLocaleMappings,
        CancellationToken cancellationToken = default);
}
