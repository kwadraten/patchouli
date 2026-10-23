using System.Collections.Concurrent;
using System.Text.Json;
using Patchouli.Core.Results;

namespace Patchouli.Core.Actions;

public sealed record ActionDescriptor(
    string Endpoint,
    string Id,
    IReadOnlyList<string> TargetTypes,
    JsonElement InputSchema,
    JsonElement ResultSchema,
    string Mode,
    IReadOnlyList<string> SideEffects,
    IReadOnlyList<string> RequiredCapabilities);

public sealed record ActionInvocation(
    string Target,
    string TargetType,
    JsonElement Args,
    string? RequestId = null);

public sealed record ActionOutcome(
    string State,
    object? Result = null,
    string? RunUri = null);

public sealed class ActionRegistry
{
    private readonly ConcurrentDictionary<(string Endpoint, string Action), (ActionDescriptor Descriptor,
        Func<ActionInvocation, CancellationToken, Task<Result<ActionOutcome>>> Handler)> _actions =
        new();

    public void Register(
        ActionDescriptor descriptor,
        Func<ActionInvocation, CancellationToken, Task<Result<ActionOutcome>>> handler)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(handler);

        if (string.IsNullOrWhiteSpace(descriptor.Id))
        {
            throw new ArgumentException("Action ID is required.", nameof(descriptor));
        }

