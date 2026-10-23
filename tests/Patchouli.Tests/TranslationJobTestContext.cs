using Patchouli.Core.Ids;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Tests;

/// <summary>
/// A synthetic host job for translation-extension tests. It hands out pages in order, records every
/// pull, context read and commit, and lets a test decide each commit result (including conflicts).
/// It is test infrastructure only: production runs use <c>HostTranslationJob</c>.
/// </summary>
internal sealed class TestTranslationJob : ITranslationJob
{
    private readonly IReadOnlyList<TranslationPageInput> _pages;
    private readonly object _gate = new();
    private int _next;

    public TestTranslationJob(IEnumerable<TranslationPageInput> pages)
        : this(CreateInfo(), pages)
    {
    }

    public TestTranslationJob(TranslationJobInfo info, IEnumerable<TranslationPageInput> pages)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(pages);
        Info = info;
        _pages = [.. pages];
    }

    public TranslationJobInfo Info { get; }

    public IProgress<ExtensionProgress>? Progress { get; set; }

    /// <summary>Pages handed to the extension, in pull order.</summary>
    public List<TranslationPageInput> Pulled { get; } = [];

    /// <summary>Results accepted by the job, in commit order.</summary>
    public List<TranslationPageResult> Committed { get; } = [];

    /// <summary>Every context read, in call order.</summary>
    public List<(int PageIndex, int NeighborRadius)> ContextReads { get; } = [];

    /// <summary>Optional per-page context returned by <see cref="ReadContextAsync"/>.</summary>
    public Dictionary<int, TranslationContextSnapshot> Contexts { get; } = [];

    /// <summary>Optional per-page translations returned by <see cref="GetCommittedPageTranslationAsync"/>.</summary>
    public Dictionary<int, TranslationContextTranslationSnapshot> CommittedTranslations { get; } = [];

    /// <summary>When set, decides every commit instead of the default success behavior.</summary>
    public Func<TranslationPageResult, TranslationPageCommit>? CommitHandler { get; set; }

    /// <summary>Stops handing out pages after this many pulls; null means every page.</summary>
    public int? PageLimit { get; set; }

    /// <summary>Progress events reported by the extension.</summary>
    public List<ExtensionProgress> ProgressEvents { get; } = [];

    public ValueTask<TranslationPageInput?> NextPageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_next >= _pages.Count || (PageLimit is { } limit && Pulled.Count >= limit))
            {
                return ValueTask.FromResult<TranslationPageInput?>(null);
            }

            TranslationPageInput page = _pages[_next++];
            Pulled.Add(page);
            return ValueTask.FromResult<TranslationPageInput?>(page);
        }
    }

    public ValueTask<TranslationContextSnapshot> ReadContextAsync(TranslationPageInput page, int neighborRadius,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ContextReads.Add((page.Page.PageIndex, neighborRadius));
        }

        TranslationContextSnapshot snapshot =
            Contexts.TryGetValue(page.Page.PageIndex, out TranslationContextSnapshot? value)
                ? value
                : new TranslationContextSnapshot([]);
        return ValueTask.FromResult(snapshot);
    }

    public ValueTask<TranslationContextTranslationSnapshot?> GetCommittedPageTranslationAsync(int pageIndex,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CommittedTranslations.TryGetValue(pageIndex,
            out TranslationContextTranslationSnapshot? translation)
            ? translation
            : null);
    }

    public ValueTask<TranslationPageCommit> CommitPageAsync(TranslationPageResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();
        TranslationPageCommit commit = CommitHandler is { } handler
            ? handler(result)
            : new TranslationPageCommit(TranslationPageCommitState.Committed,
                result.Outcomes.Count(outcome => outcome.Kind == ExtensionBoxOutcomeKind.Translated));
        if (commit.State is TranslationPageCommitState.Committed or TranslationPageCommitState.Partial)
        {
            lock (_gate)
            {
                Committed.Add(result);
            }
        }

        return ValueTask.FromResult(commit);
    }

    /// <summary>Reports extension progress and records it for assertions.</summary>
    public void Report(ExtensionProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        lock (_gate)
        {
            ProgressEvents.Add(progress);
        }

        Progress?.Report(progress);
    }

    public static TranslationJobInfo CreateInfo(
        TranslationExtensionOptions? options = null,
        ExtensionConfigSnapshot? config = null,
        string sourceLanguage = "auto",
        string targetLanguage = "en")
    {
        return new TranslationJobInfo(
            Guid.NewGuid().ToString("N"),
            DocumentInstanceId.New(),
            sourceLanguage,
            targetLanguage,
            options ?? new TranslationExtensionOptions(),
            config ?? new ExtensionConfigSnapshot(new Dictionary<string, string>(), 0));
    }

    public static TranslationPageInput CreatePage(int pageIndex, params ExtensionBox[] boxes)
    {
        return new TranslationPageInput(
            new ExtensionPageIdentity(DocumentInstanceId.New(), PageId.New(), DocumentTreeRevisionId.New(),
                pageIndex),
            boxes);
    }

    public static ExtensionBox CreateBox(string text, DocumentBoxId? boxId = null, string type = "text")
    {
        return new ExtensionBox(boxId ?? DocumentBoxId.New(), null, type, null, text, "{}");
    }
}
