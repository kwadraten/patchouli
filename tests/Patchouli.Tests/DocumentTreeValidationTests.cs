using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class DocumentTreeValidationTests
{
    [Fact]
    public async Task ValidateStoredTrees_validates_revisions_across_the_500_revision_batch_boundary()
    {
        await using Context context = await Context.CreateAsync();
        await context.SeedCommittedRevisionsAsync(501);

        Result result = await context.Trees.ValidateStoredTreesAsync();

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
    }

    [Fact]
    public async Task ValidateStoredTrees_reports_invalid_tree_after_the_500_revision_batch_boundary()
    {
        await using Context context = await Context.CreateAsync();
        await context.SeedCommittedRevisionsAsync(501, 500);

        Result result = await context.Trees.ValidateStoredTreesAsync();

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Contain("A physical page cannot mix direct leaf boxes with logical-page roots.");
    }

    private sealed class Context : IAsyncDisposable
    {
        private readonly TemporarySqliteDatabase _database;

        private Context(
            TemporarySqliteDatabase database,
            DocumentInstanceId documentInstanceId,
            PageId pageId,
            DocumentTreeService trees)
        {
            _database = database;
            DocumentInstanceId = documentInstanceId;
            PageId = pageId;
            Trees = trees;
        }

        public DocumentInstanceId DocumentInstanceId { get; }

        public PageId PageId { get; }

        public DocumentTreeService Trees { get; }

        public static async Task<Context> CreateAsync()
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();

            DocumentInstanceId documentInstanceId = DocumentInstanceId.New();
            PageId pageId = PageId.New();
            string now = DateTimeOffset.UtcNow.ToString("O");
            string libraryId = LibraryId.New().ToString();
            string itemId = ItemId.New().ToString();
            await using (SqliteConnection connection = database.ConnectionFactory.CreateConnection())
            {
                await connection.OpenAsync();
                await connection.ExecuteAsync(
                    """
                    insert into library_metadata values (@LibraryId, 'Test', 2, @Now, @Now, 1);
                    insert into items (
                        item_id, library_id, item_type, title, creators_json, tags_json,
                        collections_json, custom_fields_json, created_at, updated_at)
                    values (@ItemId, @LibraryId, 'book', 'Test', '[]', '[]', '[]', '{}', @Now, @Now);
                    insert into document_instances (
                        document_instance_id, item_id, file_asset_id, title, instance_type,
                        is_primary, status, created_at, updated_at)
                    values (@DocumentId, @ItemId, null, 'Test', 'scan', 1, 'active', @Now, @Now);
                    insert into pages (
                        page_id, document_instance_id, page_index, page_label, width, height,
                        rotation, coordinate_basis, basis_width, basis_height,
                        renderer_basis_version, source_file_hash, created_at, updated_at)
                    values (@PageId, @DocumentId, 0, '1', 100, 100, 0, 'upright_render',
                        100, 100, 'test-v1', null, @Now, @Now);
                    """,
                    new
                    {
                        LibraryId = libraryId,
                        ItemId = itemId,
                        DocumentId = documentInstanceId.ToString(),
                        PageId = pageId.ToString(),
                        Now = now
                    });
            }

            DocumentTreeService trees = new(database.ConnectionFactory, new SystemClock(), new MarkdigMarkdownEngine());
            return new Context(database, documentInstanceId, pageId, trees);
        }

        public async Task SeedCommittedRevisionsAsync(int count, int invalidRevisionIndex = -1)
        {
            await using SqliteConnection connection = _database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            using SqliteTransaction transaction = connection.BeginTransaction();
            string now = DateTimeOffset.UtcNow.ToString("O");
            string? parentRevisionId = null;

            for (int index = 0; index < count; index++)
            {
                string revisionId = CreateRevisionId(index);
                await connection.ExecuteAsync(
                    """
                    insert into document_tree_revisions (
                        tree_revision_id, document_instance_id, page_id, parent_tree_revision_id,
                        source, status, is_current, created_at, committed_at)
                    values (@RevisionId, @DocumentId, @PageId, @ParentRevisionId,
                        'manual_edit', 'committed', 0, @Now, @Now);
                    """,
                    new
                    {
                        RevisionId = revisionId,
                        DocumentId = DocumentInstanceId.ToString(),
                        PageId = PageId.ToString(),
                        ParentRevisionId = parentRevisionId,
                        Now = now
                    },
                    transaction);

                if (index == invalidRevisionIndex)
                {
                    await InsertInvalidMixedTreeAsync(connection, transaction, revisionId);
                }
                else
                {
                    await InsertTextBoxAsync(connection, transaction, revisionId, DocumentBoxId.New().ToString());
                }

                parentRevisionId = revisionId;
            }

            transaction.Commit();
        }

        private async Task InsertInvalidMixedTreeAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string revisionId)
        {
            string textBoxId = DocumentBoxId.New().ToString();
            await InsertTextBoxAsync(connection, transaction, revisionId, textBoxId);
            await connection.ExecuteAsync(
                """
                insert into document_boxes (
                    tree_revision_id, box_id, document_instance_id, page_id, next_sibling_box_id,
                    box_type, bbox_x, bbox_y, bbox_width, bbox_height, payload_json, suppressed)
                values (@RevisionId, @BoxId, @DocumentId, @PageId, @TextBoxId,
                    'logical_page', 0.1, 0.1, 0.5, 0.5, null, 0);
                """,
                new
                {
                    RevisionId = revisionId,
                    BoxId = DocumentBoxId.New().ToString(),
                    DocumentId = DocumentInstanceId.ToString(),
                    PageId = PageId.ToString(),
                    TextBoxId = textBoxId
                },
                transaction);
        }

        private async Task InsertTextBoxAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string revisionId,
            string boxId)
        {
            await connection.ExecuteAsync(
                """
                insert into document_boxes (
                    tree_revision_id, box_id, document_instance_id, page_id,
                    box_type, bbox_x, bbox_y, bbox_width, bbox_height, payload_json, suppressed)
                values (@RevisionId, @BoxId, @DocumentId, @PageId,
                    'text', 0.1, 0.1, 0.5, 0.1, '{"markdown":"valid"}', 0);
                """,
                new
                {
                    RevisionId = revisionId,
                    BoxId = boxId,
                    DocumentId = DocumentInstanceId.ToString(),
                    PageId = PageId.ToString()
                },
                transaction);
        }

        private static string CreateRevisionId(int index)
        {
            return $"00000000-0000-0000-0000-{index + 1:D12}";
        }

        public ValueTask DisposeAsync()
        {
            return _database.DisposeAsync();
        }
    }
}
