using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace Patchouli.Extensions.Contracts;

/// <summary>Resolves declaration defaults and validates a complete, immutable run configuration.</summary>
public static class ExtensionConfiguration
{
    public static ExtensionConfigSnapshot Resolve(IReadOnlyList<ConfigFieldDescriptor> fields,
        ExtensionConfigSnapshot stored)
    {
        Dictionary<string, string> resolved = new(stored.Values, StringComparer.Ordinal);
        foreach (ConfigFieldDescriptor field in fields)
        {
            if (!resolved.TryGetValue(field.Key, out string? value))
            {
                value = field.DefaultValue ?? "";
                resolved[field.Key] = value;
            }

            Validate(field, value);
        }

        return new ExtensionConfigSnapshot(new ReadOnlyDictionary<string, string>(resolved), stored.Revision);
    }

    private static void Validate(ConfigFieldDescriptor field, string? value)
    {
        if (value is null || (field.Required && string.IsNullOrWhiteSpace(value)))
        {
            throw Invalid(field, "A value is required.");
        }

        switch (field.Kind)
        {
            case ConfigValueKind.Integer:
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer))
                {
                    throw Invalid(field, "An integer is required.");
                }

                ValidateRange(field, integer);
                break;
            case ConfigValueKind.Decimal:
                if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal number))
                {
                    throw Invalid(field, "A decimal number is required.");
                }

                ValidateRange(field, number);
                break;
            case ConfigValueKind.Boolean when !bool.TryParse(value, out _):
                throw Invalid(field, "A Boolean value is required.");
            case ConfigValueKind.Choice when field.Choices is not null &&
                                             !field.Choices.Contains(value, StringComparer.Ordinal):
                throw Invalid(field, "Choose a declared value.");
            case ConfigValueKind.Model when !string.IsNullOrWhiteSpace(value):
                try
                {
                    using JsonDocument document = JsonDocument.Parse(value);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        throw Invalid(field, "A provider/model selection is required.");
                    }

                    foreach (JsonProperty property in document.RootElement.EnumerateObject())
                    {
                        if (property.Name.Equals("providerId", StringComparison.OrdinalIgnoreCase) ||
                            property.Name.Equals("model", StringComparison.OrdinalIgnoreCase))
                        {
                            if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                            {
                                throw Invalid(field, "Provider and model must be text.");
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    throw Invalid(field, "Invalid provider/model selection JSON.");
                }

                break;
        }
    }

    private static void ValidateRange(ConfigFieldDescriptor field, decimal value)
    {
        if ((field.Minimum is { } minimum && value < minimum) || (field.Maximum is { } maximum && value > maximum))
        {
            throw Invalid(field, "The value is outside its declared range.");
        }
    }

    private static ArgumentException Invalid(ConfigFieldDescriptor field, string message)
    {
        return new($"EXTCFG001: {field.Key}: {message}", field.Key);
    }
}
