using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using Fsharp.Citeproc;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Csl;

internal sealed class FsharpCiteprocProcessor
{
    private const long MaxStyleCharacters = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Mirrors Fsharp.Citeproc 1.1.0 preferredPrimaryDialects from
    // kwadraten/Fsharp.Citeproc commit a7f5a8620572bbe3e3d4b5271407174ec28a862d.
    private static readonly IReadOnlyDictionary<string, string> PreferredPrimaryDialects =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["af"] = "af-ZA", ["ar"] = "ar", ["bal"] = "bal-PK", ["bg"] = "bg-BG",
            ["brh"] = "brh-PK", ["ca"] = "ca-AD", ["cs"] = "cs-CZ", ["cy"] = "cy-GB",
            ["da"] = "da-DK", ["de"] = "de-DE", ["el"] = "el-GR", ["en"] = "en-US",
            ["es"] = "es-ES", ["et"] = "et-EE", ["eu"] = "eu", ["fa"] = "fa-IR",
            ["fi"] = "fi-FI", ["fr"] = "fr-FR", ["gl"] = "gl-ES", ["he"] = "he-IL",
            ["hi"] = "hi-IN", ["hr"] = "hr-HR", ["hu"] = "hu-HU", ["hy"] = "hy-AM",
            ["id"] = "id-ID", ["is"] = "is-IS", ["it"] = "it-IT", ["ja"] = "ja-JP",
            ["km"] = "km-KH", ["ko"] = "ko-KR", ["la"] = "la", ["lij"] = "lij-IT",
            ["lt"] = "lt-LT", ["lv"] = "lv-LV", ["mn"] = "mn-MN", ["ms"] = "ms-MY",
            ["nb"] = "nb-NO", ["nl"] = "nl-NL", ["nn"] = "nn-NO", ["pa"] = "pa-PK",
            ["pl"] = "pl-PL", ["pt"] = "pt-PT", ["ro"] = "ro-RO", ["ru"] = "ru-RU",
            ["sk"] = "sk-SK", ["sl"] = "sl-SI", ["sr"] = "sr-Cyrl-RS", ["sv"] = "sv-SE",
            ["th"] = "th-TH", ["tl"] = "tl-PH", ["tr"] = "tr-TR", ["uk"] = "uk-UA",
            ["vi"] = "vi-VN", ["zh"] = "zh-CN"
        };

    public Result<FsharpCiteprocRenderResponse> Render(
        FsharpCiteprocRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string styleXml, string? originalDefaultLocale) = ApplyLocale(request.StyleXml, request.Locale);
            (string StyleXml, IReadOnlyList<Dictionary<string, object?>> Items) localizedRender =
                ApplyLanguageLocaleMappings(styleXml, request.Items,
                    request.LanguageLocaleMappings, originalDefaultLocale);
            styleXml = localizedRender.StyleXml;
            IReadOnlyList<Dictionary<string, object?>> renderItems = localizedRender.Items;
            EngineCitationItem[] citationItems = request.Items.Select(ReadCitationItem).ToArray();

            Result<IReadOnlyList<string>> textEntries = RenderEntries(
                styleXml, renderItems, citationItems, "plain-text", cancellationToken);
            if (textEntries.IsFailure)
            {
                return Result<FsharpCiteprocRenderResponse>.Failure(textEntries.ErrorCode!, textEntries.ErrorMessage!);
            }

            Result<IReadOnlyList<string>> htmlEntries = RenderEntries(
                styleXml, renderItems, citationItems, "html", cancellationToken);
            if (htmlEntries.IsFailure)
            {
                return Result<FsharpCiteprocRenderResponse>.Failure(htmlEntries.ErrorCode!, htmlEntries.ErrorMessage!);
            }

            string renderedText = string.Join(Environment.NewLine, textEntries.Value).Trim();
            string renderedHtml = RenderHtmlBibliography(htmlEntries.Value);
            return Result<FsharpCiteprocRenderResponse>.Success(new FsharpCiteprocRenderResponse(
                request.StyleId, request.Locale, renderedText, renderedHtml,
                Array.Empty<string>(), Array.Empty<string>()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (XmlException exception)
        {
            return Result<FsharpCiteprocRenderResponse>.Failure("csl_render_failed",
                $"invalid-xml at line {exception.LineNumber}, position {exception.LinePosition}: {exception.Message}");
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.fsharp-citeproc"))
        {
            return Result<FsharpCiteprocRenderResponse>.Failure(
                "csl_render_failed", $"Fsharp.Citeproc rendering failed: {exception.Message}");
        }
    }

    private static Result<IReadOnlyList<string>> RenderEntries(
        string styleXml,
        IReadOnlyList<Dictionary<string, object?>> items,
        IReadOnlyList<EngineCitationItem> citationItems,
        string format,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string payload = JsonSerializer.Serialize(new EngineRenderRequest(styleXml, items, [citationItems], format),
            JsonOptions);
        FSharpResult<string, FSharpList<CiteprocError>> rendered = Citeproc.renderJson(payload);
        cancellationToken.ThrowIfCancellationRequested();
        if (rendered.IsError)
        {
            string message = string.Join("; ", rendered.ErrorValue.Select(FormatDiagnostic));
            return Result<IReadOnlyList<string>>.Failure("csl_render_failed",
                string.IsNullOrWhiteSpace(message) ? "Fsharp.Citeproc rendering failed." : message);
        }

        EngineRenderResponse? response = JsonSerializer.Deserialize<EngineRenderResponse>(rendered.ResultValue,
            JsonOptions);
        return response?.Bibliography is null || response.Bibliography.Count == 0
            ? Result<IReadOnlyList<string>>.Failure("csl_render_failed",
                "Fsharp.Citeproc returned no bibliography entries.")
            : Result<IReadOnlyList<string>>.Success(response.Bibliography);
    }

    private static EngineCitationItem ReadCitationItem(IReadOnlyDictionary<string, object?> item)
    {
        if (!item.TryGetValue("id", out object? rawId) || string.IsNullOrWhiteSpace(rawId?.ToString()))
        {
            throw new InvalidDataException("A CSL item is missing its id.");
        }

        return new EngineCitationItem(rawId.ToString()!.Trim());
    }

    private static (string StyleXml, string? OriginalDefaultLocale) ApplyLocale(string styleXml, string? locale)
    {
        XDocument document = LoadStyle(styleXml);
        XElement root = document.Root ?? throw new InvalidDataException("The CSL style has no document root.");
        if (!string.Equals(root.Name.LocalName, "style", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The CSL document root must be <style>.");
        }

        string? originalDefaultLocale = (string?)root.Attribute("default-locale");
        if (string.IsNullOrWhiteSpace(locale))
        {
            return (styleXml, originalDefaultLocale);
        }

        root.SetAttributeValue("default-locale", locale.Trim());
        return (document.ToString(SaveOptions.DisableFormatting), originalDefaultLocale);
    }

    private static (string StyleXml, IReadOnlyList<Dictionary<string, object?>> Items) ApplyLanguageLocaleMappings(
        string styleXml,
        IReadOnlyList<Dictionary<string, object?>> items,
        IReadOnlyDictionary<string, string> mappings,
        string? originalDefaultLocale)
    {
        XDocument document = LoadStyle(styleXml);
        XElement root = document.Root ?? throw new InvalidDataException("The CSL style has no document root.");
        XNamespace ns = root.Name.Namespace;
        XElement? bibliography = root.Element(ns + "bibliography");
        List<XElement> layouts = bibliography?.Elements(ns + "layout").ToList() ?? [];
        HashSet<string> explicitLayoutLocales = layouts
            .SelectMany(static layout => SplitLocales((string?)layout.Attribute("locale")))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool hasLocalizedLayouts = explicitLayoutLocales.Count > 0;
        List<Dictionary<string, object?>> renderItems =
            items.Select(static item => new Dictionary<string, object?>(item, StringComparer.Ordinal)).ToList();
        HashSet<string> outputLocales = new(StringComparer.OrdinalIgnoreCase);
        string privateLanguageVariable = GetPrivateLanguageVariable(root, renderItems);
        bool rewroteLanguage = false;
        string defaultLocale = SplitLocales((string?)root.Attribute("default-locale")).FirstOrDefault() ?? "en-US";
        string fallbackLocale = GetFallbackLocale(root, defaultLocale);
        bool needsDefaultLayout = false;

        foreach (Dictionary<string, object?> item in renderItems)
        {
            item.TryGetValue("language", out object? rawLanguage);
            string? sourceLanguage = ItemLanguage.Normalize(rawLanguage?.ToString());
            item[privateLanguageVariable] = sourceLanguage;
            string? primaryLanguage = ItemLanguage.GetPrimaryLanguage(sourceLanguage);
            if (sourceLanguage is not null && HasExplicitLocaleForLanguage(root, bibliography, sourceLanguage))
            {
                continue;
            }

            bool hasDefaultLayout = layouts.Any(static layout =>
                string.IsNullOrWhiteSpace((string?)layout.Attribute("locale")));
            string targetLocale;
            if (sourceLanguage is not null && hasDefaultLayout &&
                LocaleSelectorMatches(sourceLanguage, originalDefaultLocale ?? ""))
            {
                targetLocale = originalDefaultLocale!;
            }
            else if (primaryLanguage is not null &&
                     mappings.TryGetValue(primaryLanguage, out string? configuredLocale) &&
                     IsValidLocale(configuredLocale))
            {
                targetLocale = configuredLocale.Trim();
            }
            else
            {
                item["language"] = fallbackLocale;
                needsDefaultLayout = true;
                rewroteLanguage = true;
                continue;
            }

            bool usesDefaultLocaleFallback = layouts.Any(static layout =>
                                                 string.IsNullOrWhiteSpace((string?)layout.Attribute("locale"))) &&
                                             LocaleSelectorMatches(targetLocale, originalDefaultLocale ?? "");
            if (hasLocalizedLayouts && !HasExplicitLocaleForLanguage(root, bibliography, targetLocale) &&
                !usesDefaultLocaleFallback)
            {
                item["language"] = fallbackLocale;
                needsDefaultLayout = true;
                rewroteLanguage = true;
                continue;
            }

            item["language"] = targetLocale;
            outputLocales.Add(targetLocale);
            rewroteLanguage = true;
        }

        if (needsDefaultLayout)
        {
            XElement? defaultLayout = layouts.LastOrDefault(static layout =>
                string.IsNullOrWhiteSpace((string?)layout.Attribute("locale")));
            if (defaultLayout is not null)
            {
                // A declared private locale prevents title-language detection from selecting another branch.
                XElement fallbackLayout = new(defaultLayout);
                fallbackLayout.SetAttributeValue("locale", fallbackLocale);
                layouts[0].AddBeforeSelf(fallbackLayout);
                AddDefaultLocaleAlias(root, defaultLocale, fallbackLocale);
            }
        }

        if (rewroteLanguage)
        {
            foreach (XElement element in root.Descendants()
                         .Where(static element => element.Attribute("variable") is not null))
            {
                string[] names = ((string)element.Attribute("variable")!).Split((char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (names.Contains("language", StringComparer.Ordinal))
                {
                    element.SetAttributeValue("variable", string.Join(' ', names.Select(name =>
                        string.Equals(name, "language", StringComparison.Ordinal)
                            ? privateLanguageVariable
                            : name)));
                }
            }
        }

        if (bibliography is not null && layouts.Count == 1 && explicitLayoutLocales.Count == 0 &&
            outputLocales.Count > 0)
        {
            XElement fallbackLayout = layouts[0];
            foreach (string outputLocale in outputLocales.Order(StringComparer.OrdinalIgnoreCase))
            {
                XElement localizedLayout = new(fallbackLayout);
                localizedLayout.SetAttributeValue("locale", outputLocale);
                fallbackLayout.AddBeforeSelf(localizedLayout);
            }
        }
        else if (bibliography is not null && hasLocalizedLayouts)
        {
            XElement? defaultLocaleFallback = layouts.LastOrDefault(static layout =>
                string.IsNullOrWhiteSpace((string?)layout.Attribute("locale")));
            bool requestedLocaleOverridesStyleDefault = !LocaleSelectorMatches(
                (string?)root.Attribute("default-locale") ?? "", originalDefaultLocale ?? "");
            if (defaultLocaleFallback is not null && requestedLocaleOverridesStyleDefault)
            {
                foreach (string outputLocale in outputLocales.Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (HasLocale(explicitLayoutLocales, outputLocale) ||
                        !LocaleSelectorMatches(outputLocale, originalDefaultLocale ?? ""))
                    {
                        continue;
                    }

                    XElement localizedFallback = new(defaultLocaleFallback);
                    localizedFallback.SetAttributeValue("locale", outputLocale);
                    defaultLocaleFallback.AddBeforeSelf(localizedFallback);
                }
            }
        }

        return (document.ToString(SaveOptions.DisableFormatting), renderItems);
    }

    private static string GetFallbackLocale(XElement root, string defaultLocale)
    {
        HashSet<string> used = root.DescendantsAndSelf().Attributes()
            .Where(static attribute => attribute.Name.LocalName is "locale" or "lang")
            .SelectMany(static attribute => SplitLocales(attribute.Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string candidate = $"{defaultLocale}-x-pcl-fallback";
        for (int suffix = 1; used.Contains(candidate); suffix++)
        {
            candidate = $"{defaultLocale}-x-pcl-fallback-{suffix}";
        }

        return candidate;
    }

    private static void AddDefaultLocaleAlias(XElement root, string defaultLocale, string fallbackLocale)
    {
        XNamespace ns = root.Name.Namespace;
        XElement[] inlineLocales = root.Elements(ns + "locale").ToArray();
        string resourceName = $"Fsharp.Citeproc.locales.locales-{ResolveBestLocale(defaultLocale)}.xml";
        using (Stream? resource = typeof(Citeproc).Assembly.GetManifestResourceStream(resourceName))
        {
            if (resource is not null)
            {
                using StreamReader reader = new(resource);
                XElement? locale = LoadStyle(reader.ReadToEnd()).Root;
                if (locale is not null)
                {
                    XElement alias = new(locale);
                    alias.SetAttributeValue(XNamespace.Xml + "lang", fallbackLocale);
                    root.Add(alias);
                }
            }
        }

        // Preserve the engine's locale layering: official terms, global, language, then dialect overrides.
        IEnumerable<XElement> applicable = inlineLocales
            .Where(static locale => locale.Attribute(XNamespace.Xml + "lang") is null)
            .Concat(inlineLocales.Where(locale =>
                    !string.IsNullOrWhiteSpace((string?)locale.Attribute(XNamespace.Xml + "lang")) &&
                    LocaleSelectorMatches(defaultLocale, (string)locale.Attribute(XNamespace.Xml + "lang")!))
                .OrderBy(locale => ((string)locale.Attribute(XNamespace.Xml + "lang")!).Contains('-')));
        foreach (XElement locale in applicable)
        {
            XElement alias = new(locale);
            alias.SetAttributeValue(XNamespace.Xml + "lang", fallbackLocale);
            root.Add(alias);
        }

        foreach (XElement condition in root.Descendants().Where(static element =>
                     element.Name.LocalName is "if" or "else-if"))
        {
            string[] selectors = SplitLocales((string?)condition.Attribute("locale")).ToArray();
            if (selectors.Any(selector => LocaleSelectorMatches(defaultLocale, selector)))
            {
                condition.SetAttributeValue("locale", string.Join(' ', selectors.Append(fallbackLocale)));
            }
        }
    }

    private static XDocument LoadStyle(string styleXml)
    {
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            MaxCharactersInDocument = MaxStyleCharacters,
            XmlResolver = null
        };
        using StringReader source = new(styleXml);
        using XmlReader reader = XmlReader.Create(source, settings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static bool HasExplicitLocaleForLanguage(XElement root, XElement? bibliography, string sourceLanguage)
    {
        if (bibliography is null)
        {
            return false;
        }

        XNamespace ns = root.Name.Namespace;
        Dictionary<string, XElement> macros = root.Elements(ns + "macro")
            .Where(static macro => !string.IsNullOrWhiteSpace((string?)macro.Attribute("name")))
            .ToDictionary(static macro => (string)macro.Attribute("name")!, StringComparer.Ordinal);
        HashSet<string> visitedMacros = new(StringComparer.Ordinal);
        Queue<XElement> scopes = new();
        scopes.Enqueue(bibliography);
        while (scopes.TryDequeue(out XElement? scope))
        {
            bool hasMatchingLocale = scope.DescendantsAndSelf().Any(element =>
                element.Name.LocalName is "layout" or "if" or "else-if" &&
                SplitLocales((string?)element.Attribute("locale")).Any(locale =>
                    LocaleSelectorMatches(sourceLanguage, locale)));
            if (hasMatchingLocale)
            {
                return true;
            }

            foreach (string macroName in scope.DescendantsAndSelf()
                         .Select(static element => (string?)element.Attribute("macro"))
                         .Where(static name => !string.IsNullOrWhiteSpace(name))
                         .Select(static name => name!))
            {
                if (visitedMacros.Add(macroName) && macros.TryGetValue(macroName, out XElement? macro))
                {
                    scopes.Enqueue(macro);
                }
            }
        }

        return false;
    }

    private static IEnumerable<string> SplitLocales(string? locales)
    {
        return string.IsNullOrWhiteSpace(locales)
            ? []
            : locales.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool HasLocale(IEnumerable<string> locales, string targetLocale)
    {
        return locales.Any(locale => LocaleSelectorMatches(targetLocale, locale));
    }

    private static bool LocaleSelectorMatches(string sourceLanguage, string selector)
    {
        string? source = ItemLanguage.Normalize(sourceLanguage);
        string? selected = ItemLanguage.Normalize(selector);
        if (source is null || selected is null)
        {
            return false;
        }

        int selectedParts = selected.Count(static character => character == '-') + 1;
        if (selectedParts == 1)
        {
            return string.Equals(ItemLanguage.GetPrimaryLanguage(source), selected,
                StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(ResolveBestLocale(source), selected, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveBestLocale(string language)
    {
        string? normalized = ItemLanguage.Normalize(language);
        if (normalized is null || normalized.Contains('-'))
        {
            return normalized ?? language;
        }

        return PreferredPrimaryDialects.TryGetValue(normalized, out string? preferred)
            ? preferred
            : normalized;
    }

    private static bool IsValidLocale(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return false;
        }

        string[] parts = locale.Trim().Split('-', StringSplitOptions.None);
        return parts[0].Length is 2 or 3 && parts[0].All(char.IsAsciiLetter) &&
               parts.Skip(1).All(static part => part.Length is >= 2 and <= 8 && part.All(char.IsAsciiLetterOrDigit));
    }

    private static string GetPrivateLanguageVariable(XElement root,
        IReadOnlyList<Dictionary<string, object?>> items)
    {
        HashSet<string> keys = items.SelectMany(static item => item.Keys)
            .Concat(root.Descendants()
                .Select(static element => (string?)element.Attribute("variable"))
                .Where(static value => value is not null)
                .SelectMany(static value => value!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .ToHashSet(StringComparer.Ordinal);
        const string baseName = "patchouli-original-language";
        string candidate = baseName;
        for (int suffix = 2; keys.Contains(candidate); suffix++)
        {
            candidate = $"{baseName}-{suffix}";
        }

        return candidate;
    }

    private static string FormatDiagnostic(CiteprocError diagnostic)
    {
        string location = diagnostic.Path is null ? "" : $" at {diagnostic.Path.Value}";
        return $"{diagnostic.Code}{location}: {diagnostic.Message}";
    }

    private static string RenderHtmlBibliography(IEnumerable<string> entries)
    {
        StringBuilder html = new("<div class=\"csl-bib-body\">");
        foreach (string entry in entries)
        {
            html.Append("<div class=\"csl-entry\">");
            html.Append(entry);
            html.Append("</div>");
        }

        html.Append("</div>");
        return html.ToString();
    }

    private sealed record EngineRenderRequest(
        [property: JsonPropertyName("style")] string Style,
        [property: JsonPropertyName("items")] IReadOnlyList<Dictionary<string, object?>> Items,
        [property: JsonPropertyName("citations")]
        IReadOnlyList<IReadOnlyList<EngineCitationItem>> Citations,
        [property: JsonPropertyName("format")] string Format);

    private sealed record EngineCitationItem([property: JsonPropertyName("id")] string Id);

    private sealed record EngineRenderResponse(
        [property: JsonPropertyName("bibliography")]
        IReadOnlyList<string> Bibliography);
}

internal sealed record FsharpCiteprocRenderRequest(
    string StyleId,
    string StyleXml,
    string? Locale,
    IReadOnlyDictionary<string, string> LanguageLocaleMappings,
    IReadOnlyList<Dictionary<string, object?>> Items);

internal sealed record FsharpCiteprocRenderResponse(
    string StyleId,
    string? Locale,
    string RenderedText,
    string RenderedHtml,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);
