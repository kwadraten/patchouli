using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Dapper;
using Patchouli.Core.Csl;
using Patchouli.Core.Ids;
using Patchouli.Core.Operations;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.Csl;

public sealed class CslStyleStore : ICslStyleStore
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly string _stylesRoot;
    private readonly string _installedRoot;
    private readonly IBlockingOperationService? _blockingOperations;

    public CslStyleStore(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        string? stylesRoot = null,
        IBlockingOperationService? blockingOperations = null)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _stylesRoot = stylesRoot ?? CslStoragePaths.GetStylesRoot(connectionFactory.DatabasePath);
        _installedRoot = Path.Combine(_stylesRoot, "installed");
        _blockingOperations = blockingOperations;
        Directory.CreateDirectory(_installedRoot);
    }

    public async Task<Result<IReadOnlyList<CslStyle>>> ListInstalledStylesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var rows = await connection.QueryAsync<Row>(
                """
                select style_id as StyleId,
                       display_name as DisplayName,
                       default_locale as DefaultLocale,
                       source_url as SourceUrl,
                       source_kind as SourceKind,
                       content_hash as ContentHash,
                       installed_at as InstalledAt,
                       updated_at as UpdatedAt,
                       enabled as Enabled,
                       deleted as Deleted
                from csl_styles
                where deleted = 0
                order by display_name, style_id;
                """);
            return Result<IReadOnlyList<CslStyle>>.Success(rows.Select(row => row.ToModel()).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result<IReadOnlyList<CslStyle>>.Failure(AppErrorCodes.DatabaseError, $"CSL style listing failed: {exception.Message}");
        }
    }

    public async Task<Result<CslStyle>> GetStyleAsync(string styleId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(styleId))
        {
            return Result<CslStyle>.Failure(AppErrorCodes.ValidationFailed, "CSL style id is required.");
        }

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var row = await connection.QuerySingleOrDefaultAsync<Row>(
                """
                select style_id as StyleId,
                       display_name as DisplayName,
                       default_locale as DefaultLocale,
                       source_url as SourceUrl,
                       source_kind as SourceKind,
                       content_hash as ContentHash,
                       installed_at as InstalledAt,
                       updated_at as UpdatedAt,
                       enabled as Enabled,
                       deleted as Deleted
                from csl_styles
                where style_id = @StyleId and deleted = 0;
                """,
                new { StyleId = styleId.Trim() });
            return row is null
                ? Result<CslStyle>.Failure(AppErrorCodes.NotFound, "CSL style was not found.")
                : Result<CslStyle>.Success(row.ToModel());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result<CslStyle>.Failure(AppErrorCodes.DatabaseError, $"CSL style lookup failed: {exception.Message}");
        }
    }

    public async Task<Result<string>> GetStyleContentAsync(string styleId, CancellationToken cancellationToken = default)
    {
        var style = await GetStyleAsync(styleId, cancellationToken);
        if (style.IsFailure)
        {
            return Result<string>.Failure(style.ErrorCode!, style.ErrorMessage!);
        }

        var resolvedPath = ResolveStylePath(style.Value.StyleId);
        if (resolvedPath.IsFailure)
        {
            return Result<string>.Failure(resolvedPath.ErrorCode!, resolvedPath.ErrorMessage!);
        }

        var path = resolvedPath.Value;
        if (!File.Exists(path))
        {
            return Result<string>.Failure(AppErrorCodes.NotFound, "CSL style content file was not found.");
        }

        return Result<string>.Success(await File.ReadAllTextAsync(path, cancellationToken));
    }

    public async Task<Result<CslStyle>> InstallStyleAsync(CslCatalogStyle catalogStyle, string contentXml, CancellationToken cancellationToken = default)
    {
        var installOperationId = await TryStartInstallOperationAsync(catalogStyle.StyleId, cancellationToken);
        if (string.IsNullOrWhiteSpace(catalogStyle.StyleId))
        {
            await TryFailInstallOperationAsync(
                installOperationId,
                AppErrorCodes.ValidationFailed,
                "CSL style id is required.",
                cancellationToken);
            return Result<CslStyle>.Failure(AppErrorCodes.ValidationFailed, "CSL style id is required.");
        }

        if (string.IsNullOrWhiteSpace(contentXml))
        {
            await TryFailInstallOperationAsync(
                installOperationId,
                AppErrorCodes.ValidationFailed,
                "CSL style content is required.",
                cancellationToken);
            return Result<CslStyle>.Failure(AppErrorCodes.ValidationFailed, "CSL style content is required.");
        }

        var metadata = ParseMetadata(catalogStyle.StyleId, catalogStyle.DisplayName, catalogStyle.SourceUrl, catalogStyle.SourceKind, contentXml);
        var resolvedPath = ResolveStylePath(metadata.StyleId);
        if (resolvedPath.IsFailure)
        {
            await TryFailInstallOperationAsync(
                installOperationId,
                resolvedPath.ErrorCode!,
                resolvedPath.ErrorMessage!,
                cancellationToken);
            return Result<CslStyle>.Failure(resolvedPath.ErrorCode!, resolvedPath.ErrorMessage!);
        }

        try
        {
            Directory.CreateDirectory(_installedRoot);
            await File.WriteAllTextAsync(resolvedPath.Value, contentXml, cancellationToken);

            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(
                """
                insert into csl_styles (
                    style_id, display_name, default_locale, source_url, source_kind, content_hash,
                    installed_at, updated_at, enabled, deleted
                )
                values (
                    @StyleId, @DisplayName, @DefaultLocale, @SourceUrl, @SourceKind, @ContentHash,
                    @InstalledAt, @UpdatedAt, 1, 0
                )
                on conflict(style_id) do update set
                    display_name = excluded.display_name,
                    default_locale = excluded.default_locale,
                    source_url = excluded.source_url,
                    source_kind = excluded.source_kind,
                    content_hash = excluded.content_hash,
                    updated_at = excluded.updated_at,
                    enabled = 1,
                    deleted = 0;
                """,
                new
                {
                    metadata.StyleId,
                    metadata.DisplayName,
                    metadata.DefaultLocale,
                    metadata.SourceUrl,
                    metadata.SourceKind,
                    metadata.ContentHash,
                    InstalledAt = metadata.InstalledAt.ToString("O"),
                    UpdatedAt = metadata.UpdatedAt.ToString("O")
                });

            await TryCompleteInstallOperationAsync(
                installOperationId,
                $"Installed CSL style '{metadata.StyleId}'.",
                cancellationToken);
            return Result<CslStyle>.Success(metadata);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await TryFailInstallOperationAsync(
                installOperationId,
                AppErrorCodes.DatabaseError,
                $"CSL style install failed: {exception.Message}",
                cancellationToken);
            return Result<CslStyle>.Failure(AppErrorCodes.DatabaseError, $"CSL style install failed: {exception.Message}");
        }
    }

    public async Task<Result<CslStyle>> DisableStyleAsync(string styleId, CancellationToken cancellationToken = default)
    {
        var style = await GetStyleAsync(styleId, cancellationToken);
        if (style.IsFailure)
        {
            return Result<CslStyle>.Failure(style.ErrorCode!, style.ErrorMessage!);
        }

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(
                "update csl_styles set enabled = 0, updated_at = @UpdatedAt where style_id = @StyleId;",
                new { StyleId = style.Value.StyleId, UpdatedAt = _clock.UtcNow.ToUniversalTime().ToString("O") });
            return Result<CslStyle>.Success(style.Value with { Enabled = false, UpdatedAt = _clock.UtcNow.ToUniversalTime() });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result<CslStyle>.Failure(AppErrorCodes.DatabaseError, $"CSL style disable failed: {exception.Message}");
        }
    }

    public async Task<Result> RemoveStyleAsync(string styleId, CancellationToken cancellationToken = default)
    {
        var style = await GetStyleAsync(styleId, cancellationToken);
        if (style.IsFailure)
        {
            return Result.Failure(style.ErrorCode!, style.ErrorMessage!);
        }

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(
                "update csl_styles set deleted = 1, enabled = 0, updated_at = @UpdatedAt where style_id = @StyleId;",
                new { StyleId = style.Value.StyleId, UpdatedAt = _clock.UtcNow.ToUniversalTime().ToString("O") });
            var resolvedPath = ResolveStylePath(style.Value.StyleId);
            if (resolvedPath.IsFailure)
            {
                return Result.Failure(resolvedPath.ErrorCode!, resolvedPath.ErrorMessage!);
            }

            var path = resolvedPath.Value;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"CSL style removal failed: {exception.Message}");
        }
    }

    public async Task<Result<CslSettings>> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var row = await connection.QuerySingleOrDefaultAsync<SettingsRow>(
                """
                select default_style_id as DefaultStyleId,
                       locale as Locale,
                       updated_at as UpdatedAt
                from csl_settings
                limit 1;
                """);
            return Result<CslSettings>.Success(row?.ToModel() ?? new CslSettings(null, null, _clock.UtcNow.ToUniversalTime()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result<CslSettings>.Failure(AppErrorCodes.DatabaseError, $"CSL settings load failed: {exception.Message}");
        }
    }

    public async Task<Result<CslSettings>> SaveSettingsAsync(string? defaultStyleId, string? locale, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(defaultStyleId))
        {
            var style = await GetStyleAsync(defaultStyleId, cancellationToken);
            if (style.IsFailure)
            {
                return Result<CslSettings>.Failure(style.ErrorCode!, style.ErrorMessage!);
            }

            if (!style.Value.Enabled)
            {
                return Result<CslSettings>.Failure(AppErrorCodes.ValidationFailed, "Disabled CSL styles cannot become the default style.");
            }
        }

        try
        {
            var updatedAt = _clock.UtcNow.ToUniversalTime();
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await connection.ExecuteAsync("delete from csl_settings;");
            await connection.ExecuteAsync(
                """
                insert into csl_settings (settings_id, default_style_id, locale, updated_at)
                values ('default', @DefaultStyleId, @Locale, @UpdatedAt);
                """,
                new
                {
                    DefaultStyleId = string.IsNullOrWhiteSpace(defaultStyleId) ? null : defaultStyleId.Trim(),
                    Locale = string.IsNullOrWhiteSpace(locale) ? null : locale.Trim(),
                    UpdatedAt = updatedAt.ToString("O")
                });
            return Result<CslSettings>.Success(new CslSettings(
                string.IsNullOrWhiteSpace(defaultStyleId) ? null : defaultStyleId.Trim(),
                string.IsNullOrWhiteSpace(locale) ? null : locale.Trim(),
                updatedAt));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result<CslSettings>.Failure(AppErrorCodes.DatabaseError, $"CSL settings save failed: {exception.Message}");
        }
    }

    private Result<string> ResolveStylePath(string styleId)
    {
        if (string.IsNullOrWhiteSpace(styleId))
        {
            return Result<string>.Failure(AppErrorCodes.ValidationFailed, "CSL style id is required.");
        }

        var normalized = styleId.Trim();
        if (normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || normalized.Contains(Path.DirectorySeparatorChar)
            || normalized.Contains(Path.AltDirectorySeparatorChar)
            || Path.IsPathRooted(normalized)
            || normalized is "." or ".."
            || normalized.Contains("..", StringComparison.Ordinal))
        {
            return Result<string>.Failure(AppErrorCodes.ValidationFailed, "CSL style id contains an invalid path segment.");
        }

        var installedRoot = Path.GetFullPath(_installedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(installedRoot, $"{normalized}.csl"));
        if (!candidate.StartsWith(installedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return Result<string>.Failure(AppErrorCodes.ValidationFailed, "CSL style id resolves outside the installed style directory.");
        }

        return Result<string>.Success(candidate);
    }

    private CslStyle ParseMetadata(string styleId, string displayName, string? sourceUrl, string sourceKind, string contentXml)
    {
        var now = _clock.UtcNow.ToUniversalTime();
        try
        {
            var document = XDocument.Parse(contentXml);
            var style = document.Root;
            var ns = style?.Name.Namespace ?? XNamespace.None;
            var info = style?.Element(ns + "info");
            var title = info?.Element(ns + "title")?.Value?.Trim();
            var locale = style?.Attribute("default-locale")?.Value?.Trim();
            return new CslStyle(
                styleId.Trim(),
                string.IsNullOrWhiteSpace(title) ? displayName.Trim() : title,
                string.IsNullOrWhiteSpace(locale) ? null : locale,
                sourceUrl,
                sourceKind,
                ComputeHash(contentXml),
                now,
                now,
                true,
                false);
        }
        catch
        {
            return new CslStyle(
                styleId.Trim(),
                displayName.Trim(),
                null,
                sourceUrl,
                sourceKind,
                ComputeHash(contentXml),
                now,
                now,
                true,
                false);
        }
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private async Task<BlockingOperationId?> TryStartInstallOperationAsync(string? styleId, CancellationToken cancellationToken)
    {
        if (_blockingOperations is null)
        {
            return null;
        }

        try
        {
            var started = await _blockingOperations.StartAsync(
                BlockingOperationTypes.CslStyleInstall,
                BlockingOperationScopeTypes.CslStyle,
                string.IsNullOrWhiteSpace(styleId) ? null : styleId.Trim(),
                canCancel: false,
                progressLabel: "Installing CSL style.",
                nextActions: ["Retry style installation", "Choose a different style source"],
                cancellationToken: cancellationToken);
            return started.IsSuccess ? started.Value.OperationId : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task TryCompleteInstallOperationAsync(
        BlockingOperationId? operationId,
        string progressLabel,
        CancellationToken cancellationToken)
    {
        if (_blockingOperations is null || operationId is null)
        {
            return;
        }

        try
        {
            await _blockingOperations.CompleteAsync(
                operationId.Value,
                progressLabel,
                Array.Empty<string>(),
                cancellationToken);
        }
        catch
        {
        }
    }

    private async Task TryFailInstallOperationAsync(
        BlockingOperationId? operationId,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        if (_blockingOperations is null || operationId is null)
        {
            return;
        }

        try
        {
            await _blockingOperations.FailAsync(
                operationId.Value,
                errorCode,
                errorMessage,
                "CSL style installation failed.",
                ["Retry style installation", "Keep the existing default style"],
                cancellationToken);
        }
        catch
        {
        }
    }

    private sealed class Row
    {
        public string StyleId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string? DefaultLocale { get; set; }
        public string? SourceUrl { get; set; }
        public string SourceKind { get; set; } = "";
        public string ContentHash { get; set; } = "";
        public string InstalledAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
        public int Enabled { get; set; }
        public int Deleted { get; set; }

        public CslStyle ToModel()
            => new(
                StyleId,
                DisplayName,
                DefaultLocale,
                SourceUrl,
                SourceKind,
                ContentHash,
                DateTimeOffset.Parse(InstalledAt),
                DateTimeOffset.Parse(UpdatedAt),
                Enabled != 0,
                Deleted != 0);
    }

    private sealed class SettingsRow
    {
        public string? DefaultStyleId { get; set; }
        public string? Locale { get; set; }
        public string UpdatedAt { get; set; } = "";

        public CslSettings ToModel() => new(DefaultStyleId, Locale, DateTimeOffset.Parse(UpdatedAt));
    }
}
