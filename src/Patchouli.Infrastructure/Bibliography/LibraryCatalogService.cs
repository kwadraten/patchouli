using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.Bibliography;

/// <summary>One transaction for catalog replacement and relationship removal. Never deletes Items.</summary>
public sealed class LibraryCatalogService(
    SqliteConnectionFactory connections,
    ILibraryIdentityService identity,
    IClock clock,
    ILibraryRevisionService revisions) : ILibraryCatalogService
{
    public async Task<Result<LibraryCatalogChange>> ReplaceAsync(LibraryCatalogUpdate update,
        CancellationToken cancellationToken = default)
    {
        Result<LibraryMetadata> library = await identity.GetCurrentLibraryAsync(cancellationToken);
        if (library.IsFailure)
        {
            return Result<LibraryCatalogChange>.Failure(library.ErrorCode!, library.ErrorMessage!);
        }

        string libraryId = library.Value.LibraryId.ToString();
        if (update.LibraryId != libraryId)
        {
            return Result<LibraryCatalogChange>.Failure(AppErrorCodes.NotFound, "Library identity does not match.");
        }

        if (update.DisplayName is not null && update.DisplayName != library.Value.DisplayName)
        {
            return Invalid("display_name is read-only.");
        }

        string[]? tags = update.Tags?.Select(tag => tag.Trim()).ToArray();
        LibraryCollectionDefinition[]? definitions = update.Collections?
            .Select(value => value with { Name = value.Name.Trim() }).ToArray();
        if ((tags is not null && (tags.Any(string.IsNullOrWhiteSpace) ||
                                  tags.Distinct(StringComparer.Ordinal).Count() != tags.Length)) ||
            (definitions is not null && (definitions.Any(value => string.IsNullOrWhiteSpace(value.Name)) ||
                                         definitions.Select(value => value.Name).Distinct(StringComparer.Ordinal)
                                             .Count() != definitions.Length)))
        {
            return Invalid("Names must be nonempty and unique within each category.");
        }

        try
        {
            using IDisposable lease = await connections.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = connections.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            Dictionary<string, string> existing = (await connection.QueryAsync<CatalogRow>(
                "select collection_id as Id, name as Name from collections where library_id = @LibraryId;",
                new { LibraryId = libraryId }, transaction)).ToDictionary(value => value.Id, value => value.Name);
            // Only IDs issued by this Library identify updates. Agent-invented IDs
            // are discarded before duplicate-ID validation and host-side ID generation.
            definitions = definitions?.Select(value =>
                value.CollectionId is { } id && existing.ContainsKey(id.ToString())
                    ? value
                    : value with { CollectionId = null }).ToArray();
            if (definitions is not null && definitions.Where(value => value.CollectionId is not null)
                    .Select(value => value.CollectionId).Distinct().Count() !=
                definitions.Count(value => value.CollectionId is not null))
            {
                return Invalid("Existing collection ids must be unique within the category.");
            }

            if (definitions is not null && definitions.Any(value =>
                    value.CollectionId is null && existing.Values.Contains(value.Name, StringComparer.Ordinal)))
            {
                return Invalid(
                    "Keep collection_id for existing collections; omit it only when creating a new collection.");
            }

            HashSet<ItemId> affectedItems = [];
            List<CollectionId> affectedCollections = [];
            int createdCollections = 0, renamedCollections = 0, dissolvedCollections = 0;
            int createdTags = 0, deletedTags = 0, removedMemberships = 0;
            bool changed = false;
            string now = clock.UtcNow.ToUniversalTime().ToString("O");
            if (definitions is not null)
            {
                HashSet<string> retained = definitions.Where(value => value.CollectionId is not null)
                    .Select(value => value.CollectionId!.Value.ToString()).ToHashSet(StringComparer.Ordinal);
                foreach ((string id, string _) in existing)
                {
                    if (retained.Contains(id))
                    {
                        continue;
                    }

                    foreach (string item in await connection.QueryAsync<string>(
                                 "select item_id from item_collections where collection_id = @Id;", new { Id = id },
                                 transaction))
                    {
                        affectedItems.Add(ItemId.Parse(item));
                    }

                    removedMemberships += await connection.ExecuteAsync(
                        "delete from item_collections where collection_id = @Id;", new { Id = id }, transaction);
                    await connection.ExecuteAsync("delete from collections where collection_id = @Id;", new { Id = id },
                        transaction);
                    affectedCollections.Add(CollectionId.Parse(id));
                    dissolvedCollections++;
                    changed = true;
                }

                // Temporarily vacate names so valid rename swaps do not hit the unique-name constraint.
                foreach (LibraryCollectionDefinition definition in definitions.Where(value =>
                             value.CollectionId is not null))
                {
                    string id = definition.CollectionId!.Value.ToString();
                    if (existing[id] == definition.Name)
                    {
                        continue;
                    }

                    await connection.ExecuteAsync("update collections set name = @Name where collection_id = @Id;",
                        new { Id = id, Name = $"catalog-rename-{Guid.NewGuid():N}" }, transaction);
                }

                foreach (LibraryCollectionDefinition definition in definitions)
                {
                    CollectionId id = definition.CollectionId ?? CollectionId.New();
                    if (definition.CollectionId is null)
                    {
                        await connection.ExecuteAsync(
                            "insert into collections (collection_id, library_id, name, created_at, updated_at) values (@Id, @LibraryId, @Name, @Now, @Now);",
                            new { Id = id.ToString(), LibraryId = libraryId, definition.Name, Now = now }, transaction);
                        createdCollections++;
                    }
                    else if (existing[id.ToString()] != definition.Name)
                    {
                        await connection.ExecuteAsync(
                            "update collections set name = @Name, updated_at = @Now where collection_id = @Id;",
                            new { Id = id.ToString(), definition.Name, Now = now }, transaction);
                        renamedCollections++;
                    }
                    else
                    {
                        continue;
                    }

                    affectedCollections.Add(id);
                    changed = true;
                }
            }

            if (tags is not null)
            {
                HashSet<string> previous = (await connection.QueryAsync<string>(
                    "select tag from library_tag_definitions where library_id = @LibraryId union select tag from item_tag_memberships where library_id = @LibraryId;",
                    new { LibraryId = libraryId }, transaction)).ToHashSet(StringComparer.Ordinal);
                HashSet<string> desired = tags.ToHashSet(StringComparer.Ordinal);
                string[] removedTags = previous.Except(desired, StringComparer.Ordinal).ToArray();
                createdTags = desired.Except(previous, StringComparer.Ordinal).Count();
                deletedTags = removedTags.Length;
                if (removedTags.Length > 0)
                {
                    HashSet<string> removedNames = removedTags.ToHashSet(StringComparer.Ordinal);
                    ItemTagsRow[] rows = (await connection.QueryAsync<ItemTagsRow>(
                        "select item_id as ItemId, tags_json as TagsJson from items where library_id = @LibraryId and item_id in (select item_id from item_tag_memberships where tag in @Tags);",
                        new { LibraryId = libraryId, Tags = removedTags }, transaction)).ToArray();
                    foreach (ItemTagsRow row in rows)
                    {
                        string[] oldTags = JsonSerializer.Deserialize<string[]>(row.TagsJson) ?? [];
                        string[] next = oldTags.Where(tag => !removedNames.Contains(tag)).ToArray();
                        removedMemberships += oldTags.Length - next.Length;
                        await connection.ExecuteAsync(
                            "update items set tags_json = @TagsJson, updated_at = @Now where item_id = @ItemId;",
                            new { row.ItemId, TagsJson = JsonSerializer.Serialize(next), Now = now }, transaction);
                        affectedItems.Add(ItemId.Parse(row.ItemId));
                    }
                }

                string[] declared = (await connection.QueryAsync<string>(
                    "select tag from library_tag_definitions where library_id = @LibraryId;",
                    new { LibraryId = libraryId }, transaction)).ToArray();
                if (!desired.SetEquals(declared))
                {
                    await connection.ExecuteAsync("delete from library_tag_definitions where library_id = @LibraryId;",
                        new { LibraryId = libraryId }, transaction);
                    foreach (string tag in tags)
                    {
                        await connection.ExecuteAsync(
                            "insert into library_tag_definitions (library_id, tag) values (@LibraryId, @Tag);",
                            new { LibraryId = libraryId, Tag = tag }, transaction);
                    }

                    changed = true;
                }

                changed |= removedTags.Length > 0;
            }

            LibraryCatalogChange result = new(createdCollections, renamedCollections, dissolvedCollections,
                createdTags, deletedTags, removedMemberships, changed);
            if (!changed)
            {
                return Result<LibraryCatalogChange>.Success(result);
            }

            Result<LibraryChangeSet> revision = await revisions.IncrementInTransactionAsync(connection, transaction,
                LibraryChangeSet.Empty with
                {
                    ItemIds = affectedItems.ToArray(), CollectionIds = affectedCollections,
                    TagCatalogChanged = tags is not null
                }, cancellationToken);
            if (revision.IsFailure)
            {
                return Result<LibraryCatalogChange>.Failure(revision.ErrorCode!, revision.ErrorMessage!);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
            revisions.PublishCommitted(revision.Value);
            return Result<LibraryCatalogChange>.Success(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.library-catalog"))
        {
            return Result<LibraryCatalogChange>.Failure(AppErrorCodes.DatabaseError,
                "Library catalog transaction failed.");
        }
    }

    private static Result<LibraryCatalogChange> Invalid(string detail)
    {
        return Result<LibraryCatalogChange>.Failure(AppErrorCodes.ValidationFailed, detail);
    }

    private sealed record CatalogRow(string Id, string Name);

    private sealed record ItemTagsRow(string ItemId, string TagsJson);
}
