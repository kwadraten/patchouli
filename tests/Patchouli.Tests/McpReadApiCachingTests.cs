using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Evidence;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Evidence;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Search;
using Patchouli.Mcp;

namespace Patchouli.Tests;

public sealed class McpReadApiCachingTests
{
    [Fact]
    public async Task Repeated_page_text_call_compiles_once_and_separates_suppression_option()
    {
        await using Context context = await Context.CreateAsync();

        McpPageTextResponse first = (await context.Api.GetPageTextAsync(
            new McpPageTextRequest(context.PageId))).Value;
        McpPageTextResponse second = (await context.Api.GetPageTextAsync(
            new McpPageTextRequest(context.PageId))).Value;
        McpPageTextResponse includingSuppressed = (await context.Api.GetPageTextAsync(
            new McpPageTextRequest(context.PageId, IncludeSuppressed: true))).Value;

        first.Text.Should().Be("without suppressed");
        second.Text.Should().Be("without suppressed");
        includingSuppressed.Text.Should().Be("with suppressed");
        context.Compiler.CallCount.Should().Be(2);
        context.Compiler.Calls.Should().BeEquivalentTo(
        [
            (context.RevisionId, false, true),
            (context.RevisionId, true, true)
        ]);
    }

    [Fact]
    public async Task Concurrent_page_text_calls_share_one_compile()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Context context = await Context.CreateAsync(release.Task);

        Task<Result<McpPageTextResponse>>[] requests = Enumerable.Range(0, 8)
            .Select(_ => context.Api.GetPageTextAsync(new McpPageTextRequest(context.PageId)))
            .ToArray();
        await context.Compiler.FirstCall.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        context.Compiler.CallCount.Should().Be(1);
        release.SetResult();
        Result<McpPageTextResponse>[] results = await Task.WhenAll(requests);
        results.Should().OnlyContain(result => result.IsSuccess && result.Value.Text == "without suppressed");
        context.Compiler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Cache_evicts_least_recently_used_markdown_when_byte_limit_is_exceeded()
    {
        CompiledMarkdownCache cache = new(2048);
        DocumentTreeRevisionId first = DocumentTreeRevisionId.New();
        DocumentTreeRevisionId second = DocumentTreeRevisionId.New();
        DocumentTreeRevisionId third = DocumentTreeRevisionId.New();
        int compileCount = 0;

        async Task<Result<CompiledMarkdown>> GetAsync(DocumentTreeRevisionId revisionId)
        {
            return await cache.GetOrCreateAsync(revisionId, false, true, _ =>
            {
                Interlocked.Increment(ref compileCount);
                return Task.FromResult(Result<CompiledMarkdown>.Success(
                    new CompiledMarkdown("1234", [], [])));
            }, CancellationToken.None);
        }

        await GetAsync(first);
        await GetAsync(second);
        await GetAsync(first);
        await GetAsync(third);
        await GetAsync(first);
        await GetAsync(second);

        compileCount.Should().Be(4);
    }

    [Fact]
    public async Task Cache_retries_failures()
    {
        CompiledMarkdownCache cache = new();
        DocumentTreeRevisionId revisionId = DocumentTreeRevisionId.New();
        int compileCount = 0;

        Task<Result<CompiledMarkdown>> Compile(CancellationToken _)
        {
            int call = Interlocked.Increment(ref compileCount);
            return Task.FromResult(call == 1
                ? Result<CompiledMarkdown>.Failure("compile_failed", "Compilation failed.")
                : Result<CompiledMarkdown>.Success(new CompiledMarkdown("success", [], [])));
        }

        Result<CompiledMarkdown> first = await cache.GetOrCreateAsync(
            revisionId, false, true, Compile, CancellationToken.None);
        Result<CompiledMarkdown> second = await cache.GetOrCreateAsync(
            revisionId, false, true, Compile, CancellationToken.None);

        first.IsFailure.Should().BeTrue();
        second.Value.Markdown.Should().Be("success");
        compileCount.Should().Be(2);
    }