        if (!_actions.TryAdd((descriptor.Endpoint, descriptor.Id), (descriptor, handler)))
        {
            throw new InvalidOperationException($"An action with ID '{descriptor.Id}' is already registered.");
        }
    }

    public IReadOnlyList<ActionDescriptor> Describe()
    {
        return _actions.Values.Select(static entry => entry.Descriptor).ToArray();
    }

    public ActionDescriptor? Find(string endpoint, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return _actions.TryGetValue((endpoint, id),
            out (ActionDescriptor Descriptor, Func<ActionInvocation, CancellationToken, Task<Result<ActionOutcome>>>
            Handler) entry)
            ? entry.Descriptor
            : null;
    }

    public async Task<Result<ActionOutcome>> InvokeAsync(
        string endpoint,
        string id,
        ActionInvocation invocation,
        Func<ActionDescriptor, ActionInvocation, bool> authorize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(authorize);

        if (string.IsNullOrWhiteSpace(id) || !_actions.TryGetValue((endpoint, id),
                out (ActionDescriptor Descriptor, Func<ActionInvocation, CancellationToken, Task<Result<ActionOutcome>>>
                Handler) entry))
        {
            return Result<ActionOutcome>.Failure(
                AppErrorCodes.InvalidArgument,
                $"Unknown action ID '{id}'.");
        }

        ActionDescriptor descriptor = entry.Descriptor;

        if (!descriptor.TargetTypes.Contains(invocation.TargetType, StringComparer.Ordinal))
        {
            return Result<ActionOutcome>.Failure(
                AppErrorCodes.InvalidArgument,
                $"Action '{descriptor.Id}' does not support target type '{invocation.TargetType}'.");
        }

        string? schemaError = ValidateArgs(descriptor.InputSchema, invocation.Args);
        if (schemaError is not null)
        {
            return Result<ActionOutcome>.Failure(AppErrorCodes.InvalidArgument, schemaError);
        }

        if (!authorize(descriptor, invocation))
        {
            return Result<ActionOutcome>.Failure(
                "permission_denied",
                $"Authorization denied for action '{descriptor.Id}'.");
        }

        return await entry.Handler(invocation, cancellationToken).ConfigureAwait(false);
    }

    public static string? ValidateArgs(JsonElement schema, JsonElement args)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return "Input schema must be a JSON object.";
        }

        if (!schema.TryGetProperty("type", out JsonElement typeElement)
            || typeElement.ValueKind != JsonValueKind.String
            || !string.Equals(typeElement.GetString(), "object", StringComparison.Ordinal))
        {
            return "Input schema root type must be 'object'.";
        }

        return ValidateObject(schema, args, "$");
    }

    private static string? ValidateObject(JsonElement schema, JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return $"Argument at '{path}' must be a JSON object.";
        }

        if (HasDuplicatePropertyNames(value))
        {
            return $"Argument at '{path}' contains duplicate property names.";
        }

        if (schema.TryGetProperty("required", out JsonElement required)
            && required.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement name in required.EnumerateArray())
            {
                if (name.ValueKind == JsonValueKind.String
                    && !value.TryGetProperty(name.GetString()!, out _))
                {
                    return $"Argument at '{path}' is missing required property '{name.GetString()}'.";
                }
            }
        }

        if (schema.TryGetProperty("additionalProperties", out JsonElement additionalProperties)
            && additionalProperties.ValueKind == JsonValueKind.False
            && schema.TryGetProperty("properties", out JsonElement declared)
            && declared.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!declared.TryGetProperty(property.Name, out _))
                {
                    return $"Argument at '{path}' has undeclared property '{property.Name}'.";
                }
            }
        }

        if (schema.TryGetProperty("properties", out JsonElement properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                string? error = ValidateAgainstSchema(property.Value, value, property.Name, $"{path}.{property.Name}");
                if (error is not null)
                {
                    return error;
                }
            }
        }

        return null;
    }

    private static string? ValidateAgainstSchema(
        JsonElement propertySchema,
        JsonElement container,
        string propertyName,
        string path)
    {
        if (container.ValueKind != JsonValueKind.Object ||
            !container.TryGetProperty(propertyName, out JsonElement propertyValue))
        {
            return null;
        }

        if (propertySchema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!propertySchema.TryGetProperty("type", out JsonElement typeElement)
            || typeElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return typeElement.GetString() switch
        {
            "string" => ValidateString(propertySchema, propertyValue, path),
            "integer" => ValidateInteger(propertyValue, path),
            "number" => ValidateNumber(propertyValue, path),
            "boolean" => ValidateBoolean(propertyValue, path),
            "array" => ValidateArray(propertySchema, propertyValue, path),
            "object" => ValidateObject(propertySchema, propertyValue, path),
            _ => $"Unsupported schema type '{typeElement.GetString()}' at '{path}'."
        };
    }

    private static string? ValidateString(JsonElement schema, JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return $"Argument at '{path}' must be a string.";
        }

        if (schema.TryGetProperty("minLength", out JsonElement minLength)
            && minLength.ValueKind == JsonValueKind.Number
            && value.GetString() is { } text
            && text.Length < minLength.GetInt32())
        {
            return $"Argument at '{path}' must be at least {minLength.GetInt32()} characters long.";
        }

        return null;
    }

    private static string? ValidateInteger(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out _))
        {
            return $"Argument at '{path}' must be an integer.";
        }

        return null;
    }

    private static string? ValidateNumber(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Number)
        {
            return $"Argument at '{path}' must be a number.";
        }

        return null;
    }

    private static string? ValidateBoolean(JsonElement value, string path)
    {
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return $"Argument at '{path}' must be a boolean.";
        }

        return null;
    }

    private static string? ValidateArray(JsonElement schema, JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return $"Argument at '{path}' must be an array.";
        }

        if (schema.TryGetProperty("minItems", out JsonElement minItems)
            && minItems.ValueKind == JsonValueKind.Number
            && value.GetArrayLength() < minItems.GetInt32())
        {
            return $"Argument at '{path}' must contain at least {minItems.GetInt32()} items.";
        }

        if (schema.TryGetProperty("items", out JsonElement items)
            && items.ValueKind == JsonValueKind.Object
            && items.TryGetProperty("type", out JsonElement itemType)
            && itemType.ValueKind == JsonValueKind.String)
        {
            int index = 0;
            foreach (JsonElement item in value.EnumerateArray())
            {
                string? error = itemType.GetString() switch
                {
                    "string" => ValidateString(items, item, $"{path}[{index}]"),
                    "integer" => ValidateInteger(item, $"{path}[{index}]"),
                    "boolean" => ValidateBoolean(item, $"{path}[{index}]"),
                    "array" => ValidateArray(items, item, $"{path}[{index}]"),
                    "object" => ValidateObject(items, item, $"{path}[{index}]"),
                    _ => null
                };

                if (error is not null)
                {
                    return error;
                }

                index++;
            }
        }

        return null;
    }

    private static bool HasDuplicatePropertyNames(JsonElement value)
    {
        HashSet<string> seen = new();
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                return true;
            }
        }

        return false;
    }
}
