using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Ocr;

namespace Patchouli.Infrastructure.Ocr;

public sealed class OcrPresetService : IOcrPresetService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ILibraryIdentityService _libraryIdentityService;
    private readonly IClock _clock;

    public OcrPresetService(SqliteConnectionFactory connectionFactory, ILibraryIdentityService libraryIdentityService,
        IClock clock)
    {
        _connectionFactory = connectionFactory;
        _libraryIdentityService = libraryIdentityService;
        _clock = clock;
    }

    public async Task<Result<OcrPreset>> CreatePresetAsync(string name, string? description, string engineId,
        string modelId, string? modelPath, string parametersJson, bool applyOnSuccess,
        CancellationToken cancellationToken = default)
    {
        Result validation = ValidatePresetInput(name, engineId, modelId);
        if (validation.IsFailure)
        {
            return Result<OcrPreset>.Failure(validation.ErrorCode!, validation.ErrorMessage!);
        }

        Result<LibraryMetadata> library = await _libraryIdentityService.GetCurrentLibraryAsync(cancellationToken);
        if (library.IsFailure)
        {
            return Result<OcrPreset>.Failure(library.ErrorCode!, library.ErrorMessage!);
        }

        try
        {
            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            DateTimeOffset now = _clock.UtcNow.ToUniversalTime();
            OcrPresetId presetId = OcrPresetId.New();
            OcrPresetVersionId versionId = OcrPresetVersionId.New();
            OcrPreset preset = new(presetId, library.Value.LibraryId, name.Trim(), NullIfWhiteSpace(description),
                versionId, false, now, now);

            await connection.ExecuteAsync(
                """
                insert into ocr_presets (preset_id, library_id, name, description, current_version_id, archived, created_at, updated_at)
                values (@PresetId, @LibraryId, @Name, @Description, @CurrentVersionId, 0, @CreatedAt, @UpdatedAt);
                """,
                new
                {
                    PresetId = presetId.ToString(), LibraryId = library.Value.LibraryId.ToString(), Name = preset.Name,
                    preset.Description, CurrentVersionId = versionId.ToString(), CreatedAt = F(now), UpdatedAt = F(now)
                },
                transaction);

            await InsertVersionAsync(connection, transaction,
                new OcrPresetVersion(versionId, presetId, engineId.Trim(), modelId.Trim(), NullIfWhiteSpace(modelPath),
                    DefaultParameters(parametersJson), applyOnSuccess, now));
            await transaction.CommitAsync(cancellationToken);
            return Result<OcrPreset>.Success(preset);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.ocr-preset"))
        {
            return DbFail<OcrPreset>(ex);
        }
    }

    public async Task<Result<OcrPreset>> GetPresetAsync(OcrPresetId presetId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            PresetRow? row = await connection.QuerySingleOrDefaultAsync<PresetRow>(
                "select preset_id as PresetId, library_id as LibraryId, name as Name, description as Description, current_version_id as CurrentVersionId, archived as Archived, created_at as CreatedAt, updated_at as UpdatedAt from ocr_presets where preset_id = @PresetId;",
                new { PresetId = presetId.ToString() });
            return row is null
                ? Result<OcrPreset>.Failure(AppErrorCodes.NotFound, "OCR preset was not found.")
                : Result<OcrPreset>.Success(row.ToPreset());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.ocr-preset"))
        {
            return DbFail<OcrPreset>(ex);
        }
    }

    public async Task<Result<OcrPresetVersion>> CreatePresetVersionAsync(OcrPresetId presetId, string engineId,
        string modelId, string? modelPath, string parametersJson, bool applyOnSuccess,
        CancellationToken cancellationToken = default)
    {
        Result validation = ValidatePresetInput("version", engineId, modelId);
        if (validation.IsFailure)
        {
            return Result<OcrPresetVersion>.Failure(validation.ErrorCode!, validation.ErrorMessage!);
        }

        try
        {
            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            int presetExists = await connection.ExecuteScalarAsync<int>(
                "select count(1) from ocr_presets where preset_id = @PresetId and archived = 0;",
                new { PresetId = presetId.ToString() }, transaction);
            if (presetExists == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<OcrPresetVersion>.Failure(AppErrorCodes.NotFound, "Active OCR preset was not found.");
            }

            OcrPresetVersion version = new(OcrPresetVersionId.New(), presetId, engineId.Trim(), modelId.Trim(),
                NullIfWhiteSpace(modelPath), DefaultParameters(parametersJson), applyOnSuccess,
                _clock.UtcNow.ToUniversalTime());
            await InsertVersionAsync(connection, transaction, version);
            await connection.ExecuteAsync(
                "update ocr_presets set current_version_id = @VersionId, updated_at = @UpdatedAt where preset_id = @PresetId;",
                new
                {
                    VersionId = version.PresetVersionId.ToString(), UpdatedAt = F(version.CreatedAt),
                    PresetId = presetId.ToString()
                }, transaction);
            await transaction.CommitAsync(cancellationToken);
            return Result<OcrPresetVersion>.Success(version);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.ocr-preset"))
        {
            return DbFail<OcrPresetVersion>(ex);
        }
    }

    public async Task<Result<OcrPresetVersion>> GetCurrentVersionAsync(OcrPresetId presetId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            OcrPresetVersion? version = await GetCurrentVersionAsync(connection, presetId);
            return version is null
                ? Result<OcrPresetVersion>.Failure(AppErrorCodes.NotFound, "Active OCR preset/version was not found.")
                : Result<OcrPresetVersion>.Success(version);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.ocr-preset"))
        {
            return DbFail<OcrPresetVersion>(ex);
        }
    }

    public async Task<Result<OcrPreset?>> FindActivePresetByEngineIdAsync(string engineId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(engineId))
        {
            return Result<OcrPreset?>.Failure(AppErrorCodes.ValidationFailed, "OCR engine ID is required.");
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            PresetRow? row = await connection.QuerySingleOrDefaultAsync<PresetRow>(
                """
                select p.preset_id as PresetId,
                       p.library_id as LibraryId,
                       p.name as Name,
                       p.description as Description,
                       p.current_version_id as CurrentVersionId,
                       p.archived as Archived,
                       p.created_at as CreatedAt,
                       p.updated_at as UpdatedAt
                from ocr_presets p
                join ocr_preset_versions v on v.preset_version_id = p.current_version_id
                where p.archived = 0
                  and v.engine_id = @EngineId
                order by p.updated_at desc
                limit 1;
                """,
                new { EngineId = engineId.Trim() });
            return Result<OcrPreset?>.Success(row?.ToPreset());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.ocr-preset"))
        {
            return DbFail<OcrPreset?>(exception);
        }
    }

    public async Task<Result<OcrPresetVersion>> RebindModelPathAsync(OcrPresetId presetId, string newModelPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newModelPath))
        {
            return Result<OcrPresetVersion>.Failure(AppErrorCodes.ValidationFailed,
                "A model path or endpoint is required for rebind.");
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            OcrPresetVersion? current = await GetCurrentVersionAsync(connection, presetId);
            if (current is null)
            {
                return Result<OcrPresetVersion>.Failure(AppErrorCodes.NotFound,
                    "Active OCR preset/version was not found.");
            }

            // A rebind is a provenance change: preserve the old version and make a new immutable version current.
            return await CreatePresetVersionAsync(presetId, current.EngineId, current.ModelId, newModelPath.Trim(),
                current.ParametersJson, current.ApplyOnSuccess, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.ocr-preset"))
        {
            return DbFail<OcrPresetVersion>(ex);
        }
    }

    public async Task<Result<IReadOnlyList<OcrPreset>>> ListPresetsAsync(bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Result<LibraryMetadata> library =
                await _libraryIdentityService.GetCurrentLibraryAsync(cancellationToken);
            if (library.IsFailure)
            {
                return Result<IReadOnlyList<OcrPreset>>.Failure(library.ErrorCode!,
                    library.ErrorMessage!);
            }

            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            IEnumerable<PresetRow> rows = await connection.QueryAsync<PresetRow>(
                """
                select preset_id as PresetId, library_id as LibraryId, name as Name, description as Description,
                       current_version_id as CurrentVersionId, archived as Archived,
                       created_at as CreatedAt, updated_at as UpdatedAt
                from ocr_presets
                where library_id = @LibraryId and (@IncludeArchived = 1 or archived = 0)
                order by archived asc, updated_at desc;
                """,
                new { LibraryId = library.Value.LibraryId.ToString(), IncludeArchived = includeArchived ? 1 : 0 });
            return Result<IReadOnlyList<OcrPreset>>.Success(
                rows.Select(row => row.ToPreset()).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.ocr-preset"))
        {
            return DbFail<IReadOnlyList<OcrPreset>>(ex);
        }
    }

    public async Task<Result<OcrPreset>> UpdatePresetAsync(OcrPresetId presetId, string name,
        string? description, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result<OcrPreset>.Failure(AppErrorCodes.ValidationFailed, "OCR preset name is required.");
        }

        try
        {
            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            DateTimeOffset now = _clock.UtcNow.ToUniversalTime();
            int affected = await connection.ExecuteAsync(
                "update ocr_presets set name = @Name, description = @Description, updated_at = @UpdatedAt where preset_id = @PresetId;",
                new
                {
                    PresetId = presetId.ToString(), Name = name.Trim(), Description = NullIfWhiteSpace(description),
                    UpdatedAt = F(now)
                }, transaction);
            if (affected == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<OcrPreset>.Failure(AppErrorCodes.NotFound, "OCR preset was not found.");
            }

            await transaction.CommitAsync(cancellationToken);
            return await GetPresetAsync(presetId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.ocr-preset"))
        {
            return DbFail<OcrPreset>(ex);
        }
    }

    public async Task<Result<OcrPreset>> SaveCompositionAsync(OcrPresetId presetId, string name,
        string? description, OcrPresetVersionId? expectedCurrentVersionId,
        OcrPresetVersionWrite? newVersion, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result<OcrPreset>.Failure(AppErrorCodes.ValidationFailed, "OCR preset name is required.");
        }

        if (newVersion is not null && (string.IsNullOrWhiteSpace(newVersion.EngineId) ||
                                       string.IsNullOrWhiteSpace(newVersion.ModelId)))
        {
            return Result<OcrPreset>.Failure(AppErrorCodes.ValidationFailed, "OCR engine and model are required.");
        }

        try
        {
            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            PresetRow? row = await connection.QuerySingleOrDefaultAsync<PresetRow>(
                "select preset_id as PresetId, library_id as LibraryId, name as Name, description as Description, current_version_id as CurrentVersionId, archived as Archived, created_at as CreatedAt, updated_at as UpdatedAt from ocr_presets where preset_id = @PresetId;",
                new { PresetId = presetId.ToString() }, transaction);
            if (row is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<OcrPreset>.Failure(AppErrorCodes.NotFound, "OCR preset was not found.");
            }

            if (row.Archived == 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<OcrPreset>.Failure(AppErrorCodes.ValidationFailed,
                    "OCR preset is archived and cannot be saved.");
            }

            string? currentVersionId = row.CurrentVersionId;
            if (expectedCurrentVersionId is not null && !string.Equals(currentVersionId,
                    expectedCurrentVersionId.Value.ToString(), StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<OcrPreset>.Failure(AppErrorCodes.Conflict,
                    "OCR preset changed since it was loaded; refresh the preset list and retry the save.");
            }

            DateTimeOffset now = _clock.UtcNow.ToUniversalTime();
            string? versionId = currentVersionId;
            if (newVersion is not null)
            {
                OcrPresetVersion version = new(OcrPresetVersionId.New(), presetId,
                    newVersion.EngineId.Trim(), newVersion.ModelId.Trim(),
                    NullIfWhiteSpace(newVersion.ModelPath), DefaultParameters(newVersion.ParametersJson),
                    newVersion.ApplyOnSuccess, now);
                await InsertVersionAsync(connection, transaction, version);
                versionId = version.PresetVersionId.ToString();
            }

            await connection.ExecuteAsync(
                "update ocr_presets set name = @Name, description = @Description, current_version_id = @CurrentVersionId, updated_at = @UpdatedAt where preset_id = @PresetId;",
                new
                {
                    PresetId = presetId.ToString(), Name = name.Trim(),
                    Description = NullIfWhiteSpace(description), CurrentVersionId = versionId,
                    UpdatedAt = F(now)
                }, transaction);
            await transaction.CommitAsync(cancellationToken);
            return await GetPresetAsync(presetId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.ocr-preset"))
        {
            return DbFail<OcrPreset>(ex);
        }
    }

    public async Task<Result<OcrPresetVersion>> GetPresetVersionAsync(OcrPresetVersionId presetVersionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            VersionRow? row = await connection.QuerySingleOrDefaultAsync<VersionRow>(
                "select preset_version_id as PresetVersionId, preset_id as PresetId, engine_id as EngineId, model_id as ModelId, model_path as ModelPath, parameters_json as ParametersJson, apply_on_success as ApplyOnSuccess, created_at as CreatedAt from ocr_preset_versions where preset_version_id = @PresetVersionId;",
                new { PresetVersionId = presetVersionId.ToString() });
            return row is null
                ? Result<OcrPresetVersion>.Failure(AppErrorCodes.NotFound,
                    "OCR preset version was not found.")
                : Result<OcrPresetVersion>.Success(row.ToVersion());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.ocr-preset"))
        {
            return DbFail<OcrPresetVersion>(ex);
        }
    }

    public async Task<Result<IReadOnlyList<OcrPresetVersion>>> ListVersionsAsync(OcrPresetId presetId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            IEnumerable<VersionRow> rows = await connection.QueryAsync<VersionRow>(
                "select preset_version_id as PresetVersionId, preset_id as PresetId, engine_id as EngineId, model_id as ModelId, model_path as ModelPath, parameters_json as ParametersJson, apply_on_success as ApplyOnSuccess, created_at as CreatedAt from ocr_preset_versions where preset_id = @PresetId order by created_at desc;",
                new { PresetId = presetId.ToString() });
            return Result<IReadOnlyList<OcrPresetVersion>>.Success(
                rows.Select(row => row.ToVersion()).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.ocr-preset"))
        {
            return DbFail<IReadOnlyList<OcrPresetVersion>>(ex);
        }
    }

    public async Task<Result<OcrPreset>> ResolveSelectionAsync(OcrPresetId presetId,
        CancellationToken cancellationToken = default)
    {
        Result<OcrPreset> preset = await GetPresetAsync(presetId, cancellationToken);
        if (preset.IsFailure)
        {
            return preset;
        }

        if (preset.Value.Archived)
        {
            return Result<OcrPreset>.Failure(AppErrorCodes.ValidationFailed,
                $"OCR preset '{preset.Value.Name}' is archived; choose another preset.");
        }

        Result<LibraryMetadata> library =
            await _libraryIdentityService.GetCurrentLibraryAsync(cancellationToken);
        if (library.IsFailure)
        {
            return Result<OcrPreset>.Failure(library.ErrorCode!, library.ErrorMessage!);
        }

        return preset.Value.LibraryId != library.Value.LibraryId
            ? Result<OcrPreset>.Failure(AppErrorCodes.ValidationFailed,
                $"OCR preset '{preset.Value.Name}' belongs to another library; explicit preset selections are library-scoped.")
            : preset;
    }

    public async Task<Result> ArchivePresetAsync(OcrPresetId presetId, CancellationToken cancellationToken = default)
    {
        try
        {
            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            int affected = await connection.ExecuteAsync(
                "update ocr_presets set archived = 1, updated_at = @UpdatedAt where preset_id = @PresetId;",
                new { PresetId = presetId.ToString(), UpdatedAt = F(_clock.UtcNow) });
            return affected == 0
                ? Result.Failure(AppErrorCodes.NotFound, "OCR preset was not found.")
                : Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.ocr-preset"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}");
        }
    }

    internal static async Task<OcrPresetVersion?> GetCurrentVersionAsync(
        SqliteConnection connection, OcrPresetId presetId,
        DbTransaction? transaction = null)
    {
        VersionRow? row = await connection.QuerySingleOrDefaultAsync<VersionRow>(
            """
            select v.preset_version_id as PresetVersionId, v.preset_id as PresetId, v.engine_id as EngineId, v.model_id as ModelId,
                   v.model_path as ModelPath, v.parameters_json as ParametersJson, v.apply_on_success as ApplyOnSuccess, v.created_at as CreatedAt
            from ocr_presets p
            join ocr_preset_versions v on v.preset_version_id = p.current_version_id
            where p.preset_id = @PresetId and p.archived = 0;
            """,
            new { PresetId = presetId.ToString() },
            transaction);
        return row?.ToVersion();
    }

    private static Task InsertVersionAsync(SqliteConnection connection,
        DbTransaction transaction, OcrPresetVersion version)
    {
        return connection.ExecuteAsync(
            "insert into ocr_preset_versions (preset_version_id, preset_id, engine_id, model_id, model_path, parameters_json, apply_on_success, created_at) values (@PresetVersionId, @PresetId, @EngineId, @ModelId, @ModelPath, @ParametersJson, @ApplyOnSuccess, @CreatedAt);",
            new
            {
                PresetVersionId = version.PresetVersionId.ToString(), PresetId = version.PresetId.ToString(),
                version.EngineId, version.ModelId, version.ModelPath, version.ParametersJson,
                ApplyOnSuccess = version.ApplyOnSuccess ? 1 : 0, CreatedAt = F(version.CreatedAt)
            }, transaction);
    }

    private static Result ValidatePresetInput(string name, string engineId, string modelId)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "OCR preset name is required.");
        }

        if (string.IsNullOrWhiteSpace(engineId) || string.IsNullOrWhiteSpace(modelId))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "OCR engine and model are required.");
        }

        return Result.Success();
    }

    private static string DefaultParameters(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "{}" : value;
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string F(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O");
    }

    private static Result<T> DbFail<T>(Exception ex)
    {
        return Result<T>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}");
    }

    private sealed class PresetRow
    {
        public string PresetId { get; set; } = "";
        public string LibraryId { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public string? CurrentVersionId { get; set; }
        public int Archived { get; set; }
        public string CreatedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";

        public OcrPreset ToPreset()
        {
            return new OcrPreset(OcrPresetId.Parse(PresetId), Patchouli.Core.Ids.LibraryId.Parse(LibraryId), Name,
                Description,
                CurrentVersionId is null ? null : OcrPresetVersionId.Parse(CurrentVersionId), Archived == 1,
                DateTimeOffset.Parse(CreatedAt), DateTimeOffset.Parse(UpdatedAt));
        }
    }

    private sealed class VersionRow
    {
        public string PresetVersionId { get; set; } = "";
        public string PresetId { get; set; } = "";
        public string EngineId { get; set; } = "";
        public string ModelId { get; set; } = "";
        public string? ModelPath { get; set; }
        public string ParametersJson { get; set; } = "{}";
        public int ApplyOnSuccess { get; set; }
        public string CreatedAt { get; set; } = "";

        public OcrPresetVersion ToVersion()
        {
            return new OcrPresetVersion(OcrPresetVersionId.Parse(PresetVersionId), OcrPresetId.Parse(PresetId),
                EngineId, ModelId,
                ModelPath, ParametersJson, ApplyOnSuccess == 1, DateTimeOffset.Parse(CreatedAt));
        }
    }
}
