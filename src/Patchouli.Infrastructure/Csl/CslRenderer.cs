using System.Net;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Csl;
using Patchouli.Core.Results;
using Patchouli.Core.Ids;

namespace Patchouli.Infrastructure.Csl;

public sealed class CslRenderer : ICslRenderer
{
    private readonly IItemService _itemService;
    private readonly ICslStyleStore _styleStore;
    private readonly ICslItemMapper _itemMapper;

    public CslRenderer(IItemService itemService, ICslStyleStore styleStore, ICslItemMapper itemMapper)
    {
        _itemService = itemService;
        _styleStore = styleStore;
        _itemMapper = itemMapper;
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

        var locale = await ResolveLocaleAsync(request.Locale, style.Value, cancellationToken);
        if (locale.IsFailure)
        {
            return Result<CslRenderResult>.Failure(locale.ErrorCode!, locale.ErrorMessage!);
        }

        var textEntries = new List<string>();
        var htmlEntries = new List<string>();
        var warnings = new List<string>();
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

            var entry = RenderEntry(mapped.Value, style.Value.StyleId);
            if (string.IsNullOrWhiteSpace(entry.Text) || string.IsNullOrWhiteSpace(entry.Html))
            {
                return Result<CslRenderResult>.Failure("csl_render_failed", "CSL renderer produced an empty bibliography entry.");
            }

            textEntries.Add(entry.Text);
            htmlEntries.Add(entry.Html);
            warnings.AddRange(entry.Warnings);
        }

        return Result<CslRenderResult>.Success(new CslRenderResult(
            style.Value.StyleId,
            style.Value.DisplayName,
            locale.Value,
            request.ItemIds,
            string.Join(Environment.NewLine, textEntries),
            string.Join("<br/>", htmlEntries),
            warnings.Distinct(StringComparer.Ordinal).ToArray(),
            Array.Empty<string>()));
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

    private static RenderedEntry RenderEntry(CslMappedItem item, string styleId)
    {
        var warnings = new List<string>();
        var variables = item.Variables;
        var authors = ReadCreatorNames(variables, "author");
        if (authors.Count == 0)
        {
            authors = ReadCreatorNames(variables, "editor");
            if (authors.Count > 0)
            {
                warnings.Add("Item has no author; editor list was used for bibliography rendering.");
            }
        }

        var year = ReadYear(variables.TryGetValue("issued", out var issued) ? issued : null);
        if (string.IsNullOrWhiteSpace(year))
        {
            warnings.Add("Item has no issued year; bibliography rendering used 'n.d.'.");
            year = "n.d.";
        }

        var title = ReadString(variables, "title") ?? "(untitled)";
        var publication = ReadString(variables, "container-title");
        var publisher = ReadString(variables, "publisher");
        var place = ReadString(variables, "publisher-place");
        var pages = ReadString(variables, "page");
        var volume = ReadString(variables, "volume");
        var issue = ReadString(variables, "issue");

        var authorText = authors.Count == 0 ? "Unknown author" : string.Join(", ", authors);
        string text;
        string html;
        if (styleId.Contains("apa", StringComparison.OrdinalIgnoreCase))
        {
            text = $"{authorText} ({year}). {title}.";
            html = $"{Encode(authorText)} ({Encode(year)}). <i>{Encode(title)}</i>.";
        }
        else if (styleId.Contains("gb-t-7714", StringComparison.OrdinalIgnoreCase))
        {
            text = $"{authorText}. {title}. {place}{(string.IsNullOrWhiteSpace(place) || string.IsNullOrWhiteSpace(publisher) ? string.Empty : ": ")}{publisher}, {year}.";
            html = $"{Encode(authorText)}. <i>{Encode(title)}</i>. {Encode(place)}{(string.IsNullOrWhiteSpace(place) || string.IsNullOrWhiteSpace(publisher) ? string.Empty : ": ")}{Encode(publisher)}, {Encode(year)}.";
        }
        else
        {
            text = $"{authorText} ({year}) {title}.";
            html = $"{Encode(authorText)} ({Encode(year)}) <i>{Encode(title)}</i>.";
        }

        if (!string.IsNullOrWhiteSpace(publication))
        {
            text += $" {publication}.";
            html += $" {Encode(publication)}.";
        }

        if (!string.IsNullOrWhiteSpace(volume))
        {
            text += $" {volume}";
            html += $" {Encode(volume)}";
            if (!string.IsNullOrWhiteSpace(issue))
            {
                text += $"({issue})";
                html += $"({Encode(issue)})";
            }
            text += ".";
            html += ".";
        }

        if (!string.IsNullOrWhiteSpace(pages))
        {
            text += $" {pages}.";
            html += $" {Encode(pages)}.";
        }

        return new RenderedEntry(text.Trim(), html.Trim(), warnings);
    }

    private static List<string> ReadCreatorNames(IReadOnlyDictionary<string, object?> variables, string role)
    {
        if (!variables.TryGetValue(role, out var value) || value is not IEnumerable<object?> creators)
        {
            return new List<string>();
        }

        var names = new List<string>();
        foreach (var creator in creators)
        {
            if (creator is not IDictionary<string, object?> creatorMap)
            {
                continue;
            }

            var literal = creatorMap.TryGetValue("literal", out var literalValue) ? literalValue?.ToString() : null;
            var family = creatorMap.TryGetValue("family", out var familyValue) ? familyValue?.ToString() : null;
            var given = creatorMap.TryGetValue("given", out var givenValue) ? givenValue?.ToString() : null;
            var display = string.IsNullOrWhiteSpace(literal)
                ? string.Join(" ", new[] { given, family }.Where(part => !string.IsNullOrWhiteSpace(part)))
                : literal;
            if (!string.IsNullOrWhiteSpace(display))
            {
                names.Add(display.Trim());
            }
        }

        return names;
    }

    private static string? ReadYear(object? issued)
    {
        if (issued is not IDictionary<string, object?> dictionary)
        {
            return null;
        }

        if (dictionary.TryGetValue("literal", out var literal) && !string.IsNullOrWhiteSpace(literal?.ToString()))
        {
            return literal!.ToString();
        }

        if (dictionary.TryGetValue("date-parts", out var dateParts))
        {
            if (dateParts is IEnumerable<IEnumerable<int>> numericParts)
            {
                var year = numericParts.FirstOrDefault()?.FirstOrDefault().ToString();
                return string.IsNullOrWhiteSpace(year) ? null : year;
            }

            if (dateParts is IEnumerable<object?> parts)
            {
                var firstArray = parts
                    .Select(part => part as IEnumerable<object?>)
                    .FirstOrDefault(part => part is not null);
                var year = firstArray?.FirstOrDefault()?.ToString();
                return string.IsNullOrWhiteSpace(year) ? null : year;
            }
        }

        return null;
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> variables, string key)
        => variables.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value?.ToString())
            ? value!.ToString()
            : null;

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private sealed record RenderedEntry(string Text, string Html, IReadOnlyList<string> Warnings);
}
