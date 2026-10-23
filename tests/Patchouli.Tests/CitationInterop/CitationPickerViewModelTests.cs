using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Search;
using Patchouli.Host.CitationInterop;
using Patchouli.UI.ViewModels.Citations;

namespace Patchouli.Tests.CitationInterop;

/// <summary>Citation picker view-model behavior (issue #14 §8), UI-free.</summary>
public sealed class CitationPickerViewModelTests
{
    private sealed class FakeQueryService : ILibraryItemQueryService
    {
        public List<LibraryItemRow> All { get; } = new();

        public string? LastSearchText { get; private set; }

        public Task<Result<IReadOnlyList<LibraryItemRow>>> ListRowsAsync(
            IReadOnlyList<string>? requiredTags = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<LibraryItemRow>>.Success(All));
        }

        public Task<Result<LibraryItemPage>> ListRowsAsync(
            int limit, LibraryItemCursor? after, IReadOnlyList<string>? requiredTags = null,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<LibraryItemRow> rows = after is null
                ? All.Take(limit).ToArray()
                : All.SkipWhile(row => row.ItemId != after.ItemId).Skip(1).Take(limit).ToArray();
            return Task.FromResult(Result<LibraryItemPage>.Success(
                new LibraryItemPage(rows, null, false)));
        }

        public Task<Result<IReadOnlyList<LibraryItemRow>>> SearchRowsAsync(
            BibliographicItemSearch search, CancellationToken cancellationToken = default)
        {
            LastSearchText = search.Query;
            IReadOnlyList<LibraryItemRow> rows = All
                .Where(row => row.Title.Contains(search.Query ?? "", StringComparison.OrdinalIgnoreCase))
                .Take(search.Limit)
                .ToArray();
            return Task.FromResult(Result<IReadOnlyList<LibraryItemRow>>.Success(rows));
        }

