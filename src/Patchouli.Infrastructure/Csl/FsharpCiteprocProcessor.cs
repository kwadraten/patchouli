using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using Fsharp.Citeproc;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Csl;

internal sealed class FsharpCiteprocProcessor
{
    private const long MaxStyleCharacters = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public Result<FsharpCiteprocRenderResponse> Render(
        FsharpCiteprocRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string styleXml = ApplyLocale(request.StyleXml, request.Locale);
            EngineCitationItem[] citationItems = request.Items
                .Select(ReadCitationItem)
                .ToArray();

            Result<IReadOnlyList<string>> textEntries = RenderEntries(
                styleXml,
                request.Items,
                citationItems,
                "plain-text",
                cancellationToken);
            if (textEntries.IsFailure)
            {
                return Result<FsharpCiteprocRenderResponse>.Failure(
                    textEntries.ErrorCode!, textEntries.ErrorMessage!);
            }

            Result<IReadOnlyList<string>> htmlEntries = RenderEntries(
                styleXml,
                request.Items,
                citationItems,
                "html",
                cancellationToken);
            if (htmlEntries.IsFailure)
            {
                return Result<FsharpCiteprocRenderResponse>.Failure(
                    htmlEntries.ErrorCode!, htmlEntries.ErrorMessage!);
            }

            string renderedText = string.Join(Environment.NewLine, textEntries.Value).Trim();
            string renderedHtml = RenderHtmlBibliography(htmlEntries.Value);
            return Result<FsharpCiteprocRenderResponse>.Success(new FsharpCiteprocRenderResponse(
                request.StyleId,
                request.Locale,
                renderedText,
                renderedHtml,
                Array.Empty<string>(),
                Array.Empty<string>()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.fsharp-citeproc"))
        {
            return Result<FsharpCiteprocRenderResponse>.Failure(
                "csl_render_failed",
                $"Fsharp.Citeproc rendering failed: {exception.Message}");
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
        string payload = JsonSerializer.Serialize(
            new EngineRenderRequest(styleXml, items, [citationItems], format),
            JsonOptions);
        FSharpResult<string, FSharpList<CiteprocError>> rendered = Fsharp.Citeproc.Citeproc.renderJson(payload);
        cancellationToken.ThrowIfCancellationRequested();

        if (rendered.IsError)
        {
            string message = string.Join("; ", rendered.ErrorValue.Select(FormatDiagnostic));
            return Result<IReadOnlyList<string>>.Failure(
                "csl_render_failed",
                string.IsNullOrWhiteSpace(message) ? "Fsharp.Citeproc rendering failed." : message);
        }

        EngineRenderResponse? response = JsonSerializer.Deserialize<EngineRenderResponse>(
            rendered.ResultValue,
            JsonOptions);
        if (response?.Bibliography is null || response.Bibliography.Count == 0)
        {
            return Result<IReadOnlyList<string>>.Failure(
                "csl_render_failed",
                "Fsharp.Citeproc returned no bibliography entries.");
        }

        return Result<IReadOnlyList<string>>.Success(response.Bibliography);
    }

    private static EngineCitationItem ReadCitationItem(IReadOnlyDictionary<string, object?> item)
    {
        if (!item.TryGetValue("id", out object? rawId) || string.IsNullOrWhiteSpace(rawId?.ToString()))
        {
            throw new InvalidDataException("A CSL item is missing its id.");
        }

        return new EngineCitationItem(rawId.ToString()!.Trim());
    }

    private static string ApplyLocale(string styleXml, string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return styleXml;
        }

        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            MaxCharactersInDocument = MaxStyleCharacters,
            XmlResolver = null
        };
        using StringReader source = new(styleXml);
        using XmlReader reader = XmlReader.Create(source, settings);
        XDocument document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        XElement root = document.Root ?? throw new InvalidDataException("The CSL style has no document root.");
        if (!string.Equals(root.Name.LocalName, "style", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The CSL document root must be <style>.");
        }

        root.SetAttributeValue("default-locale", locale.Trim());
        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static string FormatDiagnostic(Fsharp.Citeproc.CiteprocError diagnostic)
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

    private sealed record EngineCitationItem(
        [property: JsonPropertyName("id")] string Id);

    private sealed record EngineRenderResponse(
        [property: JsonPropertyName("bibliography")]
        IReadOnlyList<string> Bibliography);
}

internal sealed record FsharpCiteprocRenderRequest(
    string StyleId,
    string StyleXml,
    string? Locale,
    IReadOnlyList<Dictionary<string, object?>> Items);

internal sealed record FsharpCiteprocRenderResponse(
    string StyleId,
    string? Locale,
    string RenderedText,
    string RenderedHtml,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);
