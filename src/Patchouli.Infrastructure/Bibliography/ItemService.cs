using System.Text;
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
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Result<ItemMetadata>.Failure(AppErrorCodes.ValidationFailed, "Item title is required.");
        }

        if (string.IsNullOrWhiteSpace(itemType))
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
            var item = new ItemMetadata(
                itemId,
                libraryResult.Value.LibraryId,
                itemType.Trim(),
                GenerateCitationKey(title, itemId),
                title.Trim(),
                NullIfWhiteSpace(subtitle),
                NullIfWhiteSpace(titleShort),
                DefaultJsonArray(creatorsJson),
                NullIfWhiteSpace(date),
                NullIfWhiteSpace(publicationTitle),
                NullIfWhiteSpace(containerTitleShort),
                NullIfWhiteSpace(collectionTitle),
                NullIfWhiteSpace(publisher),
                NullIfWhiteSpace(place),
                NullIfWhiteSpace(edition),
                NullIfWhiteSpace(genre),
                NullIfWhiteSpace(number),
                NullIfWhiteSpace(chapterNumber),
                NullIfWhiteSpace(volume),
                NullIfWhiteSpace(version),
                NullIfWhiteSpace(issue),
                NullIfWhiteSpace(pages),
                NullIfWhiteSpace(language),
                NullIfWhiteSpace(status),
                NullIfWhiteSpace(note),
                NullIfWhiteSpace(abstractText),
                DefaultJsonArray(tagsJson),
                DefaultJsonArray(collectionsJson),
                DefaultJsonObject(customFieldsJson),
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

            await transaction.CommitAsync(cancellationToken);
            return Result<ItemMetadata>.Success(item);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return DatabaseFailure<ItemMetadata>(exception);
        }
    }

    public async Task<Result<ItemMetadata>> GetItemAsync(ItemId itemId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var row = await QueryItemRowAsync(connection, itemId, cancellationToken);
            return row is null
                ? Result<ItemMetadata>.Failure(AppErrorCodes.NotFound, "Item was not found.")
                : Result<ItemMetadata>.Success(row.ToMetadata());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
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

            var updated = new ItemMetadata(
                existing.ToItemId(),
                existing.ToLibraryId(),
                request.ItemType.Trim(),
                existing.CitationKey,
                request.Title.Trim(),
                NullIfWhiteSpace(request.Subtitle),
                NullIfWhiteSpace(request.TitleShort),
                DefaultJsonArray(request.CreatorsJson),
                NullIfWhiteSpace(request.Date),
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
                DateTimeOffset.Parse(existing.CreatedAt),
                _clock.UtcNow.ToUniversalTime());

            await connection.ExecuteAsync(
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
                where item_id = @ItemId and deleted_at is null;
                """,
                ToParameters(updated),
                transaction);

            await transaction.CommitAsync(cancellationToken);
            return Result<ItemMetadata>.Success(updated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
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
        catch (Exception exception)
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

            return Result<ItemListPage>.Success(new ItemListPage(
                pageRows.Select(row => row.ToMetadata()).ToArray(),
                nextCursor,
                totalCount));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
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
                    Scheme = scheme.Trim(),
                    Value = value.Trim()
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
                scheme.Trim(),
                value.Trim(),
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
        catch (Exception exception)
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
        catch (Exception exception)
        {
            return DatabaseFailure<IReadOnlyList<ItemIdentifier>>(exception);
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

        public ItemMetadata ToMetadata()
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
                Date,
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
                Scheme,
                Value,
                Note,
                DateTimeOffset.Parse(CreatedAt));
        }
    }
}
