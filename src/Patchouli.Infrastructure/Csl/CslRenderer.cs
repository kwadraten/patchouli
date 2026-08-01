using Patchouli.Core.Bibliography;
using Patchouli.Core.Csl;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Csl;

public sealed class CslRenderer : ICslRenderer
{
    private readonly IItemService _itemService;
    private readonly ICslStyleStore _styleStore;
    private readonly ICslItemMapper _itemMapper;
    private readonly FsharpCiteprocProcessor _citeproc;

    public CslRenderer(
        IItemService itemService,
        ICslStyleStore styleStore,
        ICslItemMapper itemMapper)
        : this(itemService, styleStore, itemMapper, new FsharpCiteprocProcessor())
    {
    }

    internal CslRenderer(
        IItemService itemService,
        ICslStyleStore styleStore,
        ICslItemMapper itemMapper,
        FsharpCiteprocProcessor citeproc)
    {
        _itemService = itemService;
        _styleStore = styleStore;
        _itemMapper = itemMapper;
        _citeproc = citeproc;
    }

    public async Task<Result<CslRenderResult>> RenderAsync(CslRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.ItemIds.Count == 0)
        {
            return Result<CslRenderResult>.Failure(AppErrorCodes.ValidationFailed,
                "At least one item id is required for CSL rendering.");
        }

        Result<ResolvedStyle> style = await ResolveStyleAsync(request.StyleId, cancellationToken);
        if (style.IsFailure)
        {
            return Result<CslRenderResult>.Failure(style.ErrorCode!, style.ErrorMessage!);
        }

        Result<string> styleContent = await _styleStore.GetStyleContentAsync(style.Value.Style.StyleId, cancellationToken);
        if (styleContent.IsFailure)
        {
            return Result<CslRenderResult>.Failure(styleContent.ErrorCode!, styleContent.ErrorMessage!);
        }

        Result<string?> locale = await ResolveLocaleAsync(request.Locale, style.Value.Style, cancellationToken);
        if (locale.IsFailure)
        {
            return Result<CslRenderResult>.Failure(locale.ErrorCode!, locale.ErrorMessage!);
        }

        List<string> warnings = style.Value.Warnings.ToList();
        List<Dictionary<string, object?>> mappedItems = new();
        foreach (ItemId itemId in request.ItemIds)
        {
            Result<ItemMetadata> item = await _itemService.GetItemAsync(itemId, cancellationToken);
            if (item.IsFailure)
            {
                return Result<CslRenderResult>.Failure(item.ErrorCode!, item.ErrorMessage!);
            }

            ItemMetadata itemForCsl = item.Value;
            if (request.AllowGeneralAsMisc &&
                string.Equals(item.Value.ItemType, "general", StringComparison.Ordinal))
            {
                itemForCsl = item.Value with { ItemType = "misc" };
                warnings.Add($"general_as_misc:{item.Value.ItemId}");
            }

            Result<CslMappedItem> mapped = await _itemMapper.MapAsync(itemForCsl, cancellationToken);
            if (mapped.IsFailure)
            {
                return Result<CslRenderResult>.Failure(mapped.ErrorCode!, mapped.ErrorMessage!);
            }

            mappedItems.Add(FsharpCiteprocJsonAdapter.ToItem(mapped.Value, warnings));
        }

        Result<FsharpCiteprocRenderResponse> rendered = _citeproc.Render(
            new FsharpCiteprocRenderRequest(style.Value.Style.StyleId, styleContent.Value, locale.Value, mappedItems),
            cancellationToken);
        if (rendered.IsFailure)
        {
            return Result<CslRenderResult>.Failure(rendered.ErrorCode!, rendered.ErrorMessage!);
        }

        if (string.IsNullOrWhiteSpace(rendered.Value.RenderedText) ||
            string.IsNullOrWhiteSpace(rendered.Value.RenderedHtml))
        {
            return Result<CslRenderResult>.Failure("csl_render_failed", "CSL renderer produced an empty bibliography.");
        }

        warnings.AddRange(rendered.Value.Warnings);
        return Result<CslRenderResult>.Success(new CslRenderResult(
            style.Value.Style.StyleId,
            style.Value.Style.DisplayName,
            rendered.Value.Locale,
            request.ItemIds,
            rendered.Value.RenderedText,
            rendered.Value.RenderedHtml,
            warnings.Distinct(StringComparer.Ordinal).ToArray(),
            rendered.Value.Errors));
    }

    private async Task<Result<ResolvedStyle>> ResolveStyleAsync(string? requestedStyleId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requestedStyleId))
        {
            Result<CslStyle> requested = await _styleStore.GetStyleAsync(requestedStyleId, cancellationToken);
            return requested.IsFailure
                ? Result<ResolvedStyle>.Failure(requested.ErrorCode!, requested.ErrorMessage!)
                : Result<ResolvedStyle>.Success(new ResolvedStyle(requested.Value, []));
        }

        Result<CslSettings> settings = await _styleStore.GetSettingsAsync(cancellationToken);
        if (settings.IsFailure)
        {
            return Result<ResolvedStyle>.Failure(settings.ErrorCode!, settings.ErrorMessage!);
        }

        if (!string.IsNullOrWhiteSpace(settings.Value.DefaultStyleId))
        {
            Result<CslStyle> configured = await _styleStore.GetStyleAsync(settings.Value.DefaultStyleId,
                cancellationToken);
            if (configured.IsSuccess && configured.Value.Enabled)
            {
                return Result<ResolvedStyle>.Success(new ResolvedStyle(configured.Value, []));
            }
        }

        Result<IReadOnlyList<CslStyle>> styles = await _styleStore.ListInstalledStylesAsync(cancellationToken);
        if (styles.IsFailure)
        {
            return Result<ResolvedStyle>.Failure(styles.ErrorCode!, styles.ErrorMessage!);
        }

        CslStyle? firstEnabled = styles.Value.FirstOrDefault(style => style.Enabled);
        return firstEnabled is null
            ? Result<ResolvedStyle>.Failure(AppErrorCodes.NotFound, "No installed CSL style is available.")
            : Result<ResolvedStyle>.Success(new ResolvedStyle(firstEnabled,
                ["configured CSL style unavailable; using the first enabled fallback style."]));
    }

    private async Task<Result<string?>> ResolveLocaleAsync(string? requestedLocale, CslStyle style,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requestedLocale))
        {
            return Result<string?>.Success(requestedLocale.Trim());
        }

        Result<CslSettings> settings = await _styleStore.GetSettingsAsync(cancellationToken);
        if (settings.IsFailure)
        {
            return Result<string?>.Failure(settings.ErrorCode!, settings.ErrorMessage!);
        }

        if (!string.IsNullOrWhiteSpace(settings.Value.Locale))
        {
            return Result<string?>.Success(settings.Value.Locale.Trim());
        }

        return Result<string?>.Success(style.DefaultLocale);
    }

    private sealed record ResolvedStyle(CslStyle Style, IReadOnlyList<string> Warnings);
}
