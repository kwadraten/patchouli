using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Bibliography.Bibtex;
using Patchouli.Core.CitationInterop;
using Patchouli.Core.Csl;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Search;

namespace Patchouli.Host.CitationInterop;

/// <summary>
/// Domain composition for the citation interop surface (issue #14 §2): search,
/// citation-key resolution, pick validation, formatting and export, shared by
/// the CAYW and JSON-RPC adapters. Bound to one library through the
/// <see cref="HostServices"/> it was built from; a library switch builds a new
/// instance, so a pick can never leak across libraries.
/// </summary>
public sealed class CitationInteropService
{
    /// <summary>Hard cap on entries in one pick (picker and selected paths).</summary>
    public const int MaxPickEntries = 100;

    private readonly ILibraryItemQueryService _query;
    private readonly IItemService _items;
    private readonly ICslItemMapper _cslMapper;
    private readonly ICslRenderer _cslRenderer;
    private readonly IBiblatexImportService _biblatex;

    public CitationInteropService(
        ILibraryItemQueryService query,
        IItemService items,
        ICslItemMapper cslMapper,
        ICslRenderer cslRenderer,
        IBiblatexImportService biblatex)
    {
        _query = query;
        _items = items;
        _cslMapper = cslMapper;
        _cslRenderer = cslRenderer;
        _biblatex = biblatex;
    }

    /// <summary>
    /// TexPile-oriented search (issue #14 §7): database-level filtering and
    /// limit, deterministic order (the query service's created_at/item_id
    /// order), at most <see cref="CaywCompatibility.MaxSearchResults"/>
    /// results. An empty query returns the most recent items. Items of type
    /// <c>general</c> are excluded in the query itself — they are not citable
    /// and have no CSL projection — so the result cap counts returned
    /// results, never pre-filter rows (plan §7 forbids fetching the cap and
    /// filtering afterwards).
    /// A row whose CSL projection fails with a data error (for example an
    /// item with a corrupt stored date) is skipped: unlike export, search is
    /// not all-or-nothing, and one damaged item must not make the whole
    /// library unsearchable. Infrastructure failures are not skipped: they
    /// indicate a systemic problem, so the search fails as a whole instead
    /// of returning a deceptively partial success.
    /// </summary>
    public async Task<Result<IReadOnlyList<CitationItemProjection>>> SearchAsync(
        string? query, CancellationToken cancellationToken = default)
    {
        Result<IReadOnlyList<LibraryItemRow>> rows = await _query.SearchRowsAsync(
            new BibliographicItemSearch(
                string.IsNullOrWhiteSpace(query) ? null : query.Trim(),
                [],
                CaywCompatibility.MaxSearchResults,
                true),
            cancellationToken);
        if (rows.IsFailure)
        {
            return Result<IReadOnlyList<CitationItemProjection>>.Failure(rows.ErrorCode!, rows.ErrorMessage!);
        }

        List<CitationItemProjection> projections = new();
        foreach (LibraryItemRow row in rows.Value)
        {
            Result<CitationItemProjection> projection = await ProjectAsync(row.ItemId, cancellationToken);
            if (projection.IsFailure)
            {
                bool isDataFailure = projection.ErrorCode is
                    AppErrorCodes.ValidationFailed or AppErrorCodes.NotFound;
                if (!isDataFailure)
                {
                    return Result<IReadOnlyList<CitationItemProjection>>.Failure(
                        projection.ErrorCode!, projection.ErrorMessage!);
                }

                continue;
            }

            projections.Add(projection.Value);
        }

        return Result<IReadOnlyList<CitationItemProjection>>.Success(projections);
    }

