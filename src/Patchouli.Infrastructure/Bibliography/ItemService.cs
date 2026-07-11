using System.Text;
using System.Text.Json;
using Dapper;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.Bibliography;

public sealed class ItemService : IItemService
{
    private const int MaxPageSize = 200;

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ILibraryIdentityService _libraryIdentityService;
    private readonly IClock _clock;

    public ItemService(
        SqliteConnectionFactory connectionFactory,
        ILibraryIdentityService libraryIdentityService,
        IClock clock)
    {
        _connectionFactory = connectionFactory;
        _libraryIdentityService = libraryIdentityService;
        _clock = clock;
    }

    public async Task<Result<ItemMetadata>> CreateItemAsync(
        CreateItemRequest request,
        CancellationToken cancellationToken = default)
    {
        return await CreateItemCoreAsync(request, cancellationToken);
    }

    public async Task<Result<ItemMetadata>> CreateItemAsync(
        string itemType,
        string title,
        string? subtitle = null,
        string? titleShort = null,
        string? creatorsJson = null,
        string? date = null,
        string? publicationTitle = null,
        string? containerTitleShort = null,
        string? collectionTitle = null,
        string? publisher = null,
        string? place = null,
        string? edition = null,
        string? genre = null,
        string? number = null,
        string? chapterNumber = null,
        string? volume = null,
        string? version = null,
        string? issue = null,
        string? pages = null,
        string? language = null,
        string? status = null,
        string? note = null,
        string? abstractText = null,
        string? tagsJson = null,
        string? collectionsJson = null,
        string? customFieldsJson = null,
        IReadOnlyList<ItemCreatorInput>? creators = null,
        IReadOnlyList<ItemDateInput>? dates = null,
        CancellationToken cancellationToken = default)
    {
        return await CreateItemCoreAsync(
            new CreateItemRequest(
                itemType,
                title,
                subtitle,
                titleShort,
                creatorsJson,
                date,
                publicationTitle,
                containerTitleShort,
                collectionTitle,
                publisher,
                place,
                edition,
                genre,
                number,
                chapterNumber,
                volume,
                version,
                issue,
                pages,
                language,
                status,
                note,
                abstractText,
                tagsJson,
                collectionsJson,
                customFieldsJson,
                creators,
                dates),
            cancellationToken);
    }

