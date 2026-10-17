using System.Xml;
using Patchouli.Core.Results;

namespace Patchouli.Core.Csl;

/// <summary>Style identity extracted from a local CSL file's XML.</summary>
public sealed record CslLocalStyleInfo(
    string StyleId,
    string Title,
    string? DefaultLocale,
    IReadOnlyList<string> Dependencies,
    string? IndependentParentId);

/// <summary>
/// Issue #11: pure extraction of style id, title and dependencies from local CSL XML.
/// No filesystem or network access. The XML is parsed with DTDs prohibited.
/// </summary>
public static class CslStyleXmlParser
{
    /// <summary>Parses and validates CSL XML, extracting the style identity.</summary>
    public static Result<CslLocalStyleInfo> Parse(string contentXml)
    {
        if (string.IsNullOrWhiteSpace(contentXml))
        {
            return Result<CslLocalStyleInfo>.Failure(AppErrorCodes.ValidationFailed, "CSL style content is empty.");
        }

        XmlDocument document = new();
        try
        {
            using XmlReader reader = XmlReader.Create(
                new StringReader(contentXml),
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 10_000_000
                });
            document.Load(reader);
        }
        catch (XmlException exception)
        {
            return Result<CslLocalStyleInfo>.Failure(AppErrorCodes.ValidationFailed,
                $"CSL XML is not well-formed: {exception.Message}");
        }

        XmlElement? root = document.DocumentElement;
        if (root is null || !string.Equals(root.LocalName, "style", StringComparison.Ordinal))
        {
            return Result<CslLocalStyleInfo>.Failure(AppErrorCodes.ValidationFailed,
                "CSL XML root element must be <style>.");
        }

        XmlNamespaceManager namespaces = new(document.NameTable);
        string? namespaceUri = root.NamespaceURI;
        if (!string.IsNullOrEmpty(namespaceUri))
        {
            namespaces.AddNamespace("csl", namespaceUri);
        }

        XmlNode? info = string.IsNullOrEmpty(namespaceUri)
            ? root.SelectSingleNode("info")
            : root.SelectSingleNode("csl:info", namespaces);
        if (info is null)
        {
            return Result<CslLocalStyleInfo>.Failure(AppErrorCodes.ValidationFailed,
                "CSL style is missing its <info> section.");
        }

        string? idUri = ChildText(info, "id", namespaces, namespaceUri);
        string? title = ChildText(info, "title", namespaces, namespaceUri);
        if (string.IsNullOrWhiteSpace(idUri))
        {
            return Result<CslLocalStyleInfo>.Failure(AppErrorCodes.ValidationFailed,
                "CSL style <info> is missing its <id>.");
        }

        string styleId = ShortId(idUri);
        Result<string> validatedId = ValidateStyleId(styleId);
        if (validatedId.IsFailure)
        {
            return Result<CslLocalStyleInfo>.Failure(validatedId.ErrorCode!, validatedId.ErrorMessage!);
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return Result<CslLocalStyleInfo>.Failure(AppErrorCodes.ValidationFailed,
                "CSL style <info> is missing its <title>.");
        }

        List<string> dependencies = [];
        string? independentParent = null;
        XmlNodeList links = string.IsNullOrEmpty(namespaceUri)
            ? info.SelectNodes("link")!
            : info.SelectNodes("csl:link", namespaces)!;
        foreach (XmlNode link in links)
        {
            string? rel = link.Attributes?["rel"]?.Value?.Trim();
            string? href = link.Attributes?["href"]?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            string dependencyId = ShortId(href);
            if (string.Equals(rel, "independent-parent", StringComparison.OrdinalIgnoreCase))
            {
                independentParent = dependencyId;
                dependencies.Add(dependencyId);
            }
            else if (string.Equals(rel, "template", StringComparison.OrdinalIgnoreCase) &&
                     !dependencies.Contains(dependencyId, StringComparer.Ordinal))
            {
                dependencies.Add(dependencyId);
            }
        }

        string? defaultLocale = root.GetAttribute("default-locale");
        return Result<CslLocalStyleInfo>.Success(new CslLocalStyleInfo(
            validatedId.Value,
            title.Trim(),
            string.IsNullOrWhiteSpace(defaultLocale) ? null : defaultLocale.Trim(),
            dependencies,
            independentParent));
    }

    private static string? ChildText(XmlNode parent, string localName, XmlNamespaceManager namespaces,
        string? namespaceUri)
    {
        XmlNode? node = string.IsNullOrEmpty(namespaceUri)
            ? parent.SelectSingleNode(localName)
            : parent.SelectSingleNode("csl:" + localName, namespaces);
        return node?.InnerText?.Trim();
    }

    /// <summary>Derives the portable short style id from a CSL id URI or href.</summary>
    public static string ShortId(string idUri)
    {
        string trimmed = idUri.Trim().TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        string last = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        int hash = last.IndexOf('#');
        return hash >= 0 ? last[..hash] : last;
    }

    private static Result<string> ValidateStyleId(string styleId)
    {
        string normalized = styleId.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            normalized.Contains('/') || normalized.Contains('\\'))
        {
            return Result<string>.Failure(AppErrorCodes.ValidationFailed,
                "The CSL style id is not a portable style identifier.");
        }

        return Result<string>.Success(normalized);
    }
}
