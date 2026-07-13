using System.Text.Json;
using Patchouli.Core.Documents;

namespace Patchouli.Infrastructure.Documents;

internal static class DocumentBoxPayloadSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string? Serialize(DocumentBoxPayload? payload)
    {
        return payload switch
        {
            null => null,
            TextBoxPayload value => JsonSerializer.Serialize(value, Options),
            EquationBoxPayload value => JsonSerializer.Serialize(value, Options),
            ListBoxPayload value => JsonSerializer.Serialize(value, Options),
            TableBoxPayload value => JsonSerializer.Serialize(value, Options),
            CodeBoxPayload value => JsonSerializer.Serialize(value, Options),
            MediaBoxPayload value => JsonSerializer.Serialize(value, Options),
            _ => throw new InvalidOperationException($"Unsupported document box payload: {payload.GetType().Name}")
        };
    }

    public static DocumentBoxPayload? Deserialize(string boxType, string? baseType, string? json)
    {
        if (json is null)
        {
            return null;
        }

        return EffectiveType(boxType, baseType) switch
        {
            DocumentBoxType.Equation => JsonSerializer.Deserialize<EquationBoxPayload>(json, Options),
            DocumentBoxType.List => JsonSerializer.Deserialize<ListBoxPayload>(json, Options),
            DocumentBoxType.Table => JsonSerializer.Deserialize<TableBoxPayload>(json, Options),
            DocumentBoxType.Code or DocumentBoxType.Algorithm =>
                JsonSerializer.Deserialize<CodeBoxPayload>(json, Options),
            DocumentBoxType.Image or DocumentBoxType.Chart =>
                JsonSerializer.Deserialize<MediaBoxPayload>(json, Options),
            _ => JsonSerializer.Deserialize<TextBoxPayload>(json, Options)
        };
    }

    private static string EffectiveType(string boxType, string? baseType)
    {
        if (DocumentBoxType.IsKnown(boxType))
        {
            return boxType;
        }

        return baseType switch
        {
            "image" => DocumentBoxType.Image,
            "table" => DocumentBoxType.Table,
            "code" => DocumentBoxType.Code,
            _ => DocumentBoxType.Text
        };
    }
}