    /// <summary>Loads one item and projects it to CSL variables.</summary>
    public async Task<Result<CitationItemProjection>> ProjectAsync(ItemId itemId,
        CancellationToken cancellationToken = default)
    {
        Result<ItemMetadata> item = await _items.GetItemAsync(itemId, cancellationToken);
        if (item.IsFailure)
        {
            return Result<CitationItemProjection>.Failure(item.ErrorCode!, item.ErrorMessage!);
        }

        Result<CslMappedItem> mapped = await _cslMapper.MapAsync(item.Value, cancellationToken);
        if (mapped.IsFailure)
        {
            return Result<CitationItemProjection>.Failure(mapped.ErrorCode!, mapped.ErrorMessage!);
        }

        return Result<CitationItemProjection>.Success(new CitationItemProjection(
            itemId, item.Value.CitationKey, mapped.Value.Variables));
    }

    /// <summary>
    /// Validates a confirmed pick or a selection snapshot against the current
    /// library (issue #14 §4): every item must still exist, be active (not
    /// trashed/merged) and be citable, and the citation keys are re-read from
    /// the library. Input order is preserved exactly.
    /// </summary>
    public async Task<Result<CitationPick>> ValidatePickAsync(
        IReadOnlyList<CitationBridgePickEntry> entries, CancellationToken cancellationToken = default)
    {
        if (entries.Count > MaxPickEntries)
        {
            return Result<CitationPick>.Failure(AppErrorCodes.ValidationFailed,
                $"A citation pick may contain at most {MaxPickEntries} entries.");
        }

        if (entries.Count == 0)
        {
            return Result<CitationPick>.Success(CitationPick.Empty);
        }

        ItemId[] ids = entries.Select(static entry => entry.ItemId).Distinct().ToArray();
        Result<IReadOnlyList<LibraryItemRow>> rows = await _query.GetRowsByIdsAsync(ids, cancellationToken);
        if (rows.IsFailure)
        {
            return Result<CitationPick>.Failure(rows.ErrorCode!, rows.ErrorMessage!);
        }

        Dictionary<ItemId, LibraryItemRow> rowById = rows.Value.ToDictionary(static row => row.ItemId);
        List<CitationPickEntry> validated = new(entries.Count);
        foreach (CitationBridgePickEntry entry in entries)
        {
            if (!rowById.TryGetValue(entry.ItemId, out LibraryItemRow? row))
            {
                return Result<CitationPick>.Failure(AppErrorCodes.NotFound,
                    $"Picked item {entry.ItemId} no longer exists or is in the trash.");
            }

            if (string.Equals(row.ItemType, "general", StringComparison.Ordinal))
            {
                return Result<CitationPick>.Failure(AppErrorCodes.NotCitable,
                    $"Picked item '{row.Title}' is a general item and cannot be cited.");
            }

            Result<ItemMetadata> item = await _items.GetItemAsync(entry.ItemId, cancellationToken);
            if (item.IsFailure)
            {
                return Result<CitationPick>.Failure(item.ErrorCode!, item.ErrorMessage!);
            }

            validated.Add(new CitationPickEntry(
                entry.ItemId,
                item.Value.CitationKey,
                NullIfWhiteSpace(entry.Locator),
                NullIfWhiteSpace(entry.Label),
                NullIfWhiteSpace(entry.Prefix),
                NullIfWhiteSpace(entry.Suffix),
                entry.SuppressAuthor));
        }

        return Result<CitationPick>.Success(new CitationPick(validated));
    }