    [Fact]
    public async Task Canceling_one_waiter_does_not_cancel_shared_generation()
    {
        CompiledMarkdownCache cache = new();
        DocumentTreeRevisionId revisionId = DocumentTreeRevisionId.New();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int compileCount = 0;

        async Task<Result<CompiledMarkdown>> Compile(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref compileCount);
            await release.Task.WaitAsync(cancellationToken);
            return Result<CompiledMarkdown>.Success(new CompiledMarkdown("shared", [], []));
        }

        using CancellationTokenSource canceledWaiter = new();
        Task<Result<CompiledMarkdown>> first = cache.GetOrCreateAsync(
            revisionId, false, true, Compile, canceledWaiter.Token);
        Task<Result<CompiledMarkdown>> second = cache.GetOrCreateAsync(
            revisionId, false, true, Compile, CancellationToken.None);
        canceledWaiter.Cancel();

        Func<Task> canceled = async () => await first;
        await canceled.Should().ThrowAsync<OperationCanceledException>();
        release.SetResult();
        (await second).Value.Markdown.Should().Be("shared");
        compileCount.Should().Be(1);
    }

    private sealed class Context : IAsyncDisposable
    {
        private Context(TemporarySqliteDatabase database, PageId pageId, DocumentTreeRevisionId revisionId,
            CountingCompiler compiler, McpReadApi api)
        {
            Database = database;
            PageId = pageId;
            RevisionId = revisionId;
            Compiler = compiler;
            Api = api;
        }

        public TemporarySqliteDatabase Database { get; }
        public PageId PageId { get; }
        public DocumentTreeRevisionId RevisionId { get; }
        public CountingCompiler Compiler { get; }
        public McpReadApi Api { get; }

        public static async Task<Context> CreateAsync(Task? compileGate = null)
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-30T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
            LibraryMetadata library = (await libraries.CreateLibraryAsync("MCP cache tests")).Value;
            ItemMetadata item = (await new ItemService(database.ConnectionFactory, libraries, clock)
                .CreateItemAsync("document", "Cached page")).Value;
            DocumentInstance document = (await new DocumentInstanceService(database.ConnectionFactory, clock)
                .AttachDocumentInstanceAsync(item.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
            Page page = (await new PageService(database.ConnectionFactory, clock)
                .CreatePageAsync(document.DocumentInstanceId, 0, "1", null, null, 0,
                    CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;
            DocumentTreeRevision revision = await BoxTreeTestData.CommitTextAsync(database.ConnectionFactory, clock,
                document.DocumentInstanceId, page.PageId, "source text");
            CountingCompiler compiler = new(compileGate);
            McpReadApi api = new(database.ConnectionFactory, new SqliteSearchService(database.ConnectionFactory),
                new EvidenceReferenceService(database.ConnectionFactory, clock), markdownCompiler: compiler);
            return new Context(database, page.PageId, revision.TreeRevisionId, compiler, api);
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }

    private sealed class CountingCompiler(Task? gate) : IDocumentMarkdownCompiler
    {
        private readonly Task? _gate = gate;
        private readonly TaskCompletionSource _firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly List<(DocumentTreeRevisionId RevisionId, bool IncludeSuppressed, bool IncludeComplexTableHtml)>
            _calls = [];

        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);
        public Task FirstCall => _firstCall.Task;

        public IReadOnlyList<(DocumentTreeRevisionId RevisionId, bool IncludeSuppressed,
            bool IncludeComplexTableHtml)> Calls
        {
            get
            {
                lock (_calls)
                {
                    return _calls.ToArray();
                }
            }
        }

        public async Task<Result<CompiledMarkdown>> CompilePageMarkdownAsync(DocumentTreeRevisionId treeRevisionId,
            bool includeSuppressed = false, CancellationToken cancellationToken = default,
            bool includeComplexTableHtml = false)
        {
            Interlocked.Increment(ref _callCount);
            lock (_calls)
            {
                _calls.Add((treeRevisionId, includeSuppressed, includeComplexTableHtml));
            }

            _firstCall.TrySetResult();
            if (_gate is not null)
            {
                await _gate.WaitAsync(cancellationToken);
            }

            string markdown = includeSuppressed ? "with suppressed" : "without suppressed";
            return Result<CompiledMarkdown>.Success(new CompiledMarkdown(markdown, [], []));
        }
    }
}
