using Dapper;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.Bibliography;

public sealed class ItemTypeInferenceService : IItemTypeInferenceService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly ICslItemTypeProfileService _profiles;
    private readonly IItemService _items;

    public ItemTypeInferenceService(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        ICslItemTypeProfileService profiles,
        IItemService items)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _profiles = profiles;
        _items = items;
    }

    public async Task<Result<ItemTypeInference>> SuggestAsync(
        ItemId itemId,
        string suggestedType,
        double confidence,
        string source,
        string? evidenceSummary,
        CancellationToken cancellationToken = default)
    {
        if (confidence is < 0 or > 1)
        {
            return Result<ItemTypeInference>.Failure(AppErrorCodes.ValidationFailed, "Confidence must be between 0 and 1.");
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            return Result<ItemTypeInference>.Failure(AppErrorCodes.ValidationFailed, "Inference source is required.");
        }

        var typeValidation = await _profiles.ValidateItemTypeAsync(suggestedType, cancellationToken);
        if (typeValidation.IsFailure)
        {
            return Result<ItemTypeInference>.Failure(typeValidation.ErrorCode!, typeValidation.ErrorMessage!);
        }

        var item = await _items.GetItemAsync(itemId, cancellationToken);
        if (item.IsFailure)
        {
            return Result<ItemTypeInference>.Failure(item.ErrorCode!, item.ErrorMessage!);
        }

        try
        {
            var inference = new ItemTypeInference(
                Guid.NewGuid().ToString("D"),
                itemId,
                suggestedType.Trim(),
                confidence,
                source.Trim(),
                string.IsNullOrWhiteSpace(evidenceSummary) ? null : evidenceSummary.Trim(),
                _clock.UtcNow.ToUniversalTime(),
                null);

            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(
                """
                insert into item_type_inferences (
                    inference_id, item_id, suggested_type, confidence, source, evidence_summary, created_at, accepted_at
                )
                values (
                    @InferenceId, @ItemId, @SuggestedType, @Confidence, @Source, @EvidenceSummary, @CreatedAt, null
                );
                """,
                new
                {
                    InferenceId = inference.InferenceId,
                    ItemId = inference.ItemId.ToString(),
                    inference.SuggestedType,
                    inference.Confidence,
                    inference.Source,
                    inference.EvidenceSummary,
                    CreatedAt = inference.CreatedAt.ToString("O")
                });

            return Result<ItemTypeInference>.Success(inference);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result<ItemTypeInference>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<ItemTypeInference>>> ListSuggestionsAsync(
        ItemId itemId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var rows = await connection.QueryAsync<Row>(
                """
                select inference_id as InferenceId,
                       item_id as ItemId,
                       suggested_type as SuggestedType,
                       confidence as Confidence,
                       source as Source,
                       evidence_summary as EvidenceSummary,
                       created_at as CreatedAt,
                       accepted_at as AcceptedAt
                from item_type_inferences
                where item_id = @ItemId
                order by confidence desc, created_at desc, inference_id desc;
                """,
                new { ItemId = itemId.ToString() });

            return Result<IReadOnlyList<ItemTypeInference>>.Success(rows.Select(row => row.ToModel()).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result<IReadOnlyList<ItemTypeInference>>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<ItemTypeInference>> AcceptSuggestionAsync(
        string inferenceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inferenceId))
        {
            return Result<ItemTypeInference>.Failure(AppErrorCodes.ValidationFailed, "Inference id is required.");
        }

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var row = await connection.QuerySingleOrDefaultAsync<Row>(
                """
                select inference_id as InferenceId,
                       item_id as ItemId,
                       suggested_type as SuggestedType,
                       confidence as Confidence,
                       source as Source,
                       evidence_summary as EvidenceSummary,
                       created_at as CreatedAt,
                       accepted_at as AcceptedAt
                from item_type_inferences
                where inference_id = @InferenceId;
                """,
                new { InferenceId = inferenceId.Trim() },
                transaction);

            if (row is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<ItemTypeInference>.Failure(AppErrorCodes.NotFound, "Item type inference was not found.");
            }

            var acceptedAt = _clock.UtcNow.ToUniversalTime();
            var itemId = ItemId.Parse(row.ItemId);
            var updated = await connection.ExecuteAsync(
                """
                update items
                set item_type = @ItemType,
                    updated_at = @UpdatedAt
                where item_id = @ItemId and deleted_at is null;
                """,
                new
                {
                    ItemType = row.SuggestedType,
                    UpdatedAt = acceptedAt.ToString("O"),
                    ItemId = itemId.ToString()
                },
                transaction);
            if (updated == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<ItemTypeInference>.Failure(AppErrorCodes.NotFound, "Item for the inference was not found.");
            }

            await connection.ExecuteAsync(
                "update item_type_inferences set accepted_at = @AcceptedAt where inference_id = @InferenceId;",
                new { AcceptedAt = acceptedAt.ToString("O"), InferenceId = inferenceId.Trim() },
                transaction);

            await transaction.CommitAsync(cancellationToken);
            return Result<ItemTypeInference>.Success(row.ToModel() with { AcceptedAt = acceptedAt });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result<ItemTypeInference>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    private sealed class Row
    {
        public string InferenceId { get; set; } = "";
        public string ItemId { get; set; } = "";
        public string SuggestedType { get; set; } = "";
        public double Confidence { get; set; }
        public string Source { get; set; } = "";
        public string? EvidenceSummary { get; set; }
        public string CreatedAt { get; set; } = "";
        public string? AcceptedAt { get; set; }

        public ItemTypeInference ToModel()
            => new(
                InferenceId,
                Patchouli.Core.Ids.ItemId.Parse(ItemId),
                SuggestedType,
                Confidence,
                Source,
                EvidenceSummary,
                DateTimeOffset.Parse(CreatedAt),
                string.IsNullOrWhiteSpace(AcceptedAt) ? null : DateTimeOffset.Parse(AcceptedAt));
    }
}
