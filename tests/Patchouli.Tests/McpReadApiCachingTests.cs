using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
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
            new McpPageTextRequest(context.PageId, true))).Value;

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

    [Fact]
    public async Task Committing_a_new_revision_returns_new_text_and_never_stale_cached_markdown()
    {
        await using Context context = await Context.CreateAsync(useRealMarkdownCompiler: true);
        DocumentTreeRevisionId originalRevision = context.RevisionId;

        McpPageTextResponse initial =
            (await context.Api.GetPageTextAsync(new McpPageTextRequest(context.PageId))).Value;
        initial.Text.Should().Be("source text");
        initial.TreeRevisionId.Should().Be(originalRevision);
        context.Compiler.CallCount.Should().Be(1);

        DocumentTreeRevision committed = await BoxTreeTestData.CommitTextAsync(
            context.Database.ConnectionFactory, context.Clock, context.DocumentId, context.PageId, "second text");

        McpPageTextResponse afterCommit =
            (await context.Api.GetPageTextAsync(new McpPageTextRequest(context.PageId))).Value;
        afterCommit.Text.Should().Be("second text");
        afterCommit.TreeRevisionId.Should().Be(committed.TreeRevisionId);
        context.Compiler.CallCount.Should().Be(2,
            "a committed resource change moves the DocumentTree current pointer to a new immutable revision; the cache must not serve stale markdown");

        McpPageTextResponse reused = (await context.Api.GetPageTextAsync(new McpPageTextRequest(context.PageId))).Value;
        reused.Text.Should().Be("second text");
        context.Compiler.CallCount.Should().Be(2, "the committed revision's markdown is now cached and reused");
    }

    [Fact]
    public async Task Revoked_current_pointer_returns_not_found_and_never_cached_markdown()
    {
        await using Context context = await Context.CreateAsync();
        McpPageTextResponse warm = (await context.Api.GetPageTextAsync(new McpPageTextRequest(context.PageId))).Value;
        warm.Text.Should().Be("without suppressed");
        int compiledBefore = context.Compiler.CallCount;

        await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("update document_tree_revisions set is_current = 0 where page_id = @PageId;",
            new { PageId = context.PageId.ToString() });

        Result<McpPageTextResponse>
            revoked = await context.Api.GetPageTextAsync(new McpPageTextRequest(context.PageId));
        revoked.IsFailure.Should().BeTrue();
        revoked.ErrorCode.Should().Be(AppErrorCodes.NotFound);
        context.Compiler.CallCount.Should().Be(compiledBefore,
            "permission/authority state is honored on every request; the cache never serves content once the current pointer is revoked");
    }

    [Fact]
    public async Task Separate_libraries_never_share_cached_markdown()
    {
        await using Context first = await Context.CreateAsync();
        await using Context second = await Context.CreateAsync();

        McpPageTextResponse firstRead = (await first.Api.GetPageTextAsync(new McpPageTextRequest(first.PageId))).Value;
        McpPageTextResponse secondRead =
            (await second.Api.GetPageTextAsync(new McpPageTextRequest(second.PageId))).Value;

        firstRead.Text.Should().Be("without suppressed");
        secondRead.Text.Should().Be("without suppressed");
        first.Compiler.CallCount.Should().Be(1);
        second.Compiler.CallCount.Should().Be(1,
            "each library is served by its own host-owned cache; the second library must compile its own revision");

        (await first.Api.GetPageTextAsync(new McpPageTextRequest(first.PageId))).Value.Text.Should()
            .Be("without suppressed");
        (await second.Api.GetPageTextAsync(new McpPageTextRequest(second.PageId))).Value.Text.Should()
            .Be("without suppressed");
        first.Compiler.CallCount.Should().Be(1);
        second.Compiler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Identical_text_in_distinct_revisions_is_never_aliased_in_one_cache()
    {
        CompiledMarkdownCache cache = new();
        DocumentTreeRevisionId first = DocumentTreeRevisionId.New();
        DocumentTreeRevisionId second = DocumentTreeRevisionId.New();

        async Task<Result<CompiledMarkdown>> GetAsync(DocumentTreeRevisionId revisionId)
        {
            return await cache.GetOrCreateAsync(revisionId, false, true, _ =>
                    Task.FromResult(Result<CompiledMarkdown>.Success(new CompiledMarkdown("identical text", [], []))),
                CancellationToken.None);
        }

        (await GetAsync(first)).Value.Markdown.Should().Be("identical text");
        (await GetAsync(second)).Value.Markdown.Should().Be("identical text");

        cache.Metrics.CachedEntries.Should().Be(2,
            "the cache key is the immutable globally-unique revision identity, so identical text across revisions (and therefore across libraries) never aliases");
        cache.Metrics.Hits.Should().Be(0);
    }

    [Fact]
    public async Task Ui_compiler_and_mcp_reuse_the_same_host_cache_and_refresh_on_new_revision()
    {
        await using Context context = await Context.CreateAsync(useRealMarkdownCompiler: true);
        CompiledMarkdownCache cache = new();
        CachedDocumentMarkdownCompiler uiCompiler = new(context.Compiler, cache);
        McpReadApi api = new(context.Database.ConnectionFactory,
            new SqliteSearchService(context.Database.ConnectionFactory),
            markdownCompiler: uiCompiler, compiledMarkdownCache: cache);

        CompiledMarkdown uiMarkdown = (await uiCompiler.CompilePageMarkdownAsync(
            context.RevisionId, includeComplexTableHtml: true)).Value;
        McpPageTextResponse mcpMarkdown = (await api.GetPageTextAsync(
            new McpPageTextRequest(context.PageId))).Value;

        uiMarkdown.Markdown.Should().Be("source text");
        mcpMarkdown.Text.Should().Be("source text");
        context.Compiler.CallCount.Should().Be(1,
            "the PDF workspace compiler and MCP read API are supplied with the same host cache");
        cache.Metrics.Hits.Should().Be(1);

        DocumentTreeRevision committed = await BoxTreeTestData.CommitTextAsync(
            context.Database.ConnectionFactory, context.Clock, context.DocumentId, context.PageId, "updated text");
        McpPageTextResponse updated = (await api.GetPageTextAsync(
            new McpPageTextRequest(context.PageId))).Value;

        updated.TreeRevisionId.Should().Be(committed.TreeRevisionId);
        updated.Text.Should().Be("updated text");
        context.Compiler.CallCount.Should().Be(2,
            "a committed edit selects a new immutable revision cache key instead of reusing old markdown");
    }

    private sealed class Context : IAsyncDisposable
    {
        private Context(TemporarySqliteDatabase database, PageId pageId, DocumentInstanceId documentId,
            DocumentTreeRevisionId revisionId, FixedClock clock, CountingCompiler compiler, McpReadApi api)
        {
            Database = database;
            PageId = pageId;
            DocumentId = documentId;
            RevisionId = revisionId;
            Clock = clock;
            Compiler = compiler;
            Api = api;
        }

        public TemporarySqliteDatabase Database { get; }
        public PageId PageId { get; }
        public DocumentInstanceId DocumentId { get; }
        public DocumentTreeRevisionId RevisionId { get; }
        public FixedClock Clock { get; }
        public CountingCompiler Compiler { get; }
        public McpReadApi Api { get; }

        public static async Task<Context> CreateAsync(Task? compileGate = null, bool useRealMarkdownCompiler = false)
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
            IDocumentMarkdownCompiler? realCompiler = useRealMarkdownCompiler
                ? new DocumentMarkdownCompiler(BoxTreeTestData.CreateService(database.ConnectionFactory, clock),
                    new MarkdigMarkdownEngine())
                : null;
            CountingCompiler compiler = new(compileGate, realCompiler);
            McpReadApi api = new(database.ConnectionFactory, new SqliteSearchService(database.ConnectionFactory),
                markdownCompiler: compiler);
            return new Context(database, page.PageId, document.DocumentInstanceId, revision.TreeRevisionId, clock,
                compiler, api);
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }

    private sealed class CountingCompiler(Task? gate, IDocumentMarkdownCompiler? inner) : IDocumentMarkdownCompiler
    {
        private readonly Task? _gate = gate;
        private readonly IDocumentMarkdownCompiler? _inner = inner;
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

            if (_inner is not null)
            {
                return await _inner.CompilePageMarkdownAsync(treeRevisionId, includeSuppressed, cancellationToken,
                    includeComplexTableHtml);
            }

            string markdown = includeSuppressed ? "with suppressed" : "without suppressed";
            return Result<CompiledMarkdown>.Success(new CompiledMarkdown(markdown, [], []));
        }
    }
}
