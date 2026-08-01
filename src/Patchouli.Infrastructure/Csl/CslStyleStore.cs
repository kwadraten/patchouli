using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Xml;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Csl;
using Patchouli.Core.Diagnostics;
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

    public async Task<Result<IReadOnlyList<CslStyle>>> ListInstalledStylesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            IEnumerable<Row> rows = await connection.QueryAsync<Row>(
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
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.csl-style-store"))
        {
            return Result<IReadOnlyList<CslStyle>>.Failure(AppErrorCodes.DatabaseError,
                $"CSL style listing failed: {exception.Message}");
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
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            Row? row = await connection.QuerySingleOrDefaultAsync<Row>(
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
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.csl-style-store"))
        {
            return Result<CslStyle>.Failure(AppErrorCodes.DatabaseError,
                $"CSL style lookup failed: {exception.Message}");
        }
    }

    public async Task<Result<string>> GetStyleContentAsync(string styleId,
        CancellationToken cancellationToken = default)
    {
        Result<CslStyle> style = await GetStyleAsync(styleId, cancellationToken);
        if (style.IsFailure)
        {
            return Result<string>.Failure(style.ErrorCode!, style.ErrorMessage!);
        }

        Result<string> resolvedPath = ResolveStylePath(style.Value.StyleId);
        if (resolvedPath.IsFailure)
        {
            return Result<string>.Failure(resolvedPath.ErrorCode!, resolvedPath.ErrorMessage!);
        }

        string path = resolvedPath.Value;
        if (!File.Exists(path))
        {
            return Result<string>.Failure(AppErrorCodes.NotFound, "CSL style content file was not found.");
        }

        return Result<string>.Success(await File.ReadAllTextAsync(path, cancellationToken));
    }

    public async Task<Result<CslStyle>> ReplaceStyleAsync(string styleId, string contentXml,
        string expectedRevision, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(contentXml))
        {
            return Result<CslStyle>.Failure(AppErrorCodes.ValidationFailed, "CSL style content is required.");
        }

        Result<string> resolvedPath = ResolveStylePath(styleId);
        if (resolvedPath.IsFailure)
        {
            return Result<CslStyle>.Failure(resolvedPath.ErrorCode!, resolvedPath.ErrorMessage!);
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(contentXml, LoadOptions.PreserveWhitespace);
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            return Result<CslStyle>.Failure(AppErrorCodes.ValidationFailed, "CSL style XML is invalid.");
        }

        string? declaredId = GetDeclaredStyleId(document);
        if (declaredId is null)
        {
            return Result<CslStyle>.Failure(AppErrorCodes.ValidationFailed,
                "CSL style XML requires a style root and a non-empty info.id.");
        }

        Result<CslStyle> current = await GetStyleAsync(styleId, cancellationToken);
        if (current.IsFailure)
        {
            return current;
        }

        Result<string> currentContent = await GetStyleContentAsync(styleId, cancellationToken);
        if (currentContent.IsFailure)
        {
            return Result<CslStyle>.Failure(currentContent.ErrorCode!, currentContent.ErrorMessage!);
        }

        string? currentDeclaredId;
        try
        {
            currentDeclaredId = GetDeclaredStyleId(XDocument.Parse(currentContent.Value, LoadOptions.PreserveWhitespace));
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            return Result<CslStyle>.Failure(AppErrorCodes.DatabaseError,
                "Existing CSL style content is invalid.");
        }

        if (!string.Equals(declaredId, currentDeclaredId, StringComparison.Ordinal))
        {
            return Result<CslStyle>.Failure(AppErrorCodes.ValidationFailed,
                "CSL style info.id cannot change through MCP.");
        }

        string expectedHash = expectedRevision.StartsWith("style:", StringComparison.Ordinal)
            ? expectedRevision["style:".Length..]
            : string.Empty;
        if (!string.Equals(expectedHash, current.Value.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            return Result<CslStyle>.Failure(AppErrorCodes.Conflict, "CSL style revision is stale.");
        }

        string path = resolvedPath.Value;
        string temporaryPath = path + ".mcp-put-" + Guid.NewGuid().ToString("N") + ".tmp";
        string backupPath = path + ".mcp-put-" + Guid.NewGuid().ToString("N") + ".bak";
        bool replacedFile = false;
        try
        {
            await File.WriteAllTextAsync(temporaryPath, contentXml, Encoding.UTF8, cancellationToken);
            File.Copy(path, backupPath, overwrite: true);
            File.Move(temporaryPath, path, overwrite: true);
            replacedFile = true;

            CslStyle metadata = ParseMetadata(current.Value.StyleId, current.Value.DisplayName,
                current.Value.SourceUrl, current.Value.SourceKind, contentXml);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            int changed = await connection.ExecuteAsync(
                """
                update csl_styles
                set display_name = @DisplayName,
                    default_locale = @DefaultLocale,
                    content_hash = @ContentHash,
                    updated_at = @UpdatedAt
                where style_id = @StyleId and deleted = 0 and content_hash = @ExpectedHash;
                """,
                new
                {
                    metadata.StyleId,
                    metadata.DisplayName,
                    metadata.DefaultLocale,
                    metadata.ContentHash,
                    UpdatedAt = metadata.UpdatedAt.ToUniversalTime().ToString("O"),
                    ExpectedHash = current.Value.ContentHash
                });
            if (changed != 1)
            {
                RestoreFile(path, backupPath);
                return Result<CslStyle>.Failure(AppErrorCodes.Conflict, "CSL style revision is stale.");
            }

            File.Delete(backupPath);
            return Result<CslStyle>.Success(metadata);
        }
        catch (OperationCanceledException)
        {
            if (replacedFile)
            {
                RestoreFile(path, backupPath);
            }

            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
            "infrastructure.csl-style-store", "replace-style"))
        {
            if (replacedFile && File.Exists(backupPath))
            {
                RestoreFile(path, backupPath);
            }

            return Result<CslStyle>.Failure(AppErrorCodes.DatabaseError,
                $"CSL style replacement failed: {exception.Message}");
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
    }

    private static void RestoreFile(string path, string backupPath)
    {
        if (File.Exists(backupPath))
        {
            File.Move(backupPath, path, overwrite: true);
        }
    }

    private static string? GetDeclaredStyleId(XDocument document)
    {
        XElement? root = document.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "style", StringComparison.Ordinal))
        {
            return null;
        }

        XNamespace ns = root.Name.Namespace;
        string? declaredId = root.Element(ns + "info")?.Element(ns + "id")?.Value?.Trim();
        return string.IsNullOrWhiteSpace(declaredId) ? null : declaredId;
    }

    public async Task<Result<CslStyle>> InstallStyleAsync(CslCatalogStyle catalogStyle, string contentXml,
        CancellationToken cancellationToken = default)
    {
        BlockingOperationId? installOperationId =
            await TryStartInstallOperationAsync(catalogStyle.StyleId, cancellationToken);
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

        CslStyle metadata = ParseMetadata(catalogStyle.StyleId, catalogStyle.DisplayName, catalogStyle.SourceUrl,
            catalogStyle.SourceKind, contentXml);
        Result<string> resolvedPath = ResolveStylePath(metadata.StyleId);
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

            await using SqliteConnection connection = _connectionFactory.CreateConnection();
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
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.csl-style-store"))
        {
            await TryFailInstallOperationAsync(
                installOperationId,
                AppErrorCodes.DatabaseError,
                $"CSL style install failed: {exception.Message}",
                cancellationToken);
            return Result<CslStyle>.Failure(AppErrorCodes.DatabaseError,
                $"CSL style install failed: {exception.Message}");
        }
    }

    public async Task<Result<CslStyle>> DisableStyleAsync(string styleId, CancellationToken cancellationToken = default)
    {
        Result<CslStyle> style = await GetStyleAsync(styleId, cancellationToken);
        if (style.IsFailure)
        {
            return Result<CslStyle>.Failure(style.ErrorCode!, style.ErrorMessage!);
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(
                "update csl_styles set enabled = 0, updated_at = @UpdatedAt where style_id = @StyleId;",
                new { StyleId = style.Value.StyleId, UpdatedAt = _clock.UtcNow.ToUniversalTime().ToString("O") });
            return Result<CslStyle>.Success(style.Value with
            {
                Enabled = false, UpdatedAt = _clock.UtcNow.ToUniversalTime()
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.csl-style-store"))
        {
            return Result<CslStyle>.Failure(AppErrorCodes.DatabaseError,
                $"CSL style disable failed: {exception.Message}");
        }
    }

    public async Task<Result> RemoveStyleAsync(string styleId, CancellationToken cancellationToken = default)
    {
        Result<CslStyle> style = await GetStyleAsync(styleId, cancellationToken);
        if (style.IsFailure)
        {
            return Result.Failure(style.ErrorCode!, style.ErrorMessage!);
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(
                "update csl_styles set deleted = 1, enabled = 0, updated_at = @UpdatedAt where style_id = @StyleId;",
                new { StyleId = style.Value.StyleId, UpdatedAt = _clock.UtcNow.ToUniversalTime().ToString("O") });
            Result<string> resolvedPath = ResolveStylePath(style.Value.StyleId);
            if (resolvedPath.IsFailure)
            {
                return Result.Failure(resolvedPath.ErrorCode!, resolvedPath.ErrorMessage!);
            }

            string path = resolvedPath.Value;
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
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.csl-style-store"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"CSL style removal failed: {exception.Message}");
        }
    }

    public async Task<Result<CslSettings>> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            SettingsRow? row = await connection.QuerySingleOrDefaultAsync<SettingsRow>(
                """
                select default_style_id as DefaultStyleId,
                       locale as Locale,
                       updated_at as UpdatedAt
                from csl_settings
                limit 1;
                """);
            return Result<CslSettings>.Success(row?.ToModel() ??
                                               new CslSettings(null, null, _clock.UtcNow.ToUniversalTime()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.csl-style-store"))
        {
            return Result<CslSettings>.Failure(AppErrorCodes.DatabaseError,
                $"CSL settings load failed: {exception.Message}");
        }
    }

    public async Task<Result<CslSettings>> SaveSettingsAsync(string? defaultStyleId, string? locale,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(defaultStyleId))
        {
            Result<CslStyle> style = await GetStyleAsync(defaultStyleId, cancellationToken);
            if (style.IsFailure)
            {
                return Result<CslSettings>.Failure(style.ErrorCode!, style.ErrorMessage!);
            }

            if (!style.Value.Enabled)
            {
                return Result<CslSettings>.Failure(AppErrorCodes.ValidationFailed,
                    "Disabled CSL styles cannot become the default style.");
            }
        }

        try
        {
            DateTimeOffset updatedAt = _clock.UtcNow.ToUniversalTime();
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
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
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.csl-style-store"))
        {
            return Result<CslSettings>.Failure(AppErrorCodes.DatabaseError,
                $"CSL settings save failed: {exception.Message}");
        }
    }

    private Result<string> ResolveStylePath(string styleId)
    {
        if (string.IsNullOrWhiteSpace(styleId))
        {
            return Result<string>.Failure(AppErrorCodes.ValidationFailed, "CSL style id is required.");
        }

        string normalized = styleId.Trim();
        if (normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || normalized.Contains(Path.DirectorySeparatorChar)
            || normalized.Contains(Path.AltDirectorySeparatorChar)
            || Path.IsPathRooted(normalized)
            || normalized is "." or ".."
            || normalized.Contains("..", StringComparison.Ordinal))
        {
            return Result<string>.Failure(AppErrorCodes.ValidationFailed,
                "CSL style id contains an invalid path segment.");
        }

        string installedRoot = Path.GetFullPath(_installedRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate = Path.GetFullPath(Path.Combine(installedRoot, $"{normalized}.csl"));
        if (!candidate.StartsWith(installedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return Result<string>.Failure(AppErrorCodes.ValidationFailed,
                "CSL style id resolves outside the installed style directory.");
        }

        return Result<string>.Success(candidate);
    }

    private CslStyle ParseMetadata(string styleId, string displayName, string? sourceUrl, string sourceKind,
        string contentXml)
    {
        DateTimeOffset now = _clock.UtcNow.ToUniversalTime();
        try
        {
            XDocument document = XDocument.Parse(contentXml);
            XElement? style = document.Root;
            XNamespace ns = style?.Name.Namespace ?? XNamespace.None;
            XElement? info = style?.Element(ns + "info");
            string? title = info?.Element(ns + "title")?.Value?.Trim();
            string? locale = style?.Attribute("default-locale")?.Value?.Trim();
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
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.csl-style-store", "complete-install-operation"))
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
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private async Task<BlockingOperationId?> TryStartInstallOperationAsync(string? styleId,
        CancellationToken cancellationToken)
    {
        if (_blockingOperations is null)
        {
            return null;
        }

        try
        {
            Result<BlockingOperation> started = await _blockingOperations.StartAsync(
                BlockingOperationTypes.CslStyleInstall,
                BlockingOperationScopeTypes.CslStyle,
                string.IsNullOrWhiteSpace(styleId) ? null : styleId.Trim(),
                false,
                "Installing CSL style.",
                nextActions: ["Retry style installation", "Choose a different style source"],
                cancellationToken: cancellationToken);
            return started.IsSuccess ? started.Value.OperationId : null;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.csl-style-store", "fail-install-operation"))
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
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.csl-style-store", "complete-install-operation"))
        {
            _ = exception;
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
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.csl-style-store", "fail-install-operation"))
        {
            _ = exception;
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
        {
            return new CslStyle(
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
    }

    private sealed class SettingsRow
    {
        public string? DefaultStyleId { get; set; }
        public string? Locale { get; set; }
        public string UpdatedAt { get; set; } = "";

        public CslSettings ToModel()
        {
            return new CslSettings(DefaultStyleId, Locale, DateTimeOffset.Parse(UpdatedAt));
        }
    }
}
