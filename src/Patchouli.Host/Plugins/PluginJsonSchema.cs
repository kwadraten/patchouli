using System.Text.Json;
using System.Text.RegularExpressions;

namespace Patchouli.Host.Plugins;

/// <summary>
/// Strict JSON Schema subset validator used for plugin manifest schemas and runtime
/// input/result validation. The supported keyword set is intentionally small and explicit;
/// a schema document using anything else is rejected at compile time (fail-closed) rather
/// than silently passing. This is the interim validator until the intended JsonSchema.Net
/// package can be restored (see Patchouli.Host.csproj).
/// </summary>
public static class PluginJsonSchema
{
    // Keywords with real validation semantics in this subset.
    private static readonly HashSet<string> ValidationKeywords = new(StringComparer.Ordinal)
    {
        "type", "properties", "required", "additionalProperties", "items", "enum", "const",
        "minLength", "maxLength", "pattern", "minimum", "maximum", "exclusiveMinimum",
        "exclusiveMaximum", "multipleOf", "minItems", "maxItems", "anyOf", "oneOf", "allOf",
        "not", "$defs", "definitions", "$ref", "contentSchema"
    };

    // Inert annotation keywords accepted without semantics.
    private static readonly HashSet<string> AnnotationKeywords = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "title", "description", "default", "examples", "format",
        "deprecated", "readOnly", "writeOnly", "$comment", "contentEncoding",
        "contentMediaType"
    };

    private static readonly HashSet<string> TypeNames = new(StringComparer.Ordinal)
    {
        "string", "integer", "number", "boolean", "array", "object", "null"
    };

    /// <summary>
    /// Validates that a schema document only uses the supported subset. Returns null when the
    /// schema is acceptable or a diagnostic naming the unsupported construct.
    /// </summary>
    public static string? CheckSupported(JsonElement schema, string path = "$")
    {
        return CheckSupported(schema, schema, path);
    }

    private static string? CheckSupported(JsonElement schema, JsonElement root, string path)
    {
        if (schema.ValueKind == JsonValueKind.True || schema.ValueKind == JsonValueKind.False)
        {
            return null; // Boolean schemas are fully supported.
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            return $"Schema at '{path}' must be an object or a boolean.";
        }

        foreach (JsonProperty property in schema.EnumerateObject())
        {
            if (AnnotationKeywords.Contains(property.Name))
            {
                continue;
            }

            if (!ValidationKeywords.Contains(property.Name))
            {
                return $"Schema at '{path}' uses unsupported keyword '{property.Name}'.";
            }

            string? error = property.Name switch
            {
                "type" => CheckType(property.Value, path),
                "required" => CheckStringArray(property.Value, $"{path}.required"),
                "enum" or "allOf" or "anyOf" or "oneOf" =>
                    property.Value.ValueKind == JsonValueKind.Array
                        ? null
                        : $"Schema keyword '{property.Name}' at '{path}' must be an array.",
                "properties" or "$defs" or "definitions" =>
                    CheckSchemaMap(property.Value, $"{path}.{property.Name}", root),
                "additionalProperties" or "items" or "not" or "contentSchema" =>
                    CheckSupported(property.Value, root, $"{path}.{property.Name}"),
                "$ref" => CheckRef(property.Value, root, path),
                "minLength" or "maxLength" or "minItems" or "maxItems" =>
                    CheckNonNegativeInteger(property.Value, $"{path}.{property.Name}"),
                "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum" or "multipleOf" =>
                    property.Value.ValueKind == JsonValueKind.Number
                        ? null
                        : $"Schema keyword '{property.Name}' at '{path}' must be a number.",
                "pattern" => CheckRegex(property.Value, $"{path}.pattern"),
                _ => null
            };
            if (error is not null)
            {
                return error;
            }
        }

        foreach (string composite in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (schema.TryGetProperty(composite, out JsonElement list))
            {
                int index = 0;
                foreach (JsonElement branch in list.EnumerateArray())
                {
                    string? error = CheckSupported(branch, root, $"{path}.{composite}[{index++}]");
                    if (error is not null)
                    {
                        return error;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Local-fragment-only $ref handling: '#', '#/$defs/&lt;name&gt;' and
    /// '#/definitions/&lt;name&gt;' resolve against the schema document root; any other or
    /// external reference is rejected so a schema can never pull in outside resources.
    /// </summary>
    private static string? CheckRef(JsonElement reference, JsonElement root, string path)
    {
        return reference.ValueKind != JsonValueKind.String
            ? $"Schema keyword '$ref' at '{path}' must be a string."
            : ResolveRef(root, reference.GetString()!, out _) is { } error
                ? $"Schema $ref at '{path}': {error}"
                : null;
    }

    private static string? ResolveRef(JsonElement root, string pointer, out JsonElement resolved)
    {
        resolved = default;
        if (pointer == "#")
        {
            resolved = root;
            return null;
        }

        const string defsPrefix = "#/$defs/";
        const string definitionsPrefix = "#/definitions/";
        string container;
        string name;
        if (pointer.StartsWith(defsPrefix, StringComparison.Ordinal))
        {
            container = "$defs";
            name = pointer[defsPrefix.Length..];
        }
        else if (pointer.StartsWith(definitionsPrefix, StringComparison.Ordinal))
        {
            container = "definitions";
            name = pointer[definitionsPrefix.Length..];
        }
        else
        {
            return "only local '#', '#/$defs/<name>' or '#/definitions/<name>' references are " +
                   "supported; external or escaped references are rejected.";
        }

        if (name.Length == 0 || name.Contains('/'))
        {
            return "nested or empty definition names are not supported.";
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(container, out JsonElement definitions) &&
            definitions.ValueKind == JsonValueKind.Object &&
            definitions.TryGetProperty(name, out resolved))
        {
            return null;
        }

        return $"the referenced definition '{name}' does not exist.";
    }

    private static string? CheckType(JsonElement type, string path)
    {
        if (type.ValueKind == JsonValueKind.String)
        {
            return TypeNames.Contains(type.GetString()!)
                ? null
                : $"Schema at '{path}' has unsupported type '{type.GetString()}'.";
        }

        if (type.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in type.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String || !TypeNames.Contains(entry.GetString()!))
                {
                    return $"Schema at '{path}' has an unsupported type entry.";
                }
            }

            return null;
        }

        return $"Schema keyword 'type' at '{path}' must be a string or array of strings.";
    }

    private static string? CheckSchemaMap(JsonElement map, string path, JsonElement root)
    {
        if (map.ValueKind != JsonValueKind.Object)
        {
            return $"Schema keyword map at '{path}' must be an object.";
        }

        foreach (JsonProperty property in map.EnumerateObject())
        {
            string? error = CheckSupported(property.Value, root, $"{path}.{property.Name}");
            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }

    private static string? CheckStringArray(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Array ||
            value.EnumerateArray().Any(entry => entry.ValueKind != JsonValueKind.String))
        {
            return $"Schema keyword at '{path}' must be an array of strings.";
        }

        return null;
    }

    private static string? CheckNonNegativeInteger(JsonElement value, string path)
    {
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) && number >= 0
            ? null
            : $"Schema keyword at '{path}' must be a non-negative integer.";
    }

    private static string? CheckRegex(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return $"Schema keyword at '{path}' must be a string.";
        }

        try
        {
            _ = new Regex(value.GetString()!, RegexOptions.Compiled, TimeSpan.FromSeconds(1));
            return null;
        }
        catch (ArgumentException)
        {
            return $"Schema pattern at '{path}' is not a valid regular expression.";
        }
    }

    /// <summary>Validates a JSON value against a supported schema; null means valid.</summary>
    public static string? Validate(JsonElement schema, JsonElement value, string path = "$")
    {
        return ValidateCore(schema, value, schema, [], path);
    }

    private static string? ValidateCore(JsonElement schema, JsonElement value, JsonElement root,
        HashSet<string> activeRefs, string path)
    {
        if (schema.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        if (schema.ValueKind == JsonValueKind.False)
        {
            return $"Value at '{path}' is not allowed by the schema.";
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            return $"Schema at '{path}' is not valid.";
        }

        // draft 2020-12 semantics: $ref combines with sibling keywords. The resolved
        // definition is checked first, then the remaining keywords on this schema apply.
        if (schema.TryGetProperty("$ref", out JsonElement reference) &&
            reference.ValueKind == JsonValueKind.String)
        {
            string pointer = reference.GetString()!;
            if (!activeRefs.Add(pointer))
            {
                return $"Schema $ref '{pointer}' at '{path}' is cyclic.";
            }

            try
            {
                string? resolveError = ResolveRef(root, pointer, out JsonElement resolved);
                if (resolveError is not null)
                {
                    return $"Schema $ref '{pointer}' at '{path}': {resolveError}";
                }

                string? refError = ValidateCore(resolved, value, root, activeRefs, path);
                if (refError is not null)
                {
                    return refError;
                }
            }
            finally
            {
                activeRefs.Remove(pointer);
            }
        }

        if (schema.TryGetProperty("const", out JsonElement constant) && !JsonEqual(constant, value))
        {
            return $"Value at '{path}' does not equal the required constant.";
        }

        if (schema.TryGetProperty("enum", out JsonElement enumeration) &&
            enumeration.ValueKind == JsonValueKind.Array &&
            !enumeration.EnumerateArray().Any(entry => JsonEqual(entry, value)))
        {
            return $"Value at '{path}' is not one of the allowed enum values.";
        }

        if (schema.TryGetProperty("type", out JsonElement type) &&
            !MatchesType(type, value))
        {
            return $"Value at '{path}' has the wrong JSON type.";
        }

        string? error = ValidateString(schema, value, path)
                        ?? ValidateNumber(schema, value, path)
                        ?? ValidateArray(schema, value, root, activeRefs, path)
                        ?? ValidateObject(schema, value, root, activeRefs, path)
                        ?? ValidateComposites(schema, value, root, activeRefs, path);
        return error;
    }

    private static bool MatchesType(JsonElement type, JsonElement value)
    {
        bool One(string name)
        {
            return name switch
            {
                "string" => value.ValueKind == JsonValueKind.String,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "number" => value.ValueKind == JsonValueKind.Number,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "array" => value.ValueKind == JsonValueKind.Array,
                "object" => value.ValueKind == JsonValueKind.Object,
                "null" => value.ValueKind == JsonValueKind.Null,
                _ => true
            };
        }

        if (type.ValueKind == JsonValueKind.String)
        {
            return One(type.GetString()!);
        }

        return type.ValueKind == JsonValueKind.Array &&
               type.EnumerateArray().Any(entry => entry.ValueKind == JsonValueKind.String && One(entry.GetString()!));
    }

    private static string? ValidateString(JsonElement schema, JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string text = value.GetString()!;
        if (schema.TryGetProperty("minLength", out JsonElement min) && text.Length < min.GetInt32())
        {
            return $"Value at '{path}' is shorter than minLength {min.GetInt32()}.";
        }

        if (schema.TryGetProperty("maxLength", out JsonElement max) && text.Length > max.GetInt32())
        {
            return $"Value at '{path}' is longer than maxLength {max.GetInt32()}.";
        }

        if (schema.TryGetProperty("pattern", out JsonElement pattern) &&
            !Regex.IsMatch(text, pattern.GetString()!, RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            return $"Value at '{path}' does not match the required pattern.";
        }

        return null;
    }

    private static string? ValidateNumber(JsonElement schema, JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out decimal number))
        {
            return null;
        }

        decimal? Bound(string name)
        {
            return schema.TryGetProperty(name, out JsonElement bound) && bound.TryGetDecimal(out decimal limit)
                ? limit
                : null;
        }

        if (Bound("minimum") is { } min && number < min)
        {
            return $"Value at '{path}' is below minimum {min}.";
        }

        if (Bound("maximum") is { } max && number > max)
        {
            return $"Value at '{path}' is above maximum {max}.";
        }

        if (Bound("exclusiveMinimum") is { } exclusiveMin && number <= exclusiveMin)
        {
            return $"Value at '{path}' is not above exclusiveMinimum {exclusiveMin}.";
        }

        if (Bound("exclusiveMaximum") is { } exclusiveMax && number >= exclusiveMax)
        {
            return $"Value at '{path}' is not below exclusiveMaximum {exclusiveMax}.";
        }

        if (Bound("multipleOf") is { } divisor && divisor != 0 && number % divisor != 0)
        {
            return $"Value at '{path}' is not a multiple of {divisor}.";
        }

        return null;
    }

    private static string? ValidateArray(JsonElement schema, JsonElement value, JsonElement root,
        HashSet<string> activeRefs, string path)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        if (schema.TryGetProperty("minItems", out JsonElement min) && value.GetArrayLength() < min.GetInt32())
        {
            return $"Array at '{path}' has fewer than minItems {min.GetInt32()}.";
        }

        if (schema.TryGetProperty("maxItems", out JsonElement max) && value.GetArrayLength() > max.GetInt32())
        {
            return $"Array at '{path}' has more than maxItems {max.GetInt32()}.";
        }

        if (schema.TryGetProperty("items", out JsonElement items))
        {
            int index = 0;
            foreach (JsonElement item in value.EnumerateArray())
            {
                string? error = ValidateCore(items, item, root, activeRefs, $"{path}[{index++}]");
                if (error is not null)
                {
                    return error;
                }
            }
        }

        return null;
    }

    private static string? ValidateObject(JsonElement schema, JsonElement value, JsonElement root,
        HashSet<string> activeRefs, string path)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (schema.TryGetProperty("required", out JsonElement required) &&
            required.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement name in required.EnumerateArray())
            {
                if (name.ValueKind == JsonValueKind.String && !value.TryGetProperty(name.GetString()!, out _))
                {
                    return $"Object at '{path}' is missing required property '{name.GetString()}'.";
                }
            }
        }

        schema.TryGetProperty("properties", out JsonElement properties);
        if (schema.TryGetProperty("additionalProperties", out JsonElement additional) &&
            additional.ValueKind == JsonValueKind.False && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!properties.TryGetProperty(property.Name, out _))
                {
                    return $"Object at '{path}' has undeclared property '{property.Name}'.";
                }
            }
        }

        if (properties.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty propertySchema in properties.EnumerateObject())
            {
                if (value.TryGetProperty(propertySchema.Name, out JsonElement propertyValue))
                {
                    string? error = ValidateCore(propertySchema.Value, propertyValue, root, activeRefs,
                        $"{path}.{propertySchema.Name}");
                    if (error is not null)
                    {
                        return error;
                    }
                }
            }
        }

        if (schema.TryGetProperty("additionalProperties", out JsonElement additionalSchema) &&
            additionalSchema.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (properties.ValueKind != JsonValueKind.Object ||
                    !properties.TryGetProperty(property.Name, out _))
                {
                    string? error = ValidateCore(additionalSchema, property.Value, root, activeRefs,
                        $"{path}.{property.Name}");
                    if (error is not null)
                    {
                        return error;
                    }
                }
            }
        }

        return null;
    }

    private static string? ValidateComposites(JsonElement schema, JsonElement value, JsonElement root,
        HashSet<string> activeRefs, string path)
    {
        if (schema.TryGetProperty("allOf", out JsonElement allOf))
        {
            int index = 0;
            foreach (JsonElement branch in allOf.EnumerateArray())
            {
                string? error = ValidateCore(branch, value, root, activeRefs, $"{path}(allOf[{index++}])");
                if (error is not null)
                {
                    return error;
                }
            }
        }

        if (schema.TryGetProperty("anyOf", out JsonElement anyOf))
        {
            int index = 0;
            bool matched = anyOf.EnumerateArray()
                .Any(branch => ValidateCore(branch, value, root, activeRefs, $"{path}(anyOf[{index++}])") is null);
            if (!matched)
            {
                return $"Value at '{path}' matches no anyOf branch.";
            }
        }

        if (schema.TryGetProperty("oneOf", out JsonElement oneOf))
        {
            int index = 0;
            int matches = oneOf.EnumerateArray()
                .Count(branch => ValidateCore(branch, value, root, activeRefs, $"{path}(oneOf[{index++}])") is null);
            if (matches != 1)
            {
                return $"Value at '{path}' matches {matches} oneOf branches (exactly one required).";
            }
        }

        if (schema.TryGetProperty("not", out JsonElement not) &&
            ValidateCore(not, value, root, activeRefs, path) is null)
        {
            return $"Value at '{path}' matches a forbidden 'not' schema.";
        }

        return null;
    }

    private static bool JsonEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        return left.ValueKind switch
        {
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Number => left.TryGetDecimal(out decimal a) && right.TryGetDecimal(out decimal b) && a == b,
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength() &&
                                   left.EnumerateArray().Zip(right.EnumerateArray())
                                       .All(pair => JsonEqual(pair.First, pair.Second)),
            JsonValueKind.Object => left.EnumerateObject().Count() == right.EnumerateObject().Count() &&
                                    left.EnumerateObject().All(property =>
                                        right.TryGetProperty(property.Name, out JsonElement other) &&
                                        JsonEqual(property.Value, other)),
            _ => false
        };
    }
}
