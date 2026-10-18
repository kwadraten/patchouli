using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Library;
using Patchouli.Core.Pagination;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Snapshots;

namespace Patchouli.Tests;

public sealed class ChapterWorkflowIntegrationTests
{
    [Fact]
    public async Task Sidebar_chapter_write_publishes_a_committed_change_and_round_trips_through_snapshot()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        LibraryRevisionService revisions = new(fixture.ConnectionFactory);
        DocumentPageMapService maps = new(fixture.ConnectionFactory, fixture.Clock, fixture.Documents,
            fixture.Pages, fixture.Trees, revisions);
        DocumentPageMap original = new(fixture.DocumentId,
            [new PhysicalPaginationRule(1, 3, 20)], [],
            [new PageLabelOverride(new PageLabelTarget(2, null), "xxi")], []);
        (await maps.ReplaceFromYamlAsync(fixture.DocumentId,
            DocumentPageMapYamlCodec.Encode(original, true))).IsSuccess.Should().BeTrue();
        ChapterEditingSnapshot snapshot = (await maps.GetChapterEditingSnapshotAsync(fixture.DocumentId)).Value;
        LibraryChangeSet? published = null;
        revisions.ChangeCommitted += (_, args) => published = args.ChangeSet;
        ChapterNode chapter = ChapterNode.Leaf("manual-introduction", "绪论", new NavigationPosition(2));

        Result<ChapterEditingSnapshot> saved = await maps.SaveChaptersAsync(fixture.DocumentId,
            new ChapterSaveRequest(snapshot.VersionToken, [chapter]));

        saved.IsSuccess.Should().BeTrue(saved.ErrorMessage);
        published.Should().NotBeNull();
        published!.DocumentInstanceIds.Should().Contain(fixture.DocumentId);
        Result<string> yaml = await maps.GetMapYamlAsync(fixture.DocumentId);
        yaml.IsSuccess.Should().BeTrue(yaml.ErrorMessage);
        DocumentPageMap projection = DocumentPageMapYamlCodec.Decode(yaml.Value, fixture.DocumentId).Value;
        projection.Chapters.Should().BeEquivalentTo(new[] { chapter });
        projection.PhysicalRules.Should().BeEquivalentTo(original.PhysicalRules);
        projection.PageLabels.Should().BeEquivalentTo(original.PageLabels);

        string directory = Path.Combine(Path.GetTempPath(), "chapter-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Result<SnapshotPublishResult> exported = await new SnapshotPublisher(fixture.Clock)
                .PublishSnapshotAsync(new SnapshotPublishRequest(fixture.Database.Path,
                    Path.Combine(directory, "sync"), "chapter-test"));
            exported.IsSuccess.Should().BeTrue(exported.ErrorMessage);
            Result<SnapshotImportResult> imported = await new SnapshotImporter().ImportSnapshotToStagingAsync(
                new SnapshotImportRequest(exported.Value.ManifestPath, Path.Combine(directory, "staging")));
            imported.IsSuccess.Should().BeTrue(imported.ErrorMessage);
            imported.Value.IsValid.Should().BeTrue();
            imported.Value.StagingDatabasePath.Should().NotBeNull();
            await using SqliteConnection staged = new(new SqliteConnectionStringBuilder
            {
                DataSource = imported.Value.StagingDatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            await staged.OpenAsync();
            string definition = await staged.QuerySingleAsync<string>(
                "select definition_json from document_page_maps where document_instance_id = @Id;",
                new { Id = fixture.DocumentId.ToString() });
            definition.Should().Contain("manual-introduction");
            definition.Should().Contain("physical_rules");
            definition.Should().Contain("xxi");
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryWithRetry(directory);
        }
    }
}
