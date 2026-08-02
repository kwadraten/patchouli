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

    public async Task<Result> ArchivePresetAsync(OcrPresetId presetId, CancellationToken cancellationToken = default)
    {
        try
        {
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
