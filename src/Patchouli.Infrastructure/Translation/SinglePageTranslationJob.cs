using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Infrastructure.Translation;

/// <summary>
/// An in-memory <see cref="ITranslationJob"/> over exactly one synthetic text box. It backs the settings
/// dialog's sample translation and draft contract test, which run a real extension through the real task-level
/// entry point without a document, a database or a page commit.
/// </summary>
/// <remarks>
/// The page is handed out once. Nothing is persisted: <see cref="CommitPageAsync"/> validates that the result
/// describes the sample page and records the accepted page result, which is what the caller inspects. A second
/// commit is therefore invalid, exactly as in a real run.
/// </remarks>
public sealed class SinglePageTranslationJob : ITranslationJob

{
    private readonly TranslationPageInput _page;
    private readonly IProgress<ExtensionProgress>? _progress;
    private readonly object _sync = new();
    private bool _pageTaken;
    private TranslationPageResult? _committed;

    /// <summary>Creates the job over one synthetic text box.</summary>
    /// <param name="documentId">The synthetic document instance.</param>
    /// <param name="pageId">The synthetic page.</param>
    /// <param name="revisionId">The synthetic page revision.</param>
    /// <param name="boxId">The box identity the extension must answer for.</param>
    /// <param name="text">The sample source text.</param>
    /// <param name="sourceLanguage">The sample source language, or <c>auto</c>.</param>
    /// <param name="targetLanguage">The sample target language.</param>
    /// <param name="options">Run options; defaults describe one small batch.</param>
    /// <param name="config">The run-pinned extension configuration snapshot.</param>
    /// <param name="progress">Optional progress sink.</param>
    public SinglePageTranslationJob(
        DocumentInstanceId documentId,
        PageId pageId,
        DocumentTreeRevisionId revisionId,
        DocumentBoxId boxId,
        string text,
        string sourceLanguage,
        string targetLanguage,
        TranslationExtensionOptions? options,
        ExtensionConfigSnapshot config,
        IProgress<ExtensionProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(config);
        BoxId = boxId;
        _progress = progress;
        _page = new TranslationPageInput(
            new ExtensionPageIdentity(documentId, pageId, revisionId, 1),
            Array.AsReadOnly<ExtensionBox>([new ExtensionBox(boxId, null, "text", null, text, "{}")]));
        LanguageTag sourceTag = LanguageTag.Create(sourceLanguage);
        Info = new TranslationJobInfo(
            Guid.NewGuid().ToString("N"),
            documentId,
            sourceTag.IsUnspecified ? LanguageTag.Auto : sourceTag,
            LanguageTag.Create(targetLanguage),
            options ?? new TranslationExtensionOptions(),
            config);
    }

    /// <inheritdoc />
    public TranslationJobInfo Info { get; }

    /// <summary>Progress events are observation only; a sample run has no step reporter to drive.</summary>
    public IProgress<ExtensionProgress>? Progress => _progress;

    /// <summary>The box identity every accepted page result must answer for.</summary>
    public DocumentBoxId BoxId { get; }

    /// <summary>The page handed to the extension, or null before it was taken.</summary>
    public TranslationPageInput? Page => _page;

    /// <summary>The accepted page result, or null until the extension commits.</summary>
    public TranslationPageResult? Committed
    {
        get
        {
            lock (_sync)
            {
                return _committed;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<TranslationPageInput?> NextPageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_pageTaken)
            {
                return ValueTask.FromResult<TranslationPageInput?>(null);
            }

            _pageTaken = true;
            return ValueTask.FromResult<TranslationPageInput?>(_page);
        }
    }

    /// <summary>A sample page has no neighbouring pages and no stored or previous translation.</summary>
    public ValueTask<TranslationContextSnapshot> ReadContextAsync(TranslationPageInput page, int neighborRadius,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new TranslationContextSnapshot([]));
    }

    /// <summary>A sample page has no stored translation, so sequential policies see nothing behind it.</summary>
    public ValueTask<TranslationContextTranslationSnapshot?> GetCommittedPageTranslationAsync(int pageIndex,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<TranslationContextTranslationSnapshot?>(null);
    }

    /// <inheritdoc />
    public ValueTask<TranslationPageCommit> CommitPageAsync(TranslationPageResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_pageTaken || _committed is not null)
            {
                return ValueTask.FromResult(new TranslationPageCommit(TranslationPageCommitState.Failed,
                    ErrorCode: AppErrorCodes.InvalidArgument,
                    ErrorMessage: "The sample page has not been taken or has already been committed."));
            }

            if (result.Page != _page.Page)
            {
                return ValueTask.FromResult(new TranslationPageCommit(TranslationPageCommitState.Failed,
                    ErrorCode: AppErrorCodes.InvalidArgument,
                    ErrorMessage: "The sample result belongs to a different page identity than the one issued."));
            }

            _committed = result;
            int translated =
                result.Outcomes?.Count(static outcome => outcome.Kind == ExtensionBoxOutcomeKind.Translated) ?? 0;
            return ValueTask.FromResult(new TranslationPageCommit(TranslationPageCommitState.Committed, translated));
        }
    }
}
