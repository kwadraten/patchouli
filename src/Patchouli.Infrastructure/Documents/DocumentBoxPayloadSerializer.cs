using System.Text.Json;
using Patchouli.Core.Documents;

namespace Patchouli.Infrastructure.Documents;

internal static class DocumentBoxPayloadSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string? Serialize(DocumentBoxPayload? payload)
    {
        DocumentBoxPayload? normalized = Normalize(payload);
        return normalized switch
        {
            null => null,
            TextBoxPayload value => JsonSerializer.Serialize(value, Options),
            EquationBoxPayload value => JsonSerializer.Serialize(value, Options),
            ListBoxPayload value => JsonSerializer.Serialize(value, Options),
            TableBoxPayload value => JsonSerializer.Serialize(value, Options),
            AlgorithmBoxPayload value => JsonSerializer.Serialize(value, Options),
            CodeBoxPayload value => JsonSerializer.Serialize(value, Options),
            MediaBoxPayload value => JsonSerializer.Serialize(value, Options),
            _ => throw new InvalidOperationException($"Unsupported document box payload: {normalized.GetType().Name}")
        };
    }

    public static DocumentBoxPayload? Deserialize(string boxType, string? subType, string? json)
    {
        if (json is null)
        {
            return null;
        }

        return Normalize(boxType switch
        {
            DocumentBoxType.Equation => JsonSerializer.Deserialize<EquationBoxPayload>(json, Options),
            DocumentBoxType.List or DocumentBoxType.Index => JsonSerializer.Deserialize<ListBoxPayload>(json, Options),
            DocumentBoxType.Table => JsonSerializer.Deserialize<TableBoxPayload>(json, Options),
            DocumentBoxType.Code when subType == DocumentBoxType.Algorithm => JsonSerializer
                .Deserialize<AlgorithmBoxPayload>(json, Options),
            DocumentBoxType.Code or DocumentBoxType.Algorithm =>
                JsonSerializer.Deserialize<CodeBoxPayload>(json, Options),
            DocumentBoxType.Image or DocumentBoxType.Chart =>
                JsonSerializer.Deserialize<MediaBoxPayload>(json, Options),
            _ when DocumentBoxType.IsKnown(boxType) => JsonSerializer.Deserialize<TextBoxPayload>(json, Options),
            _ => throw new InvalidOperationException($"Unsupported document box type: {boxType}")
        });
    }

    private static DocumentBoxPayload? Normalize(DocumentBoxPayload? payload)
    {
        if (payload is null)
        {
            return null;
        }

        DocumentBoxPayload normalized = payload switch
        {
            TextBoxPayload text => text with { Markdown = StorageText.NormalizeInvalidCodePoints(text.Markdown)! },
            EquationBoxPayload equation => equation with
            {
                Latex = StorageText.NormalizeInvalidCodePoints(equation.Latex)!
            },
            ListBoxPayload list => list with { Markdown = StorageText.NormalizeInvalidCodePoints(list.Markdown)! },
            TableBoxPayload table => table with
            {
                Markdown = StorageText.NormalizeInvalidCodePoints(table.Markdown)!,
                Html = StorageText.NormalizeInvalidCodePoints(table.Html),
                ContentFormat = StorageText.NormalizeInvalidCodePoints(table.ContentFormat)!
            },
            AlgorithmBoxPayload algorithm => algorithm with
            {
                Markdown = StorageText.NormalizeInvalidCodePoints(algorithm.Markdown)!
            },
            CodeBoxPayload code => code with { Code = StorageText.NormalizeInvalidCodePoints(code.Code)! },
            MediaBoxPayload media => media with
            {
                AssetId = StorageText.NormalizeInvalidCodePoints(media.AssetId),
                Description = StorageText.NormalizeInvalidCodePoints(media.Description)
            },
            _ => throw new InvalidOperationException($"Unsupported document box payload: {payload.GetType().Name}")
        };
        return normalized with
        {
            Annotations = payload.Annotations?.Select(annotation => new DocumentAnnotation(
                StorageText.NormalizeInvalidCodePoints(annotation.Type)!,
                StorageText.NormalizeInvalidCodePoints(annotation.Markdown)!)).ToArray()
        };
    }
}
