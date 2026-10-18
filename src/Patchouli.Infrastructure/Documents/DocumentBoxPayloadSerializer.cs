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
            AlgorithmBoxPayload value => JsonSerializer.Serialize(value, Options),
            CodeBoxPayload value => JsonSerializer.Serialize(value, Options),
            MediaBoxPayload value => JsonSerializer.Serialize(value, Options),
            _ => throw new InvalidOperationException($"Unsupported document box payload: {payload.GetType().Name}")
        };
    }

    public static DocumentBoxPayload? Deserialize(string boxType, string? subType, string? json)
    {
        if (json is null)
        {
            return null;
        }

        return boxType switch
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
        };
    }
}
