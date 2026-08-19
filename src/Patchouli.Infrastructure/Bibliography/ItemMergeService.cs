using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Ocr;

namespace Patchouli.Infrastructure.Bibliography;

public sealed class ItemMergeService : IItemMergeService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly ILibraryIdentityService _libraryIdentityService;
    private readonly ILibraryRevisionService? _revisions;

    public ItemMergeService(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        ILibraryIdentityService libraryIdentityService,
        ILibraryRevisionService? revisions = null)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _libraryIdentityService = libraryIdentityService;
        _revisions = revisions;
    }

    public async Task<Result<ItemMergePreview>> BuildMergePreviewAsync(
        ItemId sourceId,
        ItemId targetId,
        CancellationToken cancellationToken = default)
    {
        if (sourceId == targetId)
        {
            return Result<ItemMergePreview>.Failure(AppErrorCodes.ValidationFailed,
                "Cannot merge an item into itself.");
        }

        Result<LibraryMetadata> libraryResult = await _libraryIdentityService.GetCurrentLibraryAsync(cancellationToken);
        if (libraryResult.IsFailure)
        {
            return Result<ItemMergePreview>.Failure(libraryResult.ErrorCode!, libraryResult.ErrorMessage!);
        }

        try
        {
            Result<ItemMetadata> sourceResult = await GetActiveItemAsync(sourceId, cancellationToken);
            if (sourceResult.IsFailure)
            {
                return Result<ItemMergePreview>.Failure(sourceResult.ErrorCode!, sourceResult.ErrorMessage!);
            }

            Result<ItemMetadata> targetResult = await GetActiveItemAsync(targetId, cancellationToken);
            if (targetResult.IsFailure)
            {
                return Result<ItemMergePreview>.Failure(targetResult.ErrorCode!, targetResult.ErrorMessage!);
            }

            ItemMetadata source = sourceResult.Value;
            ItemMetadata target = targetResult.Value;

            List<ItemMergeConflictField> conflicts = new();
            List<ItemMergeMissingField> missing = new();

            AddStringField(conflicts, missing, "title", "题名", target.Title, source.Title, target.Title, source.Title);
            AddCreatorsField(conflicts, missing, target, source);
            AddYearField(conflicts, missing, target, source);
            AddStringField(
                conflicts,
                missing,
                "citation_key",
                "Citation Key",
                target.CitationKey,
                source.CitationKey,
                target.CitationKey,
                source.CitationKey);
            AddIdentifiersField(conflicts, missing, target, source);

            AddMissingScalarFields(missing, target, source);

            IReadOnlyList<string> tagUnion = TagNormalizer.NormalizeMany(
                ParseTags(target.TagsJson).Concat(ParseTags(source.TagsJson)));

            int documentsToTransfer = await CountDocumentInstancesAsync(sourceId, cancellationToken);

            return Result<ItemMergePreview>.Success(new ItemMergePreview(
                sourceId,
                targetId,
                source.Title,
                target.Title,
                conflicts,
                missing,
                tagUnion,
                documentsToTransfer));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.item-merge"))
        {
            return Result<ItemMergePreview>.Failure(
                AppErrorCodes.DatabaseError,
                $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result> MergeAsync(
        ItemId sourceId,
        ItemId targetId,
        IReadOnlyList<MergeFieldChoice> choices,
        Func<ItemId, bool> hasUnsavedEdits,
        CancellationToken cancellationToken = default)
    {
        if (sourceId == targetId)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "Cannot merge an item into itself.");
        }

        Result<LibraryMetadata> libraryResult = await _libraryIdentityService.GetCurrentLibraryAsync(cancellationToken);
        if (libraryResult.IsFailure)
        {
            return Result.Failure(libraryResult.ErrorCode!, libraryResult.ErrorMessage!);
        }

        Result<ItemMetadata> sourceResult = await GetActiveItemAsync(sourceId, cancellationToken);
        if (sourceResult.IsFailure)
        {
            return Result.Failure(sourceResult.ErrorCode!, sourceResult.ErrorMessage!);
        }

        Result<ItemMetadata> targetResult = await GetActiveItemAsync(targetId, cancellationToken);
        if (targetResult.IsFailure)
        {
            return Result.Failure(targetResult.ErrorCode!, targetResult.ErrorMessage!);
        }

        if (hasUnsavedEdits(sourceId) || hasUnsavedEdits(targetId))
        {
            return Result.Failure(AppErrorCodes.InvalidState, "Cannot merge items with unsaved edits.");
        }

        try
        {
            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            string[] documentInstanceIds = (await connection.QueryAsync<string>(
                "select document_instance_id from document_instances where item_id in @ItemIds;",
                new { ItemIds = new[] { sourceId.ToString(), targetId.ToString() } })).ToArray();

            if (documentInstanceIds.Length > 0)
            {
                int activeOcrCount = await connection.ExecuteScalarAsync<int>(
                    """
                    select count(1)
                    from ocr_runs
                    where document_instance_id in @DocumentIds
                      and state in (@Pending, @Running);
                    """,
                    new
                    {
                        DocumentIds = documentInstanceIds,
                        Pending = OcrRunState.Pending,
                        Running = OcrRunState.Running
                    });

                if (activeOcrCount > 0)
                {
                    return Result.Failure(
                        AppErrorCodes.InvalidState,
                        "Cannot merge items while OCR runs are pending or running.");
                }
            }

            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            ItemMetadata source = sourceResult.Value;
            ItemMetadata target = targetResult.Value;
            Dictionary<string, bool> choiceByField = choices.ToDictionary(
                static choice => choice.FieldName,
                static choice => choice.UseSourceValue,
                StringComparer.Ordinal);

            DateTimeOffset now = _clock.UtcNow.ToUniversalTime();
            ItemMetadata merged = BuildMergedItem(target, source, choiceByField, now);
            string freedSourceCitationKey = "merged-" + sourceId.Value.ToString("N");

            // Free the source citation_key before writing the target so a chosen source key
            // cannot violate the global unique index while the redirect tombstone still exists.
            await connection.ExecuteAsync(
                """
                update items
                set citation_key = @CitationKey,
                    updated_at = @UpdatedAt
                where item_id = @SourceId
                  and deleted_at is null
                  and merged_into_item_id is null;
                """,
                new
                {
                    SourceId = sourceId.ToString(),
                    CitationKey = freedSourceCitationKey,
                    UpdatedAt = FormatUtc(now)
                },
                transaction);

            await connection.ExecuteAsync(
                """
                update items
                set item_type = @ItemType,
                    citation_key = @CitationKey,
                    title = @Title,
                    subtitle = @Subtitle,
                    title_short = @TitleShort,
                    creators_json = @CreatorsJson,
                    date = @Date,
                    publication_title = @PublicationTitle,
                    container_title_short = @ContainerTitleShort,
                    collection_title = @CollectionTitle,
                    publisher = @Publisher,
                    place = @Place,
                    edition = @Edition,
                    genre = @Genre,
                    number = @Number,
                    chapter_number = @ChapterNumber,
                    volume = @Volume,
                    version = @Version,
                    issue = @Issue,
                    pages = @Pages,
                    language = @Language,
                    status = @Status,
                    note = @Note,
                    abstract = @Abstract,
                    tags_json = @TagsJson,
                    collections_json = @CollectionsJson,
                    custom_fields_json = @CustomFieldsJson,
                    updated_at = @UpdatedAt
                where item_id = @ItemId
                  and deleted_at is null
                  and merged_into_item_id is null;
                """,
                ToParameters(merged),
                transaction);

            await ReplaceCreatorsAsync(connection, transaction, targetId, merged.Creators, now);
            await ReplaceDatesAsync(connection, transaction, targetId, merged.Dates, now);
            await ReplaceIdentifiersAsync(connection, transaction, targetId, merged.Identifiers, now);

            await connection.ExecuteAsync(
                "update document_instances set item_id = @TargetId where item_id = @SourceId;",
                new { TargetId = targetId.ToString(), SourceId = sourceId.ToString() },
                transaction);

            await connection.ExecuteAsync(
                """
                update items
                set merged_into_item_id = @TargetId,
                    updated_at = @UpdatedAt
                where item_id = @SourceId
                  and deleted_at is null
                  and merged_into_item_id is null;
                """,
                new
                {
                    SourceId = sourceId.ToString(),
                    TargetId = targetId.ToString(),
                    UpdatedAt = FormatUtc(now)
                },
                transaction);

            Result<LibraryChangeSet?> revision = await IncrementRevisionAsync(
                connection,
                transaction,
                LibraryChangeSet.Empty with { ItemIds = [sourceId, targetId] },
                cancellationToken);
            if (revision.IsFailure)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure(revision.ErrorCode!, revision.ErrorMessage!);
            }

            await transaction.CommitAsync(cancellationToken);
            PublishRevision(revision.Value);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.item-merge"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    private async Task<Result<ItemMetadata>> GetActiveItemAsync(ItemId itemId, CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            ItemRow? row = await connection.QuerySingleOrDefaultAsync<ItemRow>(
                """
                select
                    item_id as ItemId,
                    library_id as LibraryId,
                    item_type as ItemType,
                    citation_key as CitationKey,
                    title as Title,
                    subtitle as Subtitle,
                    title_short as TitleShort,
                    creators_json as CreatorsJson,
                    date as Date,
                    publication_title as PublicationTitle,
                    container_title_short as ContainerTitleShort,
                    collection_title as CollectionTitle,
                    publisher as Publisher,
                    place as Place,
                    edition as Edition,
                    genre as Genre,
                    number as Number,
                    chapter_number as ChapterNumber,
                    volume as Volume,
                    version as Version,
                    issue as Issue,
                    pages as Pages,
                    language as Language,
                    status as Status,
                    note as Note,
                    abstract as Abstract,
                    tags_json as TagsJson,
                    collections_json as CollectionsJson,
                    custom_fields_json as CustomFieldsJson,
                    created_at as CreatedAt,
                    updated_at as UpdatedAt,
                    deleted_at as DeletedAt,
                    merged_into_item_id as MergedIntoItemId
                from items
                where item_id = @ItemId
                  and deleted_at is null
                  and merged_into_item_id is null;
                """,
                new { ItemId = itemId.ToString() });

            if (row is null)
            {
                return Result<ItemMetadata>.Failure(AppErrorCodes.NotFound, "Item was not found.");
            }

            ItemId id = row.ToItemId();
            Dictionary<ItemId, IReadOnlyList<ItemCreator>> creators =
                await LoadCreatorsAsync(connection, [id], cancellationToken);
            Dictionary<ItemId, IReadOnlyList<ItemDate>> dates =
                await LoadDatesAsync(connection, [id], cancellationToken);
            Dictionary<ItemId, IReadOnlyList<ItemIdentifier>> identifiers =
                await LoadIdentifiersAsync(connection, [id], cancellationToken);

            return Result<ItemMetadata>.Success(row.ToMetadata(
                creators.GetValueOrDefault(id) ?? LegacyCreators(row),
                dates.GetValueOrDefault(id) ?? LegacyDates(row),
                identifiers.GetValueOrDefault(id) ?? Array.Empty<ItemIdentifier>()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.item-merge"))
        {
            return Result<ItemMetadata>.Failure(
                AppErrorCodes.DatabaseError,
                $"Database operation failed: {exception.Message}");
        }
    }

    private static async Task<Dictionary<ItemId, IReadOnlyList<ItemCreator>>> LoadCreatorsAsync(
        SqliteConnection connection,
        IReadOnlyList<ItemId> itemIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (itemIds.Count == 0)
        {
            return new Dictionary<ItemId, IReadOnlyList<ItemCreator>>();
        }

        IEnumerable<CreatorRow> rows = await connection.QueryAsync<CreatorRow>(
            """
            select
                creator_id as CreatorId,
                item_id as ItemId,
                role as Role,
                family as Family,
                given as Given,
                literal as Literal,
                suffix as Suffix,
                particles as Particles,
                sequence_index as SequenceIndex,
                created_at as CreatedAt
            from item_creators
            where item_id in @ItemIds
            order by item_id, role, sequence_index, creator_id;
            """,
            new { ItemIds = itemIds.Select(id => id.ToString()).ToArray() });

        return rows
            .GroupBy(row => ItemId.Parse(row.ItemId))
            .ToDictionary(group => group.Key,
                group => (IReadOnlyList<ItemCreator>)group.Select(row => row.ToCreator()).ToArray());
    }

    private static async Task<Dictionary<ItemId, IReadOnlyList<ItemDate>>> LoadDatesAsync(
        SqliteConnection connection,
        IReadOnlyList<ItemId> itemIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (itemIds.Count == 0)
        {
            return new Dictionary<ItemId, IReadOnlyList<ItemDate>>();
        }

        IEnumerable<DateRow> rows = await connection.QueryAsync<DateRow>(
            """
            select
                date_id as DateId,
                item_id as ItemId,
                role as Role,
                date_parts_json as DatePartsJson,
                circa as Circa,
                season as Season,
                literal as Literal,
                created_at as CreatedAt
            from item_dates
            where item_id in @ItemIds
            order by item_id,
                     case role when 'issued' then 0 when 'accessed' then 1 else 2 end,
                     role;
            """,
            new { ItemIds = itemIds.Select(id => id.ToString()).ToArray() });

        return rows
            .GroupBy(row => ItemId.Parse(row.ItemId))
            .ToDictionary(group => group.Key,
                group => (IReadOnlyList<ItemDate>)group.Select(row => row.ToDate()).ToArray());
    }

    private static async Task<Dictionary<ItemId, IReadOnlyList<ItemIdentifier>>> LoadIdentifiersAsync(
        SqliteConnection connection,
        IReadOnlyList<ItemId> itemIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (itemIds.Count == 0)
        {
            return new Dictionary<ItemId, IReadOnlyList<ItemIdentifier>>();
        }

        IEnumerable<IdentifierRow> rows = await connection.QueryAsync<IdentifierRow>(
            """
            select
                identifier_id as IdentifierId,
                item_id as ItemId,
                scheme as Scheme,
                value as Value,
                note as Note,
                created_at as CreatedAt
            from item_identifiers
            where item_id in @ItemIds
            order by item_id, created_at, identifier_id;
            """,
            new { ItemIds = itemIds.Select(id => id.ToString()).ToArray() });

        return rows
            .GroupBy(row => ItemId.Parse(row.ItemId))
            .ToDictionary(group => group.Key,
                group => (IReadOnlyList<ItemIdentifier>)group.Select(row => row.ToIdentifier()).ToArray());
    }

    private async Task<int> CountDocumentInstancesAsync(ItemId itemId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
        await connection.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(
            "select count(1) from document_instances where item_id = @ItemId;",
            new { ItemId = itemId.ToString() });
    }

    private static void AddStringField(
        List<ItemMergeConflictField> conflicts,
        List<ItemMergeMissingField> missing,
        string fieldName,
        string label,
        string? targetValue,
        string? sourceValue,
        string targetDisplay,
        string sourceDisplay)
    {
        string? target = NullIfWhiteSpace(targetValue);
        string? source = NullIfWhiteSpace(sourceValue);

        if (target is null)
        {
            if (source is not null)
            {
                missing.Add(new ItemMergeMissingField(fieldName, label, sourceDisplay));
            }

            return;
        }

        if (source is null)
        {
            return;
        }

        if (!string.Equals(target, source, StringComparison.Ordinal))
        {
            conflicts.Add(new ItemMergeConflictField(fieldName, label, targetDisplay, sourceDisplay, target));
        }
    }

    private static void AddCreatorsField(
        List<ItemMergeConflictField> conflicts,
        List<ItemMergeMissingField> missing,
        ItemMetadata target,
        ItemMetadata source)
    {
        string targetText = FormatCreators(target.Creators);
        string sourceText = FormatCreators(source.Creators);
        bool targetHasCreators = target.Creators.Count > 0;
        bool sourceHasCreators = source.Creators.Count > 0;

        if (!targetHasCreators && sourceHasCreators)
        {
            missing.Add(new ItemMergeMissingField("creators", "作者", sourceText));
            return;
        }

        if (!targetHasCreators || !sourceHasCreators)
        {
            return;
        }

        string normalizedTarget = NormalizeCreatorsForComparison(target.Creators);
        string normalizedSource = NormalizeCreatorsForComparison(source.Creators);
        if (!string.Equals(normalizedTarget, normalizedSource, StringComparison.Ordinal))
        {
            conflicts.Add(new ItemMergeConflictField("creators", "作者", targetText, sourceText, targetText));
        }
    }

    private static void AddYearField(
        List<ItemMergeConflictField> conflicts,
        List<ItemMergeMissingField> missing,
        ItemMetadata target,
        ItemMetadata source)
    {
        string? targetYear = ExtractYear(target);
        string? sourceYear = ExtractYear(source);
        string targetText = targetYear ?? "";
        string sourceText = sourceYear ?? "";

        if (string.IsNullOrWhiteSpace(targetYear))
        {
            if (!string.IsNullOrWhiteSpace(sourceYear))
            {
                missing.Add(new ItemMergeMissingField("year", "年份", sourceText));
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(sourceYear))
        {
            return;
        }

        if (!string.Equals(targetYear, sourceYear, StringComparison.Ordinal))
        {
            conflicts.Add(new ItemMergeConflictField("year", "年份", targetText, sourceText, targetText));
        }
    }

    private static void AddIdentifiersField(
        List<ItemMergeConflictField> conflicts,
        List<ItemMergeMissingField> missing,
        ItemMetadata target,
        ItemMetadata source)
    {
        string targetText = FormatIdentifiers(target.Identifiers);
        string sourceText = FormatIdentifiers(source.Identifiers);
        bool targetHasIdentifiers = target.Identifiers.Count > 0;
        bool sourceHasIdentifiers = source.Identifiers.Count > 0;

        if (!targetHasIdentifiers && sourceHasIdentifiers)
        {
            missing.Add(new ItemMergeMissingField("identifiers", "标识符", sourceText));
            return;
        }

        if (!targetHasIdentifiers || !sourceHasIdentifiers)
        {
            return;
        }

        string normalizedTarget = NormalizeIdentifiersForComparison(target.Identifiers);
        string normalizedSource = NormalizeIdentifiersForComparison(source.Identifiers);
        if (!string.Equals(normalizedTarget, normalizedSource, StringComparison.Ordinal))
        {
            conflicts.Add(new ItemMergeConflictField("identifiers", "标识符", targetText, sourceText, targetText));
        }
    }

    private static void AddMissingScalarFields(List<ItemMergeMissingField> missing, ItemMetadata target,
        ItemMetadata source)
    {
        TryAddMissing(missing, "subtitle", "副标题", target.Subtitle, source.Subtitle);
        TryAddMissing(missing, "title_short", "短标题", target.TitleShort, source.TitleShort);
        TryAddMissing(missing, "publication_title", "出版物名称", target.PublicationTitle, source.PublicationTitle);
        TryAddMissing(missing, "container_title_short", "出版物简称", target.ContainerTitleShort,
            source.ContainerTitleShort);
        TryAddMissing(missing, "collection_title", "丛书", target.CollectionTitle, source.CollectionTitle);
        TryAddMissing(missing, "publisher", "出版社/授予机构", target.Publisher, source.Publisher);
        TryAddMissing(missing, "place", "出版地", target.Place, source.Place);
        TryAddMissing(missing, "edition", "版本", target.Edition, source.Edition);
        TryAddMissing(missing, "genre", "体裁", target.Genre, source.Genre);
        TryAddMissing(missing, "number", "编号", target.Number, source.Number);
        TryAddMissing(missing, "chapter_number", "章节号", target.ChapterNumber, source.ChapterNumber);
        TryAddMissing(missing, "volume", "卷", target.Volume, source.Volume);
        TryAddMissing(missing, "version", "版本号", target.Version, source.Version);
        TryAddMissing(missing, "issue", "期", target.Issue, source.Issue);
        TryAddMissing(missing, "pages", "页码", target.Pages, source.Pages);
        TryAddMissing(missing, "language", "语言", target.Language, source.Language);
        TryAddMissing(missing, "status", "状态", target.Status, source.Status);
        TryAddMissing(missing, "note", "备注", target.Note, source.Note);
        TryAddMissing(missing, "abstract", "摘要", target.Abstract, source.Abstract);

        if (IsEmptyCustomFields(target.CustomFieldsJson) && !IsEmptyCustomFields(source.CustomFieldsJson))
        {
            missing.Add(new ItemMergeMissingField("custom_fields", "自定义字段", source.CustomFieldsJson));
        }
    }

    private static void TryAddMissing(
        List<ItemMergeMissingField> missing,
        string fieldName,
        string label,
        string? targetValue,
        string? sourceValue)
    {
        if (string.IsNullOrWhiteSpace(targetValue) && !string.IsNullOrWhiteSpace(sourceValue))
        {
            missing.Add(new ItemMergeMissingField(fieldName, label, sourceValue));
        }
    }

    private static ItemMetadata BuildMergedItem(
        ItemMetadata target,
        ItemMetadata source,
        Dictionary<string, bool> choiceByField,
        DateTimeOffset now)
    {
        bool SourceChosen(string fieldName)
        {
            return choiceByField.TryGetValue(fieldName, out bool useSource) && useSource;
        }

        string? ChooseString(string? targetValue, string? sourceValue, string fieldName)
        {
            if (SourceChosen(fieldName))
            {
                return NullIfWhiteSpace(sourceValue);
            }

            return string.IsNullOrWhiteSpace(targetValue) ? NullIfWhiteSpace(sourceValue) : targetValue;
        }

        string title = SourceChosen("title") ? source.Title : target.Title;
        string citationKey = SourceChosen("citation_key")
            ? source.CitationKey
            : string.IsNullOrWhiteSpace(target.CitationKey)
                ? source.CitationKey
                : target.CitationKey;

        IReadOnlyList<ItemCreator> chosenCreators = SourceChosen("creators") || target.Creators.Count == 0
            ? source.Creators
            : target.Creators;
        IReadOnlyList<ItemDate> chosenDates = SourceChosen("year") || !HasIssuedDate(target)
            ? source.Dates
            : target.Dates;
        IReadOnlyList<ItemIdentifier> chosenIdentifiers = SourceChosen("identifiers") || target.Identifiers.Count == 0
            ? MergeIdentifiers(target.Identifiers, source.Identifiers)
            : target.Identifiers;

        IReadOnlyList<string> tags = TagNormalizer.NormalizeMany(
            ParseTags(target.TagsJson).Concat(ParseTags(source.TagsJson)));

        string? customFields =
            IsEmptyCustomFields(target.CustomFieldsJson) && !IsEmptyCustomFields(source.CustomFieldsJson)
                ? source.CustomFieldsJson
                : target.CustomFieldsJson;

        string creatorsJson = SerializeCreators(chosenCreators);
        string? issuedDate = DisplayIssuedDate(chosenDates);

        return new ItemMetadata(
            target.ItemId,
            target.LibraryId,
            target.ItemType,
            citationKey,
            title,
            ChooseString(target.Subtitle, source.Subtitle, "subtitle"),
            ChooseString(target.TitleShort, source.TitleShort, "title_short"),
            creatorsJson,
            chosenCreators,
            issuedDate,
            chosenDates,
            chosenIdentifiers,
            ChooseString(target.PublicationTitle, source.PublicationTitle, "publication_title"),
            ChooseString(target.ContainerTitleShort, source.ContainerTitleShort, "container_title_short"),
            ChooseString(target.CollectionTitle, source.CollectionTitle, "collection_title"),
            ChooseString(target.Publisher, source.Publisher, "publisher"),
            ChooseString(target.Place, source.Place, "place"),
            ChooseString(target.Edition, source.Edition, "edition"),
            ChooseString(target.Genre, source.Genre, "genre"),
            ChooseString(target.Number, source.Number, "number"),
            ChooseString(target.ChapterNumber, source.ChapterNumber, "chapter_number"),
            ChooseString(target.Volume, source.Volume, "volume"),
            ChooseString(target.Version, source.Version, "version"),
            ChooseString(target.Issue, source.Issue, "issue"),
            ChooseString(target.Pages, source.Pages, "pages"),
            ChooseString(target.Language, source.Language, "language"),
            ChooseString(target.Status, source.Status, "status"),
            ChooseString(target.Note, source.Note, "note"),
            ChooseString(target.Abstract, source.Abstract, "abstract"),
            JsonSerializer.Serialize(tags),
            target.CollectionsJson,
            customFields ?? "{}",
            target.CreatedAt,
            now);
    }

    private static IReadOnlyList<ItemIdentifier> MergeIdentifiers(
        IReadOnlyList<ItemIdentifier> target,
        IReadOnlyList<ItemIdentifier> source)
    {
        Dictionary<string, ItemIdentifier> byKey = new(StringComparer.Ordinal);
        foreach (ItemIdentifier identifier in target.Concat(source))
        {
            string key = $"{identifier.Scheme.ToLowerInvariant()}\0{identifier.Value.Trim()}";
            byKey.TryAdd(key, identifier);
        }

        return byKey.Values.ToArray();
    }

    private static bool HasIssuedDate(ItemMetadata item)
    {
        return item.Dates.Any(date => date.Role == ItemDateRoles.Issued);
    }

    private static string? ExtractYear(ItemMetadata item)
    {
        ItemDate? issued = item.Dates.FirstOrDefault(date => date.Role == ItemDateRoles.Issued);
        if (issued is not null)
        {
            if (!string.IsNullOrWhiteSpace(issued.Literal))
            {
                string? year = TryExtractFourDigits(issued.Literal);
                if (year is not null)
                {
                    return year;
                }
            }

            if (!string.IsNullOrWhiteSpace(issued.DatePartsJson) && issued.DatePartsJson != "[]")
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(issued.DatePartsJson);
                    if (document.RootElement.ValueKind == JsonValueKind.Array &&
                        document.RootElement.GetArrayLength() > 0)
                    {
                        JsonElement first = document.RootElement[0];
                        if (first.ValueKind == JsonValueKind.Array && first.GetArrayLength() > 0)
                        {
                            return first[0].ToString();
                        }
                    }
                }
                catch
                {
                    // ignored
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(item.Date))
        {
            string? year = TryExtractFourDigits(item.Date);
            if (year is not null)
            {
                return year;
            }
        }

        return null;
    }

    private static string? TryExtractFourDigits(string value)
    {
        for (int index = 0; index <= value.Length - 4; index++)
        {
            ReadOnlySpan<char> span = value.AsSpan(index, 4);
            if (int.TryParse(span, NumberStyles.None, CultureInfo.InvariantCulture, out int year) && year > 0)
            {
                return new string(span);
            }
        }

        return null;
    }

    private static string FormatCreators(IReadOnlyList<ItemCreator> creators)
    {
        if (creators.Count == 0)
        {
            return "";
        }

        return string.Join("; ", creators.Select(static creator =>
        {
            if (!string.IsNullOrWhiteSpace(creator.Literal))
            {
                return creator.Literal.Trim();
            }

            return string.Join(" ", new[] { creator.Given, creator.Particles, creator.Family, creator.Suffix }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        }));
    }

    private static string NormalizeCreatorsForComparison(IReadOnlyList<ItemCreator> creators)
    {
        IEnumerable<string> parts = creators.Select(static creator =>
        {
            string name = string.IsNullOrWhiteSpace(creator.Literal)
                ? string.Join("|", new[] { creator.Given, creator.Particles, creator.Family, creator.Suffix }
                    .Where(value => !string.IsNullOrWhiteSpace(value)))
                : creator.Literal.Trim();
            return $"{creator.Role.ToLowerInvariant()}:{name.ToLowerInvariant()}";
        });

        return string.Join(";", parts);
    }

    private static string FormatIdentifiers(IReadOnlyList<ItemIdentifier> identifiers)
    {
        if (identifiers.Count == 0)
        {
            return "";
        }

        return string.Join("; ", identifiers.Select(static identifier =>
            $"{identifier.Scheme.ToUpperInvariant()}: {identifier.Value}"));
    }

    private static string NormalizeIdentifiersForComparison(IReadOnlyList<ItemIdentifier> identifiers)
    {
        return string.Join(
            ";",
            identifiers
                .Select(identifier => $"{identifier.Scheme.ToLowerInvariant()}:{identifier.Value.Trim()}")
                .OrderBy(value => value, StringComparer.Ordinal));
    }

    private static string? DisplayIssuedDate(IReadOnlyList<ItemDate> dates)
    {
        ItemDate? issued = dates.FirstOrDefault(date => date.Role == ItemDateRoles.Issued);
        if (issued is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(issued.Literal))
        {
            return issued.Literal;
        }

        if (string.IsNullOrWhiteSpace(issued.DatePartsJson) || issued.DatePartsJson == "[]")
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(issued.DatePartsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            JsonElement firstPart = document.RootElement[0];
            if (firstPart.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return string.Join("-", firstPart.EnumerateArray().Select(part => part.ToString()));
        }
        catch
        {
            return null;
        }
    }

    private static string SerializeCreators(IReadOnlyList<ItemCreator> creators)
    {
        var values = creators.Select(creator => new
        {
            role = creator.Role,
            family = creator.Family,
            given = creator.Given,
            literal = creator.Literal,
            suffix = creator.Suffix,
            particles = creator.Particles,
            name = FormatCreator(creator)
        }).ToArray();

        return JsonSerializer.Serialize(values);
    }

    private static string FormatCreator(ItemCreator creator)
    {
        if (!string.IsNullOrWhiteSpace(creator.Literal))
        {
            return creator.Literal.Trim();
        }

        return string.Join(" ", new[] { creator.Given, creator.Particles, creator.Family, creator.Suffix }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static IReadOnlyList<string> ParseTags(string tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson))
        {
            return Array.Empty<string>();
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(tagsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return document.RootElement.EnumerateArray()
                .Select(element => element.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool IsEmptyCustomFields(string? customFieldsJson)
    {
        return string.IsNullOrWhiteSpace(customFieldsJson) ||
               string.Equals(customFieldsJson.Trim(), "{}", StringComparison.Ordinal);
    }

    private static async Task ReplaceCreatorsAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        ItemId itemId,
        IReadOnlyList<ItemCreator> creators,
        DateTimeOffset now)
    {
        await connection.ExecuteAsync("delete from item_creators where item_id = @ItemId;",
            new { ItemId = itemId.ToString() }, transaction);

        for (int index = 0; index < creators.Count; index++)
        {
            ItemCreator creator = creators[index];
            if (string.IsNullOrWhiteSpace(creator.Literal) &&
                string.IsNullOrWhiteSpace(creator.Family) &&
                string.IsNullOrWhiteSpace(creator.Given))
            {
                continue;
            }

            await connection.ExecuteAsync(
                """
                insert into item_creators (
                    creator_id, item_id, role, family, given, literal, suffix, particles, sequence_index, created_at
                )
                values (
                    @CreatorId, @ItemId, @Role, @Family, @Given, @Literal, @Suffix, @Particles, @SequenceIndex, @CreatedAt
                );
                """,
                new
                {
                    CreatorId = Guid.NewGuid().ToString("D"),
                    ItemId = itemId.ToString(),
                    creator.Role,
                    creator.Family,
                    creator.Given,
                    creator.Literal,
                    creator.Suffix,
                    creator.Particles,
                    SequenceIndex = index,
                    CreatedAt = FormatUtc(now)
                },
                transaction);
        }
    }

    private static async Task ReplaceDatesAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        ItemId itemId,
        IReadOnlyList<ItemDate> dates,
        DateTimeOffset now)
    {
        await connection.ExecuteAsync("delete from item_dates where item_id = @ItemId;",
            new { ItemId = itemId.ToString() }, transaction);

        foreach (ItemDate date in dates)
        {
            if (!ItemDateRoles.Supported.Contains(date.Role))
            {
                continue;
            }

            string datePartsJson = string.IsNullOrWhiteSpace(date.DatePartsJson) ? "[]" : date.DatePartsJson.Trim();
            if (!IsJsonArray(datePartsJson))
            {
                datePartsJson = "[]";
            }

            if (string.IsNullOrWhiteSpace(date.Literal) &&
                string.IsNullOrWhiteSpace(date.Season) &&
                datePartsJson == "[]")
            {
                continue;
            }

            await connection.ExecuteAsync(
                """
                insert into item_dates (
                    date_id, item_id, role, date_parts_json, circa, season, literal, created_at
                )
                values (
                    @DateId, @ItemId, @Role, @DatePartsJson, @Circa, @Season, @Literal, @CreatedAt
                );
                """,
                new
                {
                    DateId = Guid.NewGuid().ToString("D"),
                    ItemId = itemId.ToString(),
                    date.Role,
                    DatePartsJson = datePartsJson,
                    Circa = date.Circa ? 1 : 0,
                    date.Season,
                    date.Literal,
                    CreatedAt = FormatUtc(now)
                },
                transaction);
        }
    }

    private static async Task ReplaceIdentifiersAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        ItemId itemId,
        IReadOnlyList<ItemIdentifier> identifiers,
        DateTimeOffset now)
    {
        await connection.ExecuteAsync("delete from item_identifiers where item_id = @ItemId;",
            new { ItemId = itemId.ToString() }, transaction);

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (ItemIdentifier identifier in identifiers)
        {
            string scheme = identifier.Scheme.Trim().ToLowerInvariant();
            string value = identifier.Value.Trim();
            if (string.IsNullOrWhiteSpace(scheme) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            string key = $"{scheme}\0{value}";
            if (!seen.Add(key))
            {
                continue;
            }

            await connection.ExecuteAsync(
                """
                insert into item_identifiers (identifier_id, item_id, scheme, value, note, created_at)
                values (@IdentifierId, @ItemId, @Scheme, @Value, @Note, @CreatedAt);
                """,
                new
                {
                    IdentifierId = IdentifierId.New().ToString(),
                    ItemId = itemId.ToString(),
                    Scheme = scheme,
                    Value = value,
                    identifier.Note,
                    CreatedAt = FormatUtc(now)
                },
                transaction);
        }
    }

    private static object ToParameters(ItemMetadata item)
    {
        return new
        {
            ItemId = item.ItemId.ToString(),
            LibraryId = item.LibraryId.ToString(),
            item.ItemType,
            item.CitationKey,
            item.Title,
            item.Subtitle,
            item.TitleShort,
            item.CreatorsJson,
            item.Date,
            item.PublicationTitle,
            item.ContainerTitleShort,
            item.CollectionTitle,
            item.Publisher,
            item.Place,
            item.Edition,
            item.Genre,
            item.Number,
            item.ChapterNumber,
            item.Volume,
            item.Version,
            item.Issue,
            item.Pages,
            item.Language,
            item.Status,
            item.Note,
            Abstract = item.Abstract,
            item.TagsJson,
            item.CollectionsJson,
            item.CustomFieldsJson,
            CreatedAt = FormatUtc(item.CreatedAt),
            UpdatedAt = FormatUtc(item.UpdatedAt)
        };
    }

    private static bool IsJsonArray(string value)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Array;
        }
        catch
        {
            return false;
        }
    }

    private async Task<Result<LibraryChangeSet?>> IncrementRevisionAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        LibraryChangeSet changeSet,
        CancellationToken cancellationToken)
    {
        if (_revisions is null)
        {
            return Result<LibraryChangeSet?>.Success(null);
        }

        Result<LibraryChangeSet> revision = await _revisions.IncrementInTransactionAsync(
            connection, transaction, changeSet, cancellationToken);
        return revision.IsSuccess
            ? Result<LibraryChangeSet?>.Success(revision.Value)
            : Result<LibraryChangeSet?>.Failure(revision.ErrorCode!, revision.ErrorMessage!);
    }

    private void PublishRevision(LibraryChangeSet? changeSet)
    {
        if (changeSet is not null)
        {
            _revisions?.PublishCommitted(changeSet);
        }
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O");
    }

    private static IReadOnlyList<ItemCreator> LegacyCreators(ItemRow row)
    {
        if (string.IsNullOrWhiteSpace(row.CreatorsJson))
        {
            return Array.Empty<ItemCreator>();
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(row.CreatorsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<ItemCreator>();
            }

            List<ItemCreator> creators = new();
            int index = 0;
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                string? role = ReadString(element, "role") ?? ReadString(element, "Role") ?? ItemCreatorRoles.Author;
                string? family = ReadString(element, "family") ?? ReadString(element, "Family");
                string? given = ReadString(element, "given") ?? ReadString(element, "Given");
                string? literal = ReadString(element, "literal")
                                  ?? ReadString(element, "Literal")
                                  ?? ReadString(element, "name")
                                  ?? ReadString(element, "Name");
                string? suffix = ReadString(element, "suffix") ?? ReadString(element, "Suffix");
                string? particles = ReadString(element, "particles") ?? ReadString(element, "Particles");

                if (!string.IsNullOrWhiteSpace(literal) ||
                    !string.IsNullOrWhiteSpace(family) ||
                    !string.IsNullOrWhiteSpace(given))
                {
                    creators.Add(new ItemCreator(
                        string.Empty,
                        row.ToItemId(),
                        role,
                        family,
                        given,
                        literal,
                        suffix,
                        particles,
                        index,
                        DateTimeOffset.Parse(row.UpdatedAt)));
                    index++;
                }
            }

            return creators;
        }
        catch
        {
            return Array.Empty<ItemCreator>();
        }
    }

    private static IReadOnlyList<ItemDate> LegacyDates(ItemRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Date))
        {
            return Array.Empty<ItemDate>();
        }

        return new[]
        {
            new ItemDate(
                string.Empty,
                row.ToItemId(),
                ItemDateRoles.Issued,
                "[]",
                false,
                null,
                row.Date,
                DateTimeOffset.Parse(row.UpdatedAt))
        };
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value) &&
               value.ValueKind == JsonValueKind.String
            ? NullIfWhiteSpace(value.GetString())
            : null;
    }

    private sealed class ItemRow
    {
        public string ItemId { get; set; } = string.Empty;
        public string LibraryId { get; set; } = string.Empty;
        public string ItemType { get; set; } = string.Empty;
        public string CitationKey { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Subtitle { get; set; }
        public string? TitleShort { get; set; }
        public string CreatorsJson { get; set; } = "[]";
        public string? Date { get; set; }
        public string? PublicationTitle { get; set; }
        public string? ContainerTitleShort { get; set; }
        public string? CollectionTitle { get; set; }
        public string? Publisher { get; set; }
        public string? Place { get; set; }
        public string? Edition { get; set; }
        public string? Genre { get; set; }
        public string? Number { get; set; }
        public string? ChapterNumber { get; set; }
        public string? Volume { get; set; }
        public string? Version { get; set; }
        public string? Issue { get; set; }
        public string? Pages { get; set; }
        public string? Language { get; set; }
        public string? Status { get; set; }
        public string? Note { get; set; }
        public string? Abstract { get; set; }
        public string TagsJson { get; set; } = "[]";
        public string CollectionsJson { get; set; } = "[]";
        public string CustomFieldsJson { get; set; } = "{}";
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
        public string? DeletedAt { get; set; }
        public string? MergedIntoItemId { get; set; }

        public ItemId ToItemId()
        {
            return Patchouli.Core.Ids.ItemId.Parse(ItemId);
        }

        public LibraryId ToLibraryId()
        {
            return Patchouli.Core.Ids.LibraryId.Parse(LibraryId);
        }

        public ItemMetadata ToMetadata(
            IReadOnlyList<ItemCreator> creators,
            IReadOnlyList<ItemDate> dates,
            IReadOnlyList<ItemIdentifier> identifiers)
        {
            return new ItemMetadata(
                ToItemId(),
                ToLibraryId(),
                ItemType,
                CitationKey,
                Title,
                NullIfWhiteSpace(Subtitle),
                NullIfWhiteSpace(TitleShort),
                CreatorsJson,
                creators,
                NullIfWhiteSpace(Date),
                dates,
                identifiers,
                NullIfWhiteSpace(PublicationTitle),
                NullIfWhiteSpace(ContainerTitleShort),
                NullIfWhiteSpace(CollectionTitle),
                NullIfWhiteSpace(Publisher),
                NullIfWhiteSpace(Place),
                NullIfWhiteSpace(Edition),
                NullIfWhiteSpace(Genre),
                NullIfWhiteSpace(Number),
                NullIfWhiteSpace(ChapterNumber),
                NullIfWhiteSpace(Volume),
                NullIfWhiteSpace(Version),
                NullIfWhiteSpace(Issue),
                NullIfWhiteSpace(Pages),
                NullIfWhiteSpace(Language),
                NullIfWhiteSpace(Status),
                NullIfWhiteSpace(Note),
                NullIfWhiteSpace(Abstract),
                TagsJson,
                CollectionsJson,
                CustomFieldsJson,
                DateTimeOffset.Parse(CreatedAt),
                DateTimeOffset.Parse(UpdatedAt));
        }
    }

    private sealed class CreatorRow
    {
        public string CreatorId { get; set; } = string.Empty;
        public string ItemId { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string? Family { get; set; }
        public string? Given { get; set; }
        public string? Literal { get; set; }
        public string? Suffix { get; set; }
        public string? Particles { get; set; }
        public int SequenceIndex { get; set; }
        public string CreatedAt { get; set; } = string.Empty;

        public ItemCreator ToCreator()
        {
            return new ItemCreator(
                CreatorId,
                Patchouli.Core.Ids.ItemId.Parse(ItemId),
                Role,
                Family,
                Given,
                Literal,
                Suffix,
                Particles,
                SequenceIndex,
                DateTimeOffset.Parse(CreatedAt));
        }
    }

    private sealed class DateRow
    {
        public string DateId { get; set; } = string.Empty;
        public string ItemId { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string DatePartsJson { get; set; } = "[]";
        public int Circa { get; set; }
        public string? Season { get; set; }
        public string? Literal { get; set; }
        public string CreatedAt { get; set; } = string.Empty;

        public ItemDate ToDate()
        {
            return new ItemDate(
                DateId,
                Patchouli.Core.Ids.ItemId.Parse(ItemId),
                Role,
                DatePartsJson,
                Circa != 0,
                Season,
                Literal,
                DateTimeOffset.Parse(CreatedAt));
        }
    }

    private sealed class IdentifierRow
    {
        public string IdentifierId { get; set; } = string.Empty;
        public string ItemId { get; set; } = string.Empty;
        public string Scheme { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string? Note { get; set; }
        public string CreatedAt { get; set; } = string.Empty;

        public ItemIdentifier ToIdentifier()
        {
            return new ItemIdentifier(
                Patchouli.Core.Ids.IdentifierId.Parse(IdentifierId),
                Patchouli.Core.Ids.ItemId.Parse(ItemId),
                Scheme.Trim().ToLowerInvariant(),
                Value.Trim(),
                Note,
                DateTimeOffset.Parse(CreatedAt));
        }
    }
}
