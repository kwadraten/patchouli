using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Host.Import;
using Patchouli.Infrastructure.Bibliography.Biblatex;
using Patchouli.UI;

namespace Patchouli.Tests;

public sealed class BiblatexImportPromptTests
{
    [Fact]
    public async Task Entry_picker_preserves_warning_and_archive_fields_through_host_import()
    {
        File.Exists(BiblatexHelperClient.ResolveDefaultHelperPath()).Should().BeTrue();
        string root = Path.Combine(Path.GetTempPath(), $"patchouli-bib-prompt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string db = Path.Combine(root, "runtime.sqlite");
        try
        {
            HostServices services = await HostServices.CreateAsync(db, PatchouliAppSettings.Default() with
            {
                Runtime = PatchouliAppSettings.Default().Runtime with
                {
                    RuntimeDatabasePath = db,
                    DefaultSyncRoot = Path.Combine(root, "sync"),
                    DefaultStagingRoot = Path.Combine(root, "staging"),
                    LogDirectory = Path.Combine(root, "logs"),
                    UseMockOcrOnly = true
                }
            });
            (await services.Library.CreateLibraryAsync("Prompt regression")).IsSuccess.Should().BeTrue();
            RecordingPrompt prompt = new();
            LibraryImportOrchestrator orchestrator = new(services, importPrompt: prompt);
            Result<BiblatexImportApplyResult?> result = await orchestrator.ImportBiblatexTextAsync("""
                @book{other,title={Other},author={Doe, John},year={2024},publisher={P}}
                @unpublished{archive,title={Archive record},author={Doe, Jane},
                    archive={City Archive},archive_location={Box 12},callnumber={MS 42}}
                """, null, null);
            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            result.Value.Should().NotBeNull();
            prompt.Request.Should().NotBeNull();
            prompt.Request!.Entries.Single(entry => entry.SourceEntryKey == "archive")
                .Warnings.Should().NotBeEmpty();
            result.Value!.StatusMessage.Should().Contain("警告");
            Result<ItemMetadata> item =
                await services.Items.GetItemAsync(ItemId.Parse(result.Value.CreatedItemIds.Single()));
            item.IsSuccess.Should().BeTrue(item.ErrorMessage);
            item.Value.Title.Should().Be("Archive record");
            Result<string> exported = await services.BiblatexImport.ExportItemsAsync([item.Value.ItemId]);
            exported.IsSuccess.Should().BeTrue(exported.ErrorMessage);
            exported.Value.Should().Contain("City Archive").And.Contain("Box 12").And.Contain("MS 42");
        }
        finally
        {
            SqliteTestCleanup.ReleasePools(db);
            Directory.Delete(root, true);
        }
    }

    private sealed class RecordingPrompt : IBiblatexImportPrompt
    {
        public BiblatexEntryPickRequest? Request { get; private set; }

        public Task<string?> SelectEntryKeyAsync(BiblatexEntryPickRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult<string?>("archive");
        }

        public Task<bool> ConfirmSilentBatchCreateAsync(BiblatexBatchConfirmRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Single import must use the entry picker.");
        }
    }
}