    /// <summary>Formats a validated pick in one CAYW format.</summary>
    public async Task<Result<CitationFormattedOutput>> FormatPickAsync(
        CitationPick pick,
        string? format,
        CitationFormatOptions options,
        string? styleId = null,
        string? locale = null,
        bool preferHtml = false,
        CancellationToken cancellationToken = default)
    {
        Result<CitationFormatters.Resolution> resolution = CitationFormatters.Resolve(format);
        if (resolution.IsFailure)
        {
            return Result<CitationFormattedOutput>.Failure(resolution.ErrorCode!, resolution.ErrorMessage!);
        }

        if (pick.IsEmpty)
        {
            return Result<CitationFormattedOutput>.Success(
                new CitationFormattedOutput("", "text/plain; charset=utf-8"));
        }

        if (resolution.Value.IsFormattedCsl)
        {
            return await FormatWithCslAsync(pick, resolution.Value.CanonicalFormat, styleId, locale, preferHtml,
                cancellationToken);
        }

        IReadOnlyList<CitationItemProjection> projections = [];
        if (resolution.Value.CanonicalFormat == CaywCompatibility.FormatJson)
        {
            List<CitationItemProjection> built = new(pick.Entries.Count);
            foreach (CitationPickEntry entry in pick.Entries)
            {
                Result<CitationItemProjection> projection = await ProjectAsync(entry.ItemId, cancellationToken);
                if (projection.IsFailure)
                {
                    return Result<CitationFormattedOutput>.Failure(projection.ErrorCode!, projection.ErrorMessage!);
                }

                built.Add(projection.Value);
            }

            projections = built;
        }

        Result<string> formatted = resolution.Value.Formatter!.Format(
            new CitationFormatRequest(pick, projections, options));
        if (formatted.IsFailure)
        {
            return Result<CitationFormattedOutput>.Failure(formatted.ErrorCode!, formatted.ErrorMessage!);
        }

        string contentType = resolution.Value.CanonicalFormat == CaywCompatibility.FormatJson
            ? "application/json; charset=utf-8"
            : "text/plain; charset=utf-8";
        return Result<CitationFormattedOutput>.Success(new CitationFormattedOutput(formatted.Value, contentType));
    }

    private async Task<Result<CitationFormattedOutput>> FormatWithCslAsync(
        CitationPick pick, string canonicalFormat, string? styleId, string? locale, bool preferHtml,
        CancellationToken cancellationToken)
    {
        if (canonicalFormat == CaywCompatibility.FormatFormattedCitation)
        {
            CslClusterRenderRequest request = new(
                pick.Entries.Select(static entry => new CslCitationItem(
                    entry.ItemId, entry.Locator, entry.Label, entry.Prefix, entry.Suffix,
                    entry.SuppressAuthor)).ToArray(),
                styleId,
                locale,
                preferHtml ? CslRenderFormats.Html : CslRenderFormats.Text);
            Result<CslClusterRenderResult> rendered = await _cslRenderer.RenderClusterAsync(request, cancellationToken);
            if (rendered.IsFailure)
            {
                return Result<CitationFormattedOutput>.Failure(rendered.ErrorCode!, rendered.ErrorMessage!);
            }

            return Result<CitationFormattedOutput>.Success(preferHtml
                ? new CitationFormattedOutput(rendered.Value.CitationHtml, "text/html; charset=utf-8")
                : new CitationFormattedOutput(rendered.Value.CitationText, "text/plain; charset=utf-8"));
        }

        Result<CslRenderResult> bibliography = await _cslRenderer.RenderAsync(
            new CslRenderRequest(
                pick.Entries.Select(static entry => entry.ItemId).ToArray(),
                styleId,
                locale,
                preferHtml ? CslRenderFormats.Html : CslRenderFormats.Text),
            cancellationToken);
        if (bibliography.IsFailure)
        {
            return Result<CitationFormattedOutput>.Failure(bibliography.ErrorCode!, bibliography.ErrorMessage!);
        }

        return Result<CitationFormattedOutput>.Success(preferHtml
            ? new CitationFormattedOutput(bibliography.Value.RenderedHtml, "text/html; charset=utf-8")
            : new CitationFormattedOutput(bibliography.Value.RenderedText, "text/plain; charset=utf-8"));
    }