        public Task<Result<IReadOnlyList<LibraryItemRow>>> GetRowsByIdsAsync(
            IReadOnlyCollection<ItemId> itemIds, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<IReadOnlyList<LibraryItemRow>>> ListTrashedRowsAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<int>> CountUntaggedItemsAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<LibraryItemPage>> ListTrashedRowsAsync(
            int limit, LibraryItemCursor? after, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<IReadOnlyList<ItemId>>> GetItemIdsByDocumentInstanceIdsAsync(
            IReadOnlyCollection<DocumentInstanceId> documentInstanceIds, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<DocumentNavigationRow?>> GetDocumentNavigationAsync(
            DocumentInstanceId documentInstanceId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<IReadOnlyList<LibraryItemRow>>> ResolveRowsByCitationKeysAsync(
            IReadOnlyCollection<string> citationKeys, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<BibliographicSearchFilterOptions>> GetSearchFilterOptionsAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private static LibraryItemRow Row(string title, string type = "book")
    {
        return new LibraryItemRow(
            ItemId.New(), title, type, "Austen, Jane", "1813", null, null, null, null, null,
            "", "2026-01-01", 0, 0, false, PrimaryDocumentOcrIndexState.FromValue(null), "ok");
    }

    private static CitationPickerViewModel CreateVm(FakeQueryService query)
    {
        // A huge debounce keeps the timer path out of the tests; searches are
        // driven explicitly through SearchNowAsync.
        return new CitationPickerViewModel(query, TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task Initial_load_lists_recent_items_without_general()
    {
        FakeQueryService query = new();
        query.All.Add(Row("Book One"));
        query.All.Add(Row("Loose Note", "general"));
        CitationPickerViewModel vm = CreateVm(query);

        await vm.InitializeAsync();

        vm.Results.Select(result => result.Title).Should().Equal("Book One");
    }

    [Fact]
    public async Task Search_uses_the_query_service_free_text()
    {
        FakeQueryService query = new();
        query.All.Add(Row("Pride and Prejudice"));
        query.All.Add(Row("Jane Eyre"));
        CitationPickerViewModel vm = CreateVm(query);
        vm.SearchText = "pride";

        await vm.SearchNowAsync();

        query.LastSearchText.Should().Be("pride");
        vm.Results.Select(result => result.Title).Should().Equal("Pride and Prejudice");
    }

    [Fact]
    public async Task Add_remove_and_reorder_build_the_pick_in_order()
    {
        FakeQueryService query = new();
        query.All.Add(Row("First"));
        query.All.Add(Row("Second"));
        CitationPickerViewModel vm = CreateVm(query);
        await vm.InitializeAsync();

        vm.AddResultCommand.Execute(vm.Results[1]);
        vm.AddResultCommand.Execute(vm.Results[0]);
        vm.Picked.Select(entry => entry.Title).Should().Equal("Second", "First");

        // Duplicate add is refused.
        vm.AddResultCommand.Execute(vm.Results[0]);
        vm.Picked.Should().HaveCount(2);

        vm.MoveUpCommand.Execute(vm.Picked[1]);
        vm.Picked.Select(entry => entry.Title).Should().Equal("First", "Second");

        vm.RemoveEntryCommand.Execute(vm.Picked[0]);
        vm.Picked.Select(entry => entry.Title).Should().Equal("Second");
        vm.Results.Single(result => result.Title == "First").IsPicked.Should().BeFalse();
    }

    [Fact]
    public async Task Entry_edits_map_to_the_bridge_entry()
    {
        FakeQueryService query = new();
        query.All.Add(Row("Book"));
        CitationPickerViewModel vm = CreateVm(query);
        await vm.InitializeAsync();
        vm.AddResultCommand.Execute(vm.Results[0]);

        CitationPickerEntryViewModel entry = vm.Picked.Single();
        entry.Locator = " 33 ";
        entry.Label = "chapter";
        entry.Prefix = "see";
        entry.Suffix = "and more";
        entry.SuppressAuthor = true;

        CitationBridgePickEntry bridge = entry.ToBridgeEntry();
        bridge.Locator.Should().Be("33");
        bridge.Label.Should().Be("chapter");
        bridge.Prefix.Should().Be("see");
        bridge.Suffix.Should().Be("and more");
        bridge.SuppressAuthor.Should().BeTrue();
    }

    [Fact]
    public async Task Confirm_and_cancel_produce_bridge_results()
    {
        FakeQueryService query = new();
        query.All.Add(Row("Book"));
        CitationPickerViewModel vm = CreateVm(query);
        await vm.InitializeAsync();
        vm.AddResultCommand.Execute(vm.Results[0]);

        CitationPickBridgeResult? result = null;
        vm.RequestClose = bridgeResult => result = bridgeResult;
        vm.ConfirmCommand.Execute(null);

        result.Should().NotBeNull();
        result!.Outcome.Should().Be(CitationPickOutcome.Confirmed);
        result.Entries.Should().ContainSingle();

        vm.CancelCommand.Execute(null);
        result!.Outcome.Should().Be(CitationPickOutcome.Cancelled);
    }

    [Fact]
    public async Task Empty_search_reports_a_status_message()
    {
        FakeQueryService query = new();
        CitationPickerViewModel vm = CreateVm(query);
        vm.SearchText = "nothing matches this";

        await vm.SearchNowAsync();

        vm.Results.Should().BeEmpty();
        vm.StatusText.Should().Contain("没有找到");
    }

    [Fact]
    public void Add_result_refuses_entries_past_the_pick_cap()
    {
        FakeQueryService query = new();
        CitationPickerViewModel vm = CreateVm(query);

        // Fill the pick to the service cap with directly built result rows
        // (the add path under test does not involve search).
        for (int i = 0; i < CitationInteropService.MaxPickEntries; i++)
        {
            vm.AddResultCommand.Execute(
                new CitationPickerResultViewModel(Row($"Book {i}"), vm.AddResultCommand));
        }

        vm.Picked.Should().HaveCount(CitationInteropService.MaxPickEntries);

        CitationPickerResultViewModel overflow =
            new(Row("One Too Many"), vm.AddResultCommand);
        vm.AddResultCommand.Execute(overflow);

        vm.Picked.Should().HaveCount(CitationInteropService.MaxPickEntries,
            "the add path must refuse past the cap instead of letting confirm fail the whole pick");
        overflow.IsPicked.Should().BeFalse();
        vm.StatusText.Should().Contain(CitationInteropService.MaxPickEntries.ToString());
    }
}