    public async Task<Result<ItemMetadata>> GetItemAsync(ItemId itemId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var row = await QueryItemRowAsync(connection, itemId, cancellationToken);
            if (row is null)
            {
                return Result<ItemMetadata>.Failure(AppErrorCodes.NotFound, "Item was not found.");
            }

            var creators = await LoadCreatorsAsync(connection, new[] { itemId }, cancellationToken);
            var dates = await LoadDatesAsync(connection, new[] { itemId }, cancellationToken);
            var identifiers = await LoadIdentifiersAsync(connection, new[] { itemId }, cancellationToken);
            return Result<ItemMetadata>.Success(row.ToMetadata(
                creators.GetValueOrDefault(itemId) ?? LegacyCreators(row),
                dates.GetValueOrDefault(itemId) ?? LegacyDates(row),
                identifiers.GetValueOrDefault(itemId) ?? Array.Empty<ItemIdentifier>()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.item-service"))
        {
            return DatabaseFailure<ItemMetadata>(exception);
        }
    }

    public async Task<Result<ItemMetadata>> UpdateItemAsync(
        ItemId itemId,
        UpdateItemRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return Result<ItemMetadata>.Failure(AppErrorCodes.ValidationFailed, "Item title is required.");
        }

        if (string.IsNullOrWhiteSpace(request.ItemType))
        {
            return Result<ItemMetadata>.Failure(AppErrorCodes.ValidationFailed, "Item type is required.");
        }

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var existing = await QueryItemRowAsync(connection, itemId, cancellationToken, transaction);
            if (existing is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<ItemMetadata>.Failure(AppErrorCodes.NotFound, "Item was not found.");
            }

            var creatorInputs = request.Creators ?? ParseCreatorInputs(request.CreatorsJson);
            var dateInputs = request.Dates ?? ParseDateInputs(request.Date);
            var customFieldsJson = request.CustomFieldsJson is null
                ? existing.CustomFieldsJson
                : DefaultJsonObject(request.CustomFieldsJson);
            var updated = new ItemMetadata(
                existing.ToItemId(),
                existing.ToLibraryId(),
                request.ItemType.Trim(),
                existing.CitationKey,
                request.Title.Trim(),
                NullIfWhiteSpace(request.Subtitle),
                NullIfWhiteSpace(request.TitleShort),
                request.Creators is null ? DefaultJsonArray(request.CreatorsJson) : SerializeCreatorCache(creatorInputs),
                Array.Empty<ItemCreator>(),
                request.Dates is null ? NullIfWhiteSpace(request.Date) : DisplayIssuedDate(dateInputs),
                Array.Empty<ItemDate>(),
                Array.Empty<ItemIdentifier>(),
                NullIfWhiteSpace(request.PublicationTitle),
                NullIfWhiteSpace(request.ContainerTitleShort),
                NullIfWhiteSpace(request.CollectionTitle),
                NullIfWhiteSpace(request.Publisher),
                NullIfWhiteSpace(request.Place),
                NullIfWhiteSpace(request.Edition),
                NullIfWhiteSpace(request.Genre),
                NullIfWhiteSpace(request.Number),
                NullIfWhiteSpace(request.ChapterNumber),
                NullIfWhiteSpace(request.Volume),
                NullIfWhiteSpace(request.Version),
                NullIfWhiteSpace(request.Issue),
                NullIfWhiteSpace(request.Pages),
                NullIfWhiteSpace(request.Language),
                NullIfWhiteSpace(request.Status),
                NullIfWhiteSpace(request.Note),
                NullIfWhiteSpace(request.AbstractText),
                DefaultJsonArray(request.TagsJson),
                DefaultJsonArray(request.CollectionsJson),
                customFieldsJson,
                DateTimeOffset.Parse(existing.CreatedAt),
                _clock.UtcNow.ToUniversalTime());

            var updateParameters = new DynamicParameters(ToParameters(updated));
            updateParameters.Add("ExpectedUpdatedAt", request.ExpectedUpdatedAt?.ToUniversalTime().ToString("O"));
            var affected = await connection.ExecuteAsync(
                """
                update items
                set item_type = @ItemType,
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
                  and (@ExpectedUpdatedAt is null or updated_at = @ExpectedUpdatedAt);
                """,
                updateParameters,
                transaction);

            if (affected == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<ItemMetadata>.Failure(AppErrorCodes.Conflict, "Item metadata changed while the update was in progress.");
            }

            await ReplaceCreatorsAsync(connection, transaction, itemId, creatorInputs, updated.UpdatedAt);
            await ReplaceDatesAsync(connection, transaction, itemId, dateInputs, updated.UpdatedAt);
            await transaction.CommitAsync(cancellationToken);
            return await GetItemAsync(itemId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.item-service"))
        {
            return DatabaseFailure<ItemMetadata>(exception);
        }
    }

    public async Task<Result> DeleteItemAsync(ItemId itemId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var affected = await connection.ExecuteAsync(
                """
                update items
                set deleted_at = @DeletedAt,
                    updated_at = @UpdatedAt
                where item_id = @ItemId and deleted_at is null;
                """,
                new
                {
                    ItemId = itemId.ToString(),
                    DeletedAt = FormatUtc(_clock.UtcNow),
                    UpdatedAt = FormatUtc(_clock.UtcNow)
                });

            return affected == 0
                ? Result.Failure(AppErrorCodes.NotFound, "Item was not found.")
                : Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.item-service"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<ItemListPage>> ListItemsAsync(
        ListItemsRequest request,
        CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(request.PageSize, 1, MaxPageSize);
        if (!TryParseCursor(request.Cursor, out var cursorCreatedAt, out var cursorItemId))
        {
            return Result<ItemListPage>.Failure(AppErrorCodes.ValidationFailed, "Item list cursor is invalid.");
        }

        var libraryResult = await _libraryIdentityService.GetCurrentLibraryAsync(cancellationToken);
        if (libraryResult.IsFailure)
        {
            return Result<ItemListPage>.Failure(libraryResult.ErrorCode!, libraryResult.ErrorMessage!);
        }

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var query = NullIfWhiteSpace(request.Query);
            var itemType = NullIfWhiteSpace(request.ItemType);
            var pattern = query is null ? null : $"%{query}%";
            var parameters = new
            {
                LibraryId = libraryResult.Value.LibraryId.ToString(),
                Query = query,
                QueryPattern = pattern,
                ItemType = itemType,
                CursorCreatedAt = cursorCreatedAt,
                CursorItemId = cursorItemId,
                Take = pageSize + 1
            };

            var totalCount = await connection.ExecuteScalarAsync<int>(
                """
                select count(1)
                from items
                where library_id = @LibraryId
                  and deleted_at is null
                  and (@ItemType is null or item_type = @ItemType)
                  and (
                      @Query is null
                      or title like @QueryPattern
                      or coalesce(subtitle, '') like @QueryPattern
                      or coalesce(citation_key, '') like @QueryPattern
                      or coalesce(publication_title, '') like @QueryPattern
                      or exists (
                          select 1
                          from item_creators c
                          where c.item_id = items.item_id
                            and (coalesce(c.family, '') || ' ' || coalesce(c.given, '') || ' ' || coalesce(c.literal, '')) like @QueryPattern
                      )
                  );
                """,
                parameters);

            var rows = (await connection.QueryAsync<ItemRow>(
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
                    deleted_at as DeletedAt
                from items
                where library_id = @LibraryId
                  and deleted_at is null
                  and (@ItemType is null or item_type = @ItemType)
                  and (
                      @Query is null
                      or title like @QueryPattern
                      or coalesce(subtitle, '') like @QueryPattern
                      or coalesce(citation_key, '') like @QueryPattern
                      or coalesce(publication_title, '') like @QueryPattern
                      or exists (
                          select 1
                          from item_creators c
                          where c.item_id = items.item_id
                            and (coalesce(c.family, '') || ' ' || coalesce(c.given, '') || ' ' || coalesce(c.literal, '')) like @QueryPattern
                      )
                  )
                  and (
                      @CursorCreatedAt is null
                      or created_at < @CursorCreatedAt
                      or (created_at = @CursorCreatedAt and item_id < @CursorItemId)
                  )
                order by created_at desc, item_id desc
                limit @Take;
                """,
                parameters)).ToArray();

            var hasMore = rows.Length > pageSize;
            var pageRows = hasMore ? rows[..pageSize] : rows;
            var nextCursor = hasMore ? CreateCursor(pageRows[^1]) : null;
            var itemIds = pageRows.Select(row => row.ToItemId()).ToArray();
            var creators = await LoadCreatorsAsync(connection, itemIds, cancellationToken);
            var dates = await LoadDatesAsync(connection, itemIds, cancellationToken);
            var identifiers = await LoadIdentifiersAsync(connection, itemIds, cancellationToken);

            return Result<ItemListPage>.Success(new ItemListPage(
                pageRows.Select(row => row.ToMetadata(
                    creators.GetValueOrDefault(row.ToItemId()) ?? LegacyCreators(row),
                    dates.GetValueOrDefault(row.ToItemId()) ?? LegacyDates(row),
                    identifiers.GetValueOrDefault(row.ToItemId()) ?? Array.Empty<ItemIdentifier>())).ToArray(),
                nextCursor,
                totalCount));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.item-service"))
        {
            return DatabaseFailure<ItemListPage>(exception);
        }
    }

    public async Task<Result<ItemIdentifier>> AddIdentifierAsync(
        ItemId itemId,
        string scheme,
        string value,
        string? note,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scheme) || string.IsNullOrWhiteSpace(value))
        {
            return Result<ItemIdentifier>.Failure(
                AppErrorCodes.ValidationFailed,
                "Identifier scheme and value are required.");
        }

        var normalizedScheme = NormalizeIdentifierScheme(scheme);
        var normalizedValue = value.Trim();

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var itemExists = await connection.ExecuteScalarAsync<int>(
                "select count(1) from items where item_id = @ItemId and deleted_at is null;",
                new { ItemId = itemId.ToString() },
                transaction);

            if (itemExists == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<ItemIdentifier>.Failure(AppErrorCodes.NotFound, "Item was not found.");
            }

            var duplicateCount = await connection.ExecuteScalarAsync<int>(
                """
                select count(1)
                from item_identifiers
                where item_id = @ItemId and scheme = @Scheme and value = @Value;
                """,
                new
                {
                    ItemId = itemId.ToString(),
                    Scheme = normalizedScheme,
                    Value = normalizedValue
                },
                transaction);

            if (duplicateCount > 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<ItemIdentifier>.Failure(
                    AppErrorCodes.InvalidState,
                    "This identifier already exists for the item.");
            }

            var identifier = new ItemIdentifier(
                IdentifierId.New(),
                itemId,
                normalizedScheme,
                normalizedValue,
                NullIfWhiteSpace(note),
                _clock.UtcNow.ToUniversalTime());

            await connection.ExecuteAsync(
                """
                insert into item_identifiers (identifier_id, item_id, scheme, value, note, created_at)
                values (@IdentifierId, @ItemId, @Scheme, @Value, @Note, @CreatedAt);
                """,
                new
                {
                    IdentifierId = identifier.IdentifierId.ToString(),
                    ItemId = identifier.ItemId.ToString(),
                    identifier.Scheme,
                    identifier.Value,
                    identifier.Note,
                    CreatedAt = FormatUtc(identifier.CreatedAt)
                },
                transaction);

            await transaction.CommitAsync(cancellationToken);
            return Result<ItemIdentifier>.Success(identifier);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.item-service"))
        {
            return DatabaseFailure<ItemIdentifier>(exception);
        }
    }

    public async Task<Result<IReadOnlyList<ItemIdentifier>>> ListIdentifiersAsync(
        ItemId itemId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var itemResult = await GetItemAsync(itemId, cancellationToken);
            if (itemResult.IsFailure)
            {
                return Result<IReadOnlyList<ItemIdentifier>>.Failure(itemResult.ErrorCode!, itemResult.ErrorMessage!);
            }

            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var rows = await connection.QueryAsync<IdentifierRow>(
                """
                select
                    identifier_id as IdentifierId,
                    item_id as ItemId,
                    scheme as Scheme,
                    value as Value,
                    note as Note,
                    created_at as CreatedAt
                from item_identifiers
                where item_id = @ItemId
                order by created_at, identifier_id;
                """,
                new { ItemId = itemId.ToString() });

            return Result<IReadOnlyList<ItemIdentifier>>.Success(rows.Select(row => row.ToIdentifier()).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.item-service"))
        {
            return DatabaseFailure<IReadOnlyList<ItemIdentifier>>(exception);
        }
    }

    public async Task<Result> RemoveIdentifierAsync(
        ItemId itemId,
        IdentifierId identifierId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var affected = await connection.ExecuteAsync(
                """
                delete from item_identifiers
                where identifier_id = @IdentifierId and item_id = @ItemId;
                """,
                new { IdentifierId = identifierId.ToString(), ItemId = itemId.ToString() });

            return affected == 0
                ? Result.Failure(AppErrorCodes.NotFound, "Identifier was not found for the item.")
                : Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.item-service"))
        {
            var failure = DatabaseFailure<object>(exception);
            return Result.Failure(failure.ErrorCode!, failure.ErrorMessage!);
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

    private static async Task<Dictionary<ItemId, IReadOnlyList<ItemCreator>>> LoadCreatorsAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        IReadOnlyList<ItemId> itemIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (itemIds.Count == 0)
        {
            return new Dictionary<ItemId, IReadOnlyList<ItemCreator>>();
        }

        var rows = await connection.QueryAsync<CreatorRow>(
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
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ItemCreator>)group.Select(row => row.ToCreator()).ToArray());
    }

    private static async Task<Dictionary<ItemId, IReadOnlyList<ItemDate>>> LoadDatesAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        IReadOnlyList<ItemId> itemIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (itemIds.Count == 0)
        {
            return new Dictionary<ItemId, IReadOnlyList<ItemDate>>();
        }

        var rows = await connection.QueryAsync<DateRow>(
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
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ItemDate>)group.Select(row => row.ToDate()).ToArray());
    }

    private static async Task<Dictionary<ItemId, IReadOnlyList<ItemIdentifier>>> LoadIdentifiersAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        IReadOnlyList<ItemId> itemIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (itemIds.Count == 0)
        {
            return new Dictionary<ItemId, IReadOnlyList<ItemIdentifier>>();
        }

        var rows = await connection.QueryAsync<IdentifierRow>(
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
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ItemIdentifier>)group.Select(row => row.ToIdentifier()).ToArray());
    }

    private static async Task ReplaceCreatorsAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        ItemId itemId,
        IReadOnlyList<ItemCreatorInput> creators,
        DateTimeOffset now)
    {
        await connection.ExecuteAsync("delete from item_creators where item_id = @ItemId;", new { ItemId = itemId.ToString() }, transaction);

        for (var index = 0; index < creators.Count; index++)
        {
            var creator = NormalizeCreator(creators[index]);
            if (creator is null)
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
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        ItemId itemId,
        IReadOnlyList<ItemDateInput> dates,
        DateTimeOffset now)
    {
        await connection.ExecuteAsync("delete from item_dates where item_id = @ItemId;", new { ItemId = itemId.ToString() }, transaction);

        foreach (var rawDate in dates)
        {
            var date = NormalizeDate(rawDate);
            if (date is null)
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
                )
                on conflict(item_id, role) do update set
                    date_parts_json = excluded.date_parts_json,
                    circa = excluded.circa,
                    season = excluded.season,
                    literal = excluded.literal;
                """,
                new
                {
                    DateId = Guid.NewGuid().ToString("D"),
                    ItemId = itemId.ToString(),
                    date.Role,
                    date.DatePartsJson,
                    Circa = date.Circa ? 1 : 0,
                    date.Season,
                    date.Literal,
                    CreatedAt = FormatUtc(now)
                },
                transaction);
        }
    }

    private static async Task<ItemRow?> QueryItemRowAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        ItemId itemId,
        CancellationToken cancellationToken,
        System.Data.IDbTransaction? transaction = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await connection.QuerySingleOrDefaultAsync<ItemRow>(
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
                deleted_at as DeletedAt
            from items
            where item_id = @ItemId and deleted_at is null;
            """,
            new { ItemId = itemId.ToString() },
            transaction);
    }

    private static string DefaultJsonArray(string? value) => string.IsNullOrWhiteSpace(value) ? "[]" : value.Trim();
    private static string DefaultJsonObject(string? value) => string.IsNullOrWhiteSpace(value) ? "{}" : value.Trim();
    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string FormatUtc(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    private static string GenerateCitationKey(string title, ItemId itemId)
    {
        var builder = new StringBuilder();
        var appendDash = false;
        foreach (var ch in title.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                appendDash = true;
            }
            else if (appendDash && builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
                appendDash = false;
            }

            if (builder.Length >= 40)
            {
                break;
            }
        }

        var slug = builder.ToString().Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "item";
        }

        var suffix = itemId.Value.ToString("N")[..8].ToLowerInvariant();
        return $"{slug}-{suffix}";
    }

    private static bool TryParseCursor(string? cursor, out string? createdAt, out string? itemId)
    {
        createdAt = null;
        itemId = null;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        var parts = cursor.Split('|', 2, StringSplitOptions.None);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            return false;
        }

        createdAt = parts[0];
        itemId = parts[1];
        return true;
    }

    private static string CreateCursor(ItemRow row) => $"{row.CreatedAt}|{row.ItemId}";

    private static Result<T> DatabaseFailure<T>(Exception exception)
    {
        return Result<T>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
    }

    private async Task<Result<ItemMetadata>> CreateItemCoreAsync(
        CreateItemRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return Result<ItemMetadata>.Failure(AppErrorCodes.ValidationFailed, "Item title is required.");
        }

        if (string.IsNullOrWhiteSpace(request.ItemType))
        {
            return Result<ItemMetadata>.Failure(AppErrorCodes.ValidationFailed, "Item type is required.");
        }

        var libraryResult = await _libraryIdentityService.GetCurrentLibraryAsync(cancellationToken);
        if (libraryResult.IsFailure)
        {
            return Result<ItemMetadata>.Failure(libraryResult.ErrorCode!, libraryResult.ErrorMessage!);
        }

        try
        {
            var now = _clock.UtcNow.ToUniversalTime();
            var itemId = ItemId.New();
            var creatorInputs = request.Creators ?? ParseCreatorInputs(request.CreatorsJson);
            var dateInputs = request.Dates ?? ParseDateInputs(request.Date);
            var item = new ItemMetadata(
                itemId,
                libraryResult.Value.LibraryId,
                request.ItemType.Trim(),
                GenerateCitationKey(request.Title, itemId),
                request.Title.Trim(),
                NullIfWhiteSpace(request.Subtitle),
                NullIfWhiteSpace(request.TitleShort),
                request.Creators is null ? DefaultJsonArray(request.CreatorsJson) : SerializeCreatorCache(creatorInputs),
                Array.Empty<ItemCreator>(),
                request.Dates is null ? NullIfWhiteSpace(request.Date) : DisplayIssuedDate(dateInputs),
                Array.Empty<ItemDate>(),
                Array.Empty<ItemIdentifier>(),
                NullIfWhiteSpace(request.PublicationTitle),
                NullIfWhiteSpace(request.ContainerTitleShort),
                NullIfWhiteSpace(request.CollectionTitle),
                NullIfWhiteSpace(request.Publisher),
                NullIfWhiteSpace(request.Place),
                NullIfWhiteSpace(request.Edition),
                NullIfWhiteSpace(request.Genre),
                NullIfWhiteSpace(request.Number),
                NullIfWhiteSpace(request.ChapterNumber),
                NullIfWhiteSpace(request.Volume),
                NullIfWhiteSpace(request.Version),
                NullIfWhiteSpace(request.Issue),
                NullIfWhiteSpace(request.Pages),
                NullIfWhiteSpace(request.Language),
                NullIfWhiteSpace(request.Status),
                NullIfWhiteSpace(request.Note),
                NullIfWhiteSpace(request.AbstractText),
                DefaultJsonArray(request.TagsJson),
                DefaultJsonArray(request.CollectionsJson),
                DefaultJsonObject(request.CustomFieldsJson),
                now,
                now);

            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await connection.ExecuteAsync(
                """
                insert into items (
                    item_id, library_id, item_type, citation_key, title, subtitle, title_short, creators_json, date,
                    publication_title, container_title_short, collection_title, publisher, place, edition, genre,
                    number, chapter_number, volume, version, issue, pages, language, status, note, abstract,
                    tags_json, collections_json, custom_fields_json, created_at, updated_at, deleted_at
                )
                values (
                    @ItemId, @LibraryId, @ItemType, @CitationKey, @Title, @Subtitle, @TitleShort, @CreatorsJson, @Date,
                    @PublicationTitle, @ContainerTitleShort, @CollectionTitle, @Publisher, @Place, @Edition, @Genre,
                    @Number, @ChapterNumber, @Volume, @Version, @Issue, @Pages, @Language, @Status, @Note, @Abstract,
                    @TagsJson, @CollectionsJson, @CustomFieldsJson, @CreatedAt, @UpdatedAt, null
                );
                """,
                ToParameters(item),
                transaction);

            await ReplaceCreatorsAsync(connection, transaction, itemId, creatorInputs, now);
            await ReplaceDatesAsync(connection, transaction, itemId, dateInputs, now);
            var identifierKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var identifier in request.Identifiers ?? Array.Empty<ItemIdentifierInput>())
            {
                var normalized = NormalizeIdentifier(identifier);
                if (normalized is null || !identifierKeys.Add($"{normalized.Scheme}\0{normalized.Value}"))
                {
                    continue;
                }

                await InsertIdentifierAsync(connection, transaction, itemId, normalized, now);
            }

            await transaction.CommitAsync(cancellationToken);
            return await GetItemAsync(itemId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.item-service"))
        {
            return DatabaseFailure<ItemMetadata>(exception);
        }
    }

    private static IReadOnlyList<ItemCreatorInput> ParseCreatorInputs(string? creatorsJson)
    {
        if (string.IsNullOrWhiteSpace(creatorsJson))
        {
            return Array.Empty<ItemCreatorInput>();
        }

        try
        {
            using var document = JsonDocument.Parse(creatorsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<ItemCreatorInput>();
            }

            var creators = new List<ItemCreatorInput>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var role = ReadString(element, "role") ?? ReadString(element, "Role") ?? ItemCreatorRoles.Author;
                var family = ReadString(element, "family") ?? ReadString(element, "Family");
                var given = ReadString(element, "given") ?? ReadString(element, "Given");
                var literal = ReadString(element, "literal")
                    ?? ReadString(element, "Literal")
                    ?? ReadString(element, "name")
                    ?? ReadString(element, "Name");
                var suffix = ReadString(element, "suffix") ?? ReadString(element, "Suffix");
                var particles = ReadString(element, "particles") ?? ReadString(element, "Particles");
                creators.Add(new ItemCreatorInput(role, family, given, literal, suffix, particles));
            }

            return creators;
        }
        catch
        {
            return Array.Empty<ItemCreatorInput>();
        }
    }

    private static IReadOnlyList<ItemDateInput> ParseDateInputs(string? date)
    {
        var trimmed = NullIfWhiteSpace(date);
        return trimmed is null
            ? Array.Empty<ItemDateInput>()
            : new[] { new ItemDateInput(ItemDateRoles.Issued, Literal: trimmed) };
    }

    private static ItemCreatorInput? NormalizeCreator(ItemCreatorInput creator)
    {
        var role = ItemCreatorRoles.Supported.Contains(creator.Role) ? creator.Role : ItemCreatorRoles.Author;
        var family = NullIfWhiteSpace(creator.Family);
        var given = NullIfWhiteSpace(creator.Given);
        var literal = NullIfWhiteSpace(creator.Literal);
        var suffix = NullIfWhiteSpace(creator.Suffix);
        var particles = NullIfWhiteSpace(creator.Particles);
        return family is null && given is null && literal is null
            ? null
            : new ItemCreatorInput(role, family, given, literal, suffix, particles);
    }

    private static ItemDateInput? NormalizeDate(ItemDateInput date)
    {
        if (!ItemDateRoles.Supported.Contains(date.Role))
        {
            return null;
        }

        var datePartsJson = string.IsNullOrWhiteSpace(date.DatePartsJson) ? "[]" : date.DatePartsJson.Trim();
        if (!IsJsonArray(datePartsJson))
        {
            datePartsJson = "[]";
        }

        var literal = NullIfWhiteSpace(date.Literal);
        var season = NullIfWhiteSpace(date.Season);
        if (literal is null && season is null && datePartsJson == "[]")
        {
            return null;
        }

        return new ItemDateInput(date.Role, datePartsJson, date.Circa, season, literal);
    }

    private static ItemIdentifierInput? NormalizeIdentifier(ItemIdentifierInput identifier)
    {
        var scheme = NullIfWhiteSpace(identifier.Scheme);
        var value = NullIfWhiteSpace(identifier.Value);
        if (scheme is null || value is null)
        {
            return null;
        }

        return new ItemIdentifierInput(NormalizeIdentifierScheme(scheme), value, NullIfWhiteSpace(identifier.Note));
    }

    private static string NormalizeIdentifierScheme(string scheme) => scheme.Trim().ToLowerInvariant();

    private static async Task InsertIdentifierAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        ItemId itemId,
        ItemIdentifierInput identifier,
        DateTimeOffset now)
    {
        await connection.ExecuteAsync(
            """
            insert into item_identifiers (
                identifier_id, item_id, scheme, value, note, created_at
            )
            values (
                @IdentifierId, @ItemId, @Scheme, @Value, @Note, @CreatedAt
            );
            """,
            new
            {
                IdentifierId = IdentifierId.New().ToString(),
                ItemId = itemId.ToString(),
                identifier.Scheme,
                identifier.Value,
                identifier.Note,
                CreatedAt = FormatUtc(now)
            },
            transaction);
    }

    private static IReadOnlyList<ItemCreator> LegacyCreators(ItemRow row)
    {
        return ParseCreatorInputs(row.CreatorsJson)
            .Select((creator, index) => NormalizeCreator(creator) is { } normalized
                ? new ItemCreator(
                    string.Empty,
                    row.ToItemId(),
                    normalized.Role,
                    normalized.Family,
                    normalized.Given,
                    normalized.Literal,
                    normalized.Suffix,
                    normalized.Particles,
                    index,
                    DateTimeOffset.Parse(row.UpdatedAt))
                : null)
            .Where(creator => creator is not null)
            .Cast<ItemCreator>()
            .ToArray();
    }

    private static IReadOnlyList<ItemDate> LegacyDates(ItemRow row)
    {
        var date = NullIfWhiteSpace(row.Date);
        return date is null
            ? Array.Empty<ItemDate>()
            : new[]
            {
                new ItemDate(
                    string.Empty,
                    row.ToItemId(),
                    ItemDateRoles.Issued,
                    "[]",
                    false,
                    null,
                    date,
                    DateTimeOffset.Parse(row.UpdatedAt))
            };
    }

    private static string SerializeCreatorCache(IReadOnlyList<ItemCreatorInput> creators)
    {
        var values = creators
            .Select(NormalizeCreator)
            .Where(creator => creator is not null)
            .Select(creator => new
            {
                role = creator!.Role,
                family = creator.Family,
                given = creator.Given,
                literal = creator.Literal,
                suffix = creator.Suffix,
                particles = creator.Particles,
                name = DisplayCreator(creator)
            })
            .ToArray();

        return JsonSerializer.Serialize(values);
    }

    private static string? DisplayIssuedDate(IReadOnlyList<ItemDateInput> dates)
    {
        var issued = dates
            .Select(NormalizeDate)
            .FirstOrDefault(date => date?.Role == ItemDateRoles.Issued);

        if (issued is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(issued.Literal))
        {
            return issued.Literal;
        }

        try
        {
            using var document = JsonDocument.Parse(issued.DatePartsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            var firstPart = document.RootElement[0];
            if (firstPart.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return string.Join("-", firstPart.EnumerateArray().Select(part => part.GetInt32().ToString("D2"))).TrimStart('0');
        }
        catch
        {
            return null;
        }
    }

    private static string DisplayCreator(ItemCreatorInput creator)
    {
        if (!string.IsNullOrWhiteSpace(creator.Literal))
        {
            return creator.Literal.Trim();
        }

        return string.Join(" ", new[] { creator.Given, creator.Particles, creator.Family, creator.Suffix }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim()));
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? NullIfWhiteSpace(value.GetString())
            : null;
    }

    private static bool IsJsonArray(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Array;
        }
        catch
        {
            return false;
        }
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

        public Patchouli.Core.Ids.ItemId ToItemId() => Patchouli.Core.Ids.ItemId.Parse(ItemId);
        public Patchouli.Core.Ids.LibraryId ToLibraryId() => Patchouli.Core.Ids.LibraryId.Parse(LibraryId);

        public ItemMetadata ToMetadata(IReadOnlyList<ItemCreator> creators, IReadOnlyList<ItemDate> dates, IReadOnlyList<ItemIdentifier> identifiers)
        {
            return new ItemMetadata(
                Patchouli.Core.Ids.ItemId.Parse(ItemId),
                Patchouli.Core.Ids.LibraryId.Parse(LibraryId),
                ItemType,
                CitationKey,
                Title,
                Subtitle,
                TitleShort,
                CreatorsJson,
                creators,
                Date,
                dates,
                identifiers,
                PublicationTitle,
                ContainerTitleShort,
                CollectionTitle,
                Publisher,
                Place,
                Edition,
                Genre,
                Number,
                ChapterNumber,
                Volume,
                Version,
                Issue,
                Pages,
                Language,
                Status,
                Note,
                Abstract,
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

        public ItemCreator ToCreator() => new(
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

        public ItemDate ToDate() => new(
            DateId,
            Patchouli.Core.Ids.ItemId.Parse(ItemId),
            Role,
            DatePartsJson,
            Circa != 0,
            Season,
            Literal,
            DateTimeOffset.Parse(CreatedAt));
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
                NormalizeIdentifierScheme(Scheme),
                Value,
                Note,
                DateTimeOffset.Parse(CreatedAt));
        }
    }
}