    /// <summary>
    /// TexPile <c>item.export</c> (issue #14 §7): batch export by citation key.
    /// All-or-nothing — a missing/unselectable key or an unsupported translator
    /// fails the whole batch; partial bib output is never returned as if it
    /// were complete. Duplicate keys are de-duplicated keeping first
    /// occurrence; output order follows the (de-duplicated) request order.
    /// </summary>
    public async Task<Result<string>> ExportAsync(
        IReadOnlyList<string> citationKeys, string? translator,
        CancellationToken cancellationToken = default)
    {
        string translatorName = translator?.Trim() ?? "";
        bool biblatex = string.Equals(translatorName, CaywCompatibility.TranslatorBetterBiblatex,
            StringComparison.Ordinal);
        bool bibtex = string.Equals(translatorName, CaywCompatibility.TranslatorBetterBibtex,
            StringComparison.Ordinal);
        if (!biblatex && !bibtex)
        {
            return Result<string>.Failure("cayw_translator_unsupported",
                $"Translator '{translatorName}' is not supported. Supported translators: '{CaywCompatibility.TranslatorBetterBiblatex}', '{CaywCompatibility.TranslatorBetterBibtex}'.");
        }

        List<string> keys = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string key in citationKeys)
        {
            string trimmed = key.Trim();
            if (trimmed.Length > 0 && seen.Add(trimmed))
            {
                keys.Add(trimmed);
            }
        }

        if (keys.Count == 0)
        {
            return Result<string>.Failure(AppErrorCodes.ValidationFailed, "No citation keys were supplied.");
        }

        if (keys.Count > CaywCompatibility.MaxExportKeys)
        {
            return Result<string>.Failure(AppErrorCodes.ValidationFailed,
                $"At most {CaywCompatibility.MaxExportKeys} citation keys may be exported in one request.");
        }

        Result<IReadOnlyList<LibraryItemRow>> rows =
            await _query.ResolveRowsByCitationKeysAsync(keys, cancellationToken);
        if (rows.IsFailure)
        {
            return Result<string>.Failure(rows.ErrorCode!, rows.ErrorMessage!);
        }

        if (rows.Value.Count != keys.Count)
        {
            // Name the missing keys exactly: resolve the surviving items'
            // citation keys (failure path only, so the extra reads are fine).
            HashSet<string> foundKeys = new(StringComparer.Ordinal);
            foreach (LibraryItemRow row in rows.Value)
            {
                Result<ItemMetadata> item = await _items.GetItemAsync(row.ItemId, cancellationToken);
                if (item.IsSuccess)
                {
                    foundKeys.Add(item.Value.CitationKey);
                }
            }

            string[] missing = keys.Where(key => !foundKeys.Contains(key)).ToArray();
            return Result<string>.Failure("cayw_key_not_found",
                $"Citation key(s) not found or not selectable (trashed, merged or missing): {string.Join(", ", missing)}. The whole export was aborted.");
        }

        foreach (LibraryItemRow row in rows.Value)
        {
            if (string.Equals(row.ItemType, "general", StringComparison.Ordinal))
            {
                return Result<string>.Failure(AppErrorCodes.NotCitable,
                    $"Item '{row.Title}' is a general item and cannot be exported. The whole export was aborted.");
            }
        }

        ItemId[] ids = rows.Value.Select(static row => row.ItemId).ToArray();
        if (biblatex)
        {
            return await _biblatex.ExportItemsAsync(ids, cancellationToken);
        }

        List<ItemMetadata> items = new(ids.Length);
        foreach (ItemId id in ids)
        {
            Result<ItemMetadata> item = await _items.GetItemAsync(id, cancellationToken);
            if (item.IsFailure)
            {
                return Result<string>.Failure(item.ErrorCode!, item.ErrorMessage!);
            }

            items.Add(item.Value);
        }

        Result<BibtexExportResult> mapped = BibtexExportMapper.MapItems(items);
        if (mapped.IsFailure)
        {
            return Result<string>.Failure(mapped.ErrorCode!, mapped.ErrorMessage!);
        }

        // Classic BibTeX is serialized in .NET (BibtexSerializer), not via the
        // biblatex-helper writer: the helper canonicalizes journal/address/
        // school to their BibLaTeX names, which classic BibTeX ignores.
        return Result<string>.Success(BibtexSerializer.Serialize(mapped.Value.Entries));
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

/// <summary>A formatted CAYW response body plus its content type.</summary>
public sealed record CitationFormattedOutput(string Body, string ContentType);
