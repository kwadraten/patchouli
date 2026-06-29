using Dapper;
using LiteratureApp.Core.Bibliography;
using LiteratureApp.Core.Ids;
using LiteratureApp.Core.Library;
using LiteratureApp.Core.Results;
using LiteratureApp.Core.Time;
using LiteratureApp.Infrastructure.Database;

namespace LiteratureApp.Infrastructure.Bibliography;

public sealed class ItemService : IItemService
{
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
        string? creatorsJson = null,
        string? date = null,
        string? publicationTitle = null,
        string? publisher = null,
        string? place = null,
        string? volume = null,
        string? issue = null,
        string? pages = null,
        string? language = null,
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
            var item = new ItemMetadata(
                ItemId.New(),
                libraryResult.Value.LibraryId,
                itemType.Trim(),
                title.Trim(),
                NullIfWhiteSpace(subtitle),
                DefaultJsonArray(creatorsJson),
                NullIfWhiteSpace(date),
                NullIfWhiteSpace(publicationTitle),
                NullIfWhiteSpace(publisher),
                NullIfWhiteSpace(place),
                NullIfWhiteSpace(volume),
                NullIfWhiteSpace(issue),
                NullIfWhiteSpace(pages),
                NullIfWhiteSpace(language),
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
                    item_id, library_id, item_type, title, subtitle, creators_json, date,
                    publication_title, publisher, place, volume, issue, pages, language,
                    abstract, tags_json, collections_json, custom_fields_json, created_at, updated_at
                )
                values (
                    @ItemId, @LibraryId, @ItemType, @Title, @Subtitle, @CreatorsJson, @Date,
                    @PublicationTitle, @Publisher, @Place, @Volume, @Issue, @Pages, @Language,
                    @Abstract, @TagsJson, @CollectionsJson, @CustomFieldsJson, @CreatedAt, @UpdatedAt
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

            var row = await connection.QuerySingleOrDefaultAsync<ItemRow>(
                """
                select
                    item_id as ItemId,
                    library_id as LibraryId,
                    item_type as ItemType,
                    title as Title,
                    subtitle as Subtitle,
                    creators_json as CreatorsJson,
                    date as Date,
                    publication_title as PublicationTitle,
                    publisher as Publisher,
                    place as Place,
                    volume as Volume,
                    issue as Issue,
                    pages as Pages,
                    language as Language,
                    abstract as Abstract,
                    tags_json as TagsJson,
                    collections_json as CollectionsJson,
                    custom_fields_json as CustomFieldsJson,
                    created_at as CreatedAt,
                    updated_at as UpdatedAt
                from items
                where item_id = @ItemId;
                """,
                new { ItemId = itemId.ToString() });

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
                "select count(1) from items where item_id = @ItemId;",
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
            item.Title,
            item.Subtitle,
            item.CreatorsJson,
            item.Date,
            item.PublicationTitle,
            item.Publisher,
            item.Place,
            item.Volume,
            item.Issue,
            item.Pages,
            item.Language,
            Abstract = item.Abstract,
            item.TagsJson,
            item.CollectionsJson,
            item.CustomFieldsJson,
            CreatedAt = FormatUtc(item.CreatedAt),
            UpdatedAt = FormatUtc(item.UpdatedAt)
        };
    }

    private static string DefaultJsonArray(string? value) => string.IsNullOrWhiteSpace(value) ? "[]" : value;
    private static string DefaultJsonObject(string? value) => string.IsNullOrWhiteSpace(value) ? "{}" : value;
    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string FormatUtc(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    private static Result<T> DatabaseFailure<T>(Exception exception)
    {
        return Result<T>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
    }

    private sealed class ItemRow
    {
        public string ItemId { get; set; } = string.Empty;
        public string LibraryId { get; set; } = string.Empty;
        public string ItemType { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Subtitle { get; set; }
        public string CreatorsJson { get; set; } = "[]";
        public string? Date { get; set; }
        public string? PublicationTitle { get; set; }
        public string? Publisher { get; set; }
        public string? Place { get; set; }
        public string? Volume { get; set; }
        public string? Issue { get; set; }
        public string? Pages { get; set; }
        public string? Language { get; set; }
        public string? Abstract { get; set; }
        public string TagsJson { get; set; } = "[]";
        public string CollectionsJson { get; set; } = "[]";
        public string CustomFieldsJson { get; set; } = "{}";
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;

        public ItemMetadata ToMetadata()
        {
            return new ItemMetadata(
                LiteratureApp.Core.Ids.ItemId.Parse(ItemId),
                LiteratureApp.Core.Ids.LibraryId.Parse(LibraryId),
                ItemType,
                Title,
                Subtitle,
                CreatorsJson,
                Date,
                PublicationTitle,
                Publisher,
                Place,
                Volume,
                Issue,
                Pages,
                Language,
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
                LiteratureApp.Core.Ids.IdentifierId.Parse(IdentifierId),
                LiteratureApp.Core.Ids.ItemId.Parse(ItemId),
                Scheme,
                Value,
                Note,
                DateTimeOffset.Parse(CreatedAt));
        }
    }
}
