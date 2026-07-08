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
    private readonly HayagrivaCli _hayagriva;

    public CslRenderer(
        IItemService itemService,
        ICslStyleStore styleStore,
        ICslItemMapper itemMapper)
        : this(itemService, styleStore, itemMapper, new HayagrivaCli())
    {
    }

    internal CslRenderer(
        IItemService itemService,
        ICslStyleStore styleStore,
        ICslItemMapper itemMapper,
        HayagrivaCli hayagriva)
    {
        _itemService = itemService;
        _styleStore = styleStore;
        _itemMapper = itemMapper;
        _hayagriva = hayagriva;
    }

    public async Task<Result<CslRenderResult>> RenderAsync(CslRenderRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ItemIds.Count == 0)
        {
            return Result<CslRenderResult>.Failure(AppErrorCodes.ValidationFailed, "At least one item id is required for CSL rendering.");
        }

        var style = await ResolveStyleAsync(request.StyleId, cancellationToken);
        if (style.IsFailure)
        {
            return Result<CslRenderResult>.Failure(style.ErrorCode!, style.ErrorMessage!);
        }

        var styleContent = await _styleStore.GetStyleContentAsync(style.Value.StyleId, cancellationToken);
        if (styleContent.IsFailure)
        {
            return Result<CslRenderResult>.Failure(styleContent.ErrorCode!, styleContent.ErrorMessage!);
        }

        var locale = await ResolveLocaleAsync(request.Locale, style.Value, cancellationToken);
        if (locale.IsFailure)
        {
            return Result<CslRenderResult>.Failure(locale.ErrorCode!, locale.ErrorMessage!);
        }

        var warnings = new List<string>();
        var mappedItems = new List<Dictionary<string, object?>>();
        foreach (var itemId in request.ItemIds)
        {
            var item = await _itemService.GetItemAsync(itemId, cancellationToken);
            if (item.IsFailure)
            {
                return Result<CslRenderResult>.Failure(item.ErrorCode!, item.ErrorMessage!);
            }

            var mapped = await _itemMapper.MapAsync(item.Value, cancellationToken);
            if (mapped.IsFailure)
            {
                return Result<CslRenderResult>.Failure(mapped.ErrorCode!, mapped.ErrorMessage!);
            }

            mappedItems.Add(HayagrivaCslJsonAdapter.ToItem(mapped.Value, warnings));
        }

        var rendered = await _hayagriva.RenderAsync(
            new HayagrivaRenderRequest(style.Value.StyleId, styleContent.Value, locale.Value, mappedItems),
            cancellationToken);
        if (rendered.IsFailure)
        {
            return Result<CslRenderResult>.Failure(rendered.ErrorCode!, rendered.ErrorMessage!);
        }

        if (string.IsNullOrWhiteSpace(rendered.Value.RenderedText) || string.IsNullOrWhiteSpace(rendered.Value.RenderedHtml))
        {
            return Result<CslRenderResult>.Failure("csl_render_failed", "CSL renderer produced an empty bibliography.");
        }

        warnings.AddRange(rendered.Value.Warnings);
        return Result<CslRenderResult>.Success(new CslRenderResult(
            style.Value.StyleId,
            style.Value.DisplayName,
            rendered.Value.Locale,
            request.ItemIds,
            rendered.Value.RenderedText,
            rendered.Value.RenderedHtml,
            warnings.Distinct(StringComparer.Ordinal).ToArray(),
            rendered.Value.Errors));
    }

    private async Task<Result<CslStyle>> ResolveStyleAsync(string? requestedStyleId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requestedStyleId))
        {
            return await _styleStore.GetStyleAsync(requestedStyleId, cancellationToken);
        }

        var settings = await _styleStore.GetSettingsAsync(cancellationToken);
        if (settings.IsFailure)
        {
            return Result<CslStyle>.Failure(settings.ErrorCode!, settings.ErrorMessage!);
        }

        if (!string.IsNullOrWhiteSpace(settings.Value.DefaultStyleId))
        {
            return await _styleStore.GetStyleAsync(settings.Value.DefaultStyleId, cancellationToken);
        }

        var styles = await _styleStore.ListInstalledStylesAsync(cancellationToken);
        if (styles.IsFailure)
        {
            return Result<CslStyle>.Failure(styles.ErrorCode!, styles.ErrorMessage!);
        }

        var firstEnabled = styles.Value.FirstOrDefault(style => style.Enabled);
        return firstEnabled is null
            ? Result<CslStyle>.Failure(AppErrorCodes.NotFound, "No installed CSL style is available.")
            : Result<CslStyle>.Success(firstEnabled);
    }

    private async Task<Result<string?>> ResolveLocaleAsync(string? requestedLocale, CslStyle style, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requestedLocale))
        {
            return Result<string?>.Success(requestedLocale.Trim());
        }

        var settings = await _styleStore.GetSettingsAsync(cancellationToken);
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

}
