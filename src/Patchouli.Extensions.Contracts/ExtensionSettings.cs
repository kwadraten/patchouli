using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Patchouli.Extensions.Sdk;

using Contracts;

[AttributeUsage(AttributeTargets.Property)]
public sealed class FieldAttribute(string label) : Attribute
{
    public string Label { get; } = label;
    public string? Key { get; set; }
    public ConfigValueKind Kind { get; set; } = ConfigValueKind.Text;
    public string Description { get; set; } = "";
    public string[] Choices { get; set; } = [];
    public bool Required { get; set; }
    public bool Hidden { get; set; }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class RangeAttribute : Attribute
{
    public RangeAttribute(int minimum, int maximum)
    {
        Minimum = minimum;
        Maximum = maximum;
    }

    public RangeAttribute(double minimum, double maximum)
    {
        Minimum = ToDecimal(minimum);
        Maximum = ToDecimal(maximum);
    }

    public RangeAttribute(string minimum, string maximum)
    {
        Minimum = decimal.Parse(minimum, NumberStyles.Number, CultureInfo.InvariantCulture);
        Maximum = decimal.Parse(maximum, NumberStyles.Number, CultureInfo.InvariantCulture);
    }

    public decimal Minimum { get; }
    public decimal Maximum { get; }

    private static decimal ToDecimal(double value)
    {
        return double.IsFinite(value)
            ? Convert.ToDecimal(value, CultureInfo.InvariantCulture)
            : throw new ArgumentOutOfRangeException(nameof(value));
    }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class DefaultAttribute(object value) : Attribute
{
    public object Value { get; } = value;
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class ModelAttribute(string label) : Attribute
{
    public string Label { get; } = label;
    public string? FollowDefault { get; set; }
    public string? Key { get; set; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class SettingsAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public sealed class OverrideAttribute(string key, object value) : Attribute
{
    public string Key { get; } = key;
    public object Value { get; } = value;
}

/// <summary>Reads one attributed settings record and projects its flattened schema without executing package code.</summary>
public static class ConfigSettings
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const string CompilationMappingAttributeName = "Microsoft.FSharp.Core.CompilationMappingAttribute";
    private const int SourceConstructKindMask = 31;
    private const int RecordTypeFlag = 2;
    private const int RecordFieldFlag = 4;

    private sealed record SettingField(
        PropertyInfo Property,
        PropertyInfo? Parent,
        ConfigFieldDescriptor Descriptor,
        object? DefaultValue);

    private static bool SameProperty(PropertyInfo? left, PropertyInfo? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        // Hot-reload handlers may replace reflection wrappers while another generation builds
        // its settings plan. Compare the property definition, not the transient wrapper identity.
        return left.MetadataToken == right.MetadataToken &&
               left.Module.ModuleVersionId == right.Module.ModuleVersionId &&
               left.DeclaringType == right.DeclaringType;
    }

    private sealed record RuntimePropertyPlan(
        PropertyInfo Property,
        SettingField? Field,
        RuntimeSettingsPlan? Nested);

    private sealed record RuntimeSettingsPlan(
        Type RecordType,
        ConstructorInfo Constructor,
        RuntimePropertyPlan[] Properties,
        SettingField[] Fields);

    private static readonly ConditionalWeakTable<Type, Lazy<RuntimeSettingsPlan>> RuntimePlans = new();
    private static int _runtimePlanBuildCount;

    internal static int RuntimePlanBuildCount => Volatile.Read(ref _runtimePlanBuildCount);

    public static T Read<T>(ExtensionConfigSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Type root = typeof(T);
        if (root.FullName == "Microsoft.FSharp.Core.Unit")
        {
            return (T)Activator.CreateInstance(root)!;
        }

        return (T)CreateRecord(GetRuntimePlan(root), snapshot);
    }

    /// <summary>Describes an attributed record through the same validation path used by metadata schema reads.</summary>
    public static IReadOnlyList<ConfigFieldDescriptor> Describe<T>()
    {
        Type type = typeof(T);
        if (type.FullName == "Microsoft.FSharp.Core.Unit")
        {
            return Array.Empty<ConfigFieldDescriptor>();
        }

        return Array.AsReadOnly(GetRuntimePlan(type).Fields.Select(field => field.Descriptor).ToArray());
    }

    /// <summary>Reads one Settings-marked root record from assembly metadata; no attribute constructor or package code is executed.</summary>
    public static IReadOnlyList<ConfigFieldDescriptor> ReadSchema(string assemblyPath, IEnumerable<string> refs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentNullException.ThrowIfNull(refs);
        string[] paths = refs.Append(assemblyPath).Append(typeof(object).Assembly.Location)
            .Append(typeof(Task).Assembly.Location).Append(typeof(ConfigSettings).Assembly.Location)
            .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using MetadataLoadContext context = new(new PathAssemblyResolver(paths),
            typeof(object).Assembly.GetName().Name ?? "System.Private.CoreLib");
        Assembly assembly = context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        Type[] roots = assembly.GetTypes().Where(type => HasAttribute(type, typeof(SettingsAttribute))).ToArray();
        if (roots.Length == 0)
        {
            return Array.Empty<ConfigFieldDescriptor>();
        }

        if (roots.Length > 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one SettingsAttribute root record; found {roots.Length}.");
        }

        return Array.AsReadOnly(Discover(roots[0]).Select(field => field.Descriptor).ToArray());
    }

    private static SettingField[] Discover(Type root)
    {
        EnsureRecord(root);
        List<SettingField> fields = [];
        foreach (PropertyInfo property in OrderedProperties(root))
        {
            CustomAttributeData? fieldAttribute = GetAttribute(property, typeof(FieldAttribute));
            CustomAttributeData? modelAttribute = GetAttribute(property, typeof(ModelAttribute));
            if (fieldAttribute is not null && modelAttribute is not null)
            {
                throw Invalid($"Setting '{property.Name}' cannot have both Field and Model attributes.");
            }

            if (fieldAttribute is not null || modelAttribute is not null)
            {
                if (GetAttributes(property, typeof(OverrideAttribute)).Any())
                {
                    throw Invalid(
                        $"Override must be declared on a nested CommonSettings property, not '{property.Name}'.");
                }

                fields.Add(Describe(property, null, null));
                continue;
            }

            CustomAttributeData[] overrides = GetAttributes(property, typeof(OverrideAttribute)).ToArray();
            if (property.PropertyType.FullName == root.FullName || IsScalar(property.PropertyType))
            {
                if (overrides.Length > 0)
                {
                    throw Invalid($"Override is not valid on scalar setting '{property.Name}'.");
                }

                throw Invalid($"Settings property '{property.Name}' must declare Field or Model.");
            }

            EnsureRecord(property.PropertyType);
            foreach (PropertyInfo child in OrderedProperties(property.PropertyType))
            {
                if (GetAttribute(child, typeof(FieldAttribute)) is null &&
                    GetAttribute(child, typeof(ModelAttribute)) is null)
                {
                    throw Invalid($"Nested common setting '{property.Name}.{child.Name}' must declare Field or Model.");
                }

                fields.Add(Describe(child, property, overrides));
            }
        }

        string[] keys = fields.Select(field => field.Descriptor.Key).ToArray();
        string? duplicate = keys.GroupBy(key => key, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1)
            ?.Key;
        if (duplicate is not null)
        {
            throw Invalid($"Configuration key '{duplicate}' is declared more than once.");
        }

        foreach (PropertyInfo parent in OrderedProperties(root)
                     .Where(property => GetAttributes(property, typeof(OverrideAttribute)).Any()))
        {
            string[] overrideKeys = GetAttributes(parent, typeof(OverrideAttribute))
                .Select(attribute => RequiredStringArgument(attribute, 0)).ToArray();
            if (overrideKeys.Distinct(StringComparer.Ordinal).Count() != overrideKeys.Length)
            {
                throw Invalid($"Nested settings '{parent.Name}' declares duplicate overrides.");
            }

            HashSet<string> supplied = overrideKeys.ToHashSet(StringComparer.Ordinal);
            foreach (string key in supplied)
            {
                if (!fields.Any(field => SameProperty(field.Parent, parent) && field.Descriptor.Key == key))
                {
                    throw Invalid($"Override on '{parent.Name}' names unknown nested configuration key '{key}'.");
                }
            }
        }

        return fields.ToArray();
    }

    private static RuntimeSettingsPlan GetRuntimePlan(Type type)
    {
        return RuntimePlans.GetValue(type, static key =>
                new Lazy<RuntimeSettingsPlan>(() => BuildRuntimePlan(key),
                    LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;
    }

    private static RuntimeSettingsPlan BuildRuntimePlan(Type root)
    {
        Interlocked.Increment(ref _runtimePlanBuildCount);
        SettingField[] fields = Discover(root);
        return BuildRuntimePlan(root, null, fields);
    }

    private static RuntimeSettingsPlan BuildRuntimePlan(Type recordType, PropertyInfo? parent, SettingField[] fields)
    {
        PropertyInfo[] ordered = OrderedProperties(recordType);
        ConstructorInfo constructor = FindRecordConstructor(recordType, ordered);
        PropertyInfo[] constructorOrder = constructor.GetParameters().Select(parameter =>
            ordered.Single(property => StringComparer.OrdinalIgnoreCase.Equals(property.Name, parameter.Name) &&
                                       property.PropertyType.FullName == parameter.ParameterType.FullName)).ToArray();
        RuntimePropertyPlan[] properties = constructorOrder.Select(property =>
        {
            SettingField? field =
                fields.SingleOrDefault(candidate =>
                    SameProperty(candidate.Property, property) && SameProperty(candidate.Parent, parent));
            PropertyInfo[] children = parent is null
                ? fields.Where(candidate => SameProperty(candidate.Parent, property))
                    .Select(candidate => candidate.Property)
                    .ToArray()
                : [];
            if (field is not null && children.Length != 0)
            {
                throw Invalid(
                    $"Settings property '{property.Name}' cannot be both a field and a nested common record.");
            }

            RuntimeSettingsPlan? nested = children.Length == 0
                ? null
                : BuildRuntimePlan(property.PropertyType, property, fields);
            return new RuntimePropertyPlan(property, field, nested);
        }).ToArray();
        return new RuntimeSettingsPlan(recordType, constructor, properties, fields);
    }

    private static SettingField Describe(PropertyInfo property, PropertyInfo? parent,
        CustomAttributeData[]? parentOverrides)
    {
        CustomAttributeData? field = GetAttribute(property, typeof(FieldAttribute));
        CustomAttributeData? model = GetAttribute(property, typeof(ModelAttribute));
        string label = RequiredStringArgument(field ?? model!, 0);
        string key = GetNamedString(field ?? model!, "Key") ?? ToKebabCase(property.Name);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw Invalid($"Setting '{property.Name}' has an empty configuration key.");
        }

        CustomAttributeData? ownOverride = GetAttribute(property, typeof(OverrideAttribute));
        if (ownOverride is not null)
        {
            throw Invalid($"Override must be declared on a nested CommonSettings property, not '{property.Name}'.");
        }

        CustomAttributeData[] matchingOverrides = parentOverrides?.Where(attribute =>
            StringComparer.Ordinal.Equals(RequiredStringArgument(attribute, 0), key)).ToArray() ?? [];
        if (matchingOverrides.Length > 1)
        {
            throw Invalid($"Configuration key '{key}' has duplicate overrides.");
        }

        CustomAttributeData? defaultAttribute = GetAttribute(property, typeof(DefaultAttribute));
        object? defaultValue = matchingOverrides.Length == 1
            ? ValidateConstant(matchingOverrides[0].ConstructorArguments[1].Value)
            : defaultAttribute is not null
                ? ValidateConstant(defaultAttribute.ConstructorArguments[0].Value)
                : null;

        ConfigValueKind kind = model is not null
            ? ConfigValueKind.Model
            : field is not null && field.NamedArguments.Any(argument => argument.MemberName == "Kind")
                ? (ConfigValueKind)Convert.ToInt32(
                    field.NamedArguments.Single(argument => argument.MemberName == "Kind").TypedValue.Value,
                    CultureInfo.InvariantCulture)
                : KindFor(property.PropertyType);
        if (!Enum.IsDefined(kind))
        {
            throw Invalid($"Setting '{key}' declares an unknown field kind.");
        }

        if (defaultValue is not null)
        {
            ValidateConstantType(defaultValue, property.PropertyType, kind, key);
        }

        bool optionalModel = false;
        if (kind == ConfigValueKind.Model)
        {
            Type modelType = property.PropertyType;
            optionalModel = modelType.IsGenericType &&
                            modelType.GetGenericTypeDefinition().FullName == "Microsoft.FSharp.Core.FSharpOption`1";
            if (optionalModel)
            {
                modelType = modelType.GetGenericArguments()[0];
            }

            if (modelType.FullName != typeof(ExtensionModelSelection).FullName)
            {
                throw Invalid($"Model setting '{key}' must use ExtensionModelSelection or its F# option.");
            }
        }

        if (kind is ConfigValueKind.Integer or ConfigValueKind.Decimal or ConfigValueKind.Boolean
            or ConfigValueKind.Text or
            ConfigValueKind.MultilineText or ConfigValueKind.Language or ConfigValueKind.Choice
            or ConfigValueKind.CredentialReference)
        {
            Type rawType = IsNullable(property.PropertyType)
                ? property.PropertyType.GetGenericArguments()[0]
                : property.PropertyType;
            if (kind is ConfigValueKind.Integer or ConfigValueKind.Decimal or ConfigValueKind.Boolean &&
                rawType.IsGenericType &&
                rawType.GetGenericTypeDefinition().FullName == "Microsoft.FSharp.Core.FSharpOption`1")
            {
                rawType = rawType.GetGenericArguments()[0];
            }

            bool compatible = kind switch
            {
                ConfigValueKind.Integer => rawType.FullName == "System.Int32",
                ConfigValueKind.Decimal => rawType.FullName is "System.Decimal" or "System.Double",
                ConfigValueKind.Boolean => rawType.FullName == "System.Boolean",
                _ => rawType.FullName == "System.String"
            };
            if (!compatible)
            {
                throw Invalid(
                    $"Field kind '{kind}' is incompatible with setting '{key}' type '{property.PropertyType}'.");
            }
        }

        CustomAttributeData? range = GetAttribute(property, typeof(RangeAttribute));
        decimal? minimum = range is null ? null : ReadRangeBound(range.ConstructorArguments[0]);
        decimal? maximum = range is null ? null : ReadRangeBound(range.ConstructorArguments[1]);
        if (minimum is null != maximum is null || (minimum is not null && minimum > maximum))
        {
            throw Invalid($"Range on '{key}' is invalid.");
        }

        if (range is not null && kind is not (ConfigValueKind.Integer or ConfigValueKind.Decimal))
        {
            throw Invalid($"Range is only valid on numeric setting '{key}'.");
        }

        string? defaultText = defaultValue is null ? null : FormatDefault(defaultValue, property.PropertyType, kind);
        bool followsDefault = model is not null && GetNamedString(model, "FollowDefault") is { Length: > 0 };
        string? defaultDomain = model is null ? null : GetNamedString(model, "FollowDefault");
        if (defaultDomain is not null && defaultDomain is not ("translation" or "metadata"))
        {
            throw Invalid($"Model setting '{key}' declares an unknown default domain '{defaultDomain}'.");
        }

        if (defaultText is not null)
        {
            ValidateValue(key, kind, defaultText, false, minimum, maximum, GetNamedStrings(field ?? model!, "Choices"));
        }

        bool required = field is not null && field.NamedArguments.Any(argument => argument.MemberName == "Required")
            ? GetNamedBoolean(field, "Required")
            : defaultText is null && (kind != ConfigValueKind.Model || !optionalModel);
        string description = GetNamedString(field ?? model!, "Description") ?? "";
        string[] choices = GetNamedStrings(field ?? model!, "Choices");
        if (choices.Distinct(StringComparer.Ordinal).Count() != choices.Length)
        {
            throw Invalid($"Choice setting '{key}' has duplicate options.");
        }

        if (kind == ConfigValueKind.Choice && choices.Length == 0)
        {
            throw Invalid($"Choice setting '{key}' must declare at least one option.");
        }

        bool hidden = GetNamedBoolean(field ?? model!, "Hidden");
        ConfigFieldDescriptor descriptor = new(key, label, kind, required, description, defaultText, minimum, maximum,
            choices.Length == 0 ? null : Array.AsReadOnly(choices))
        {
            FollowDefault = followsDefault,
            DefaultDomain = defaultDomain,
            Hidden = hidden
        };
        return new SettingField(property, parent, descriptor, defaultValue);
    }

    private static object CreateRecord(RuntimeSettingsPlan plan, ExtensionConfigSnapshot snapshot)
    {
        object?[] values = new object?[plan.Properties.Length];
        for (int i = 0; i < plan.Properties.Length; i++)
        {
            RuntimePropertyPlan propertyPlan = plan.Properties[i];
            PropertyInfo property = propertyPlan.Property;
            SettingField? field = propertyPlan.Field;
            if (field is not null)
            {
                string key = field.Descriptor.Key;
                bool configured = snapshot.Values.TryGetValue(key, out string? saved);
                string raw = configured
                    ? saved!
                    : field.DefaultValue is not null
                        ? FormatDefault(field.DefaultValue, property.PropertyType, field.Descriptor.Kind)
                        : "";
                ValidateValue(key, field.Descriptor.Kind, raw, field.Descriptor.Required,
                    field.Descriptor.Minimum, field.Descriptor.Maximum, field.Descriptor.Choices);
                if (!configured && field.DefaultValue is null && field.Descriptor.Required)
                {
                    throw Invalid($"Required configuration key '{key}' has no saved value or default.");
                }

                values[i] = ParseValue(raw, property.PropertyType, field.Descriptor.Kind);
            }
            else if (propertyPlan.Nested is not null)
            {
                values[i] = CreateRecord(propertyPlan.Nested, snapshot);
            }
            else
            {
                throw Invalid($"Settings property '{plan.RecordType}.{property.Name}' has no runtime settings plan.");
            }
        }

        return plan.Constructor.Invoke(values);
    }

    private static object? ParseValue(string raw, Type declaredType, ConfigValueKind kind)
    {
        Type target = IsNullable(declaredType) ? declaredType.GetGenericArguments()[0] : declaredType;
        bool option = target.IsGenericType &&
                      target.GetGenericTypeDefinition().FullName == "Microsoft.FSharp.Core.FSharpOption`1";
        Type valueType = option ? target.GetGenericArguments()[0] : target;
        if (kind == ConfigValueKind.Model)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return option ? null : throw Invalid("A required model selection must include providerId and model.");
            }

            ExtensionModelSelection? selection = System.Text.Json.JsonSerializer.Deserialize<ExtensionModelSelection>(
                raw,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            string providerId = selection?.ProviderId ?? "";
            string model = selection?.Model ?? "";
            if (string.IsNullOrWhiteSpace(providerId) && string.IsNullOrWhiteSpace(model))
            {
                return option ? null : throw Invalid("A required model selection must include providerId and model.");
            }

            if (!option && (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(model)))
            {
                throw Invalid("A required model selection must include providerId and model.");
            }

            ExtensionModelSelection nonNullSelection = new(providerId, model);
            return option
                ? target.GetMethod("Some", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, [nonNullSelection])
                  ?? throw Invalid($"Unsupported F# option type '{target}'.")
                : nonNullSelection;
        }

        string? typeName = valueType.FullName;
        object? value = typeName == "System.String"
            ? raw
            : typeName == "System.Boolean"
                ? bool.Parse(raw)
                : typeName == "System.Int32"
                    ? int.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture)
                    : typeName == "System.Double"
                        ? double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)
                        : typeName == "System.Decimal"
                            ? decimal.Parse(raw, NumberStyles.Number, CultureInfo.InvariantCulture)
                            : valueType.IsEnum
                                ? Enum.Parse(valueType, raw, true)
                                : typeName == "Patchouli.Extensions.Contracts.ExtensionModelSelection"
                                    ? System.Text.Json.JsonSerializer.Deserialize(raw, valueType,
                                        new System.Text.Json.JsonSerializerOptions
                                            { PropertyNameCaseInsensitive = true })
                                    : Convert.ChangeType(raw, valueType, CultureInfo.InvariantCulture);
        if (!option || value is null)
        {
            return value;
        }

        MethodInfo some = target.GetMethod("Some", BindingFlags.Public | BindingFlags.Static)
                          ?? throw Invalid($"Unsupported F# option type '{target}'.");
        return some.Invoke(null, [value]);
    }

    private static string FormatDefault(object value, Type propertyType, ConfigValueKind kind)
    {
        if (kind == ConfigValueKind.Model)
        {
            return value is string json ? json : System.Text.Json.JsonSerializer.Serialize(value);
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    private static void ValidateValue(string key, ConfigValueKind kind, string value, bool required,
        decimal? minimum, decimal? maximum, IReadOnlyList<string>? choices)
    {
        if (required && string.IsNullOrWhiteSpace(value))
        {
            throw Invalid($"Required configuration key '{key}' has no value.");
        }

        if (kind == ConfigValueKind.Choice && choices is not null && !choices.Contains(value, StringComparer.Ordinal))
        {
            throw Invalid($"Configuration key '{key}' is not one of its declared choices.");
        }

        try
        {
            decimal number;
            switch (kind)
            {
                case ConfigValueKind.Integer:
                    number = int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    break;
                case ConfigValueKind.Decimal:
                    number = decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);
                    break;
                case ConfigValueKind.Boolean:
                    _ = bool.Parse(value);
                    return;
                case ConfigValueKind.Model:
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(value);
                        if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                        {
                            throw Invalid($"Model setting '{key}' must be a JSON object.");
                        }

                        foreach (System.Text.Json.JsonProperty property in document.RootElement.EnumerateObject())
                        {
                            if ((property.Name.Equals("providerId", StringComparison.OrdinalIgnoreCase) ||
                                 property.Name.Equals("model", StringComparison.OrdinalIgnoreCase)) &&
                                property.Value.ValueKind is not (System.Text.Json.JsonValueKind.String
                                    or System.Text.Json.JsonValueKind.Null))
                            {
                                throw Invalid($"Model setting '{key}' has an invalid provider/model value.");
                            }
                        }
                    }

                    return;
                default:
                    return;
            }

            if ((minimum is not null && number < minimum) || (maximum is not null && number > maximum))
            {
                throw Invalid($"Configuration value for '{key}' is outside its declared range.");
            }
        }
        catch (Exception error) when (error is FormatException or OverflowException or System.Text.Json.JsonException)
        {
            throw Invalid($"Configuration value for '{key}' is invalid for its declared type.");
        }
    }

    private static decimal ReadRangeBound(CustomAttributeTypedArgument argument)
    {
        object? value = argument.Value;
        return value is string text
            ? decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture)
            : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }

    private static object ValidateConstant(object? value)
    {
        while (value is CustomAttributeTypedArgument argument)
        {
            value = argument.Value;
        }

        return value switch
        {
            string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double
                or char => value,
            _ => throw Invalid(
                "Default and override values must be CLR attribute constants (int, double, string, Boolean, or char).")
        };
    }

    private static ConfigValueKind KindFor(Type type)
    {
        Type actual = IsNullable(type) ? type.GetGenericArguments()[0] : type;
        if (actual.FullName == "System.String")
        {
            return ConfigValueKind.Text;
        }

        if (actual.FullName == "System.Boolean")
        {
            return ConfigValueKind.Boolean;
        }

        if (actual.FullName == "System.Int32")
        {
            return ConfigValueKind.Integer;
        }

        if (actual.FullName is "System.Double" or "System.Decimal")
        {
            return ConfigValueKind.Decimal;
        }

        throw Invalid($"Unsupported settings property type '{type}'.");
    }

    private static bool IsNullable(Type type)
    {
        return type.IsGenericType && type.GetGenericTypeDefinition().FullName == "System.Nullable`1";
    }

    private static bool IsScalar(Type type)
    {
        return type.FullName is "System.String" or "System.Decimal" || type.IsPrimitive || type.IsEnum;
    }

    private static void EnsureRecord(Type type)
    {
        if (type.IsPrimitive || type.IsEnum || type.FullName == "System.String" || type.IsAbstract ||
            type.IsInterface ||
            !IsFSharpRecord(type))
        {
            throw Invalid($"Settings type '{type}' must be a record.");
        }
    }

    private static PropertyInfo[] OrderedProperties(Type type)
    {
        PropertyInfo[] properties = type.GetProperties(Instance);
        (PropertyInfo Property, int Sequence)[] mapped = properties.Select(property =>
        {
            CustomAttributeData[] fieldMappings = GetAttributes(property, CompilationMappingAttributeName)
                .Where(attribute => MappingKind(attribute) == RecordFieldFlag).ToArray();
            if (fieldMappings.Length > 1 ||
                (fieldMappings.Length == 1 && fieldMappings[0].ConstructorArguments.Count < 2))
            {
                throw Invalid($"Property '{type}.{property.Name}' has invalid F# record field metadata.");
            }

            if (fieldMappings.Length == 0)
            {
                return (Property: property, Sequence: -1);
            }

            int sequence = Convert.ToInt32(fieldMappings[0].ConstructorArguments[^1].Value,
                CultureInfo.InvariantCulture);
            return (Property: property, Sequence: sequence);
        }).Where(item => item.Sequence >= 0).OrderBy(item => item.Sequence).ToArray();
        if (mapped.Length == 0)
        {
            throw Invalid($"F# record '{type}' has no property-mapped record fields.");
        }

        if (mapped.Select(item => item.Sequence).Distinct().Count() != mapped.Length ||
            mapped.Where((item, index) => item.Sequence != index).Any())
        {
            throw Invalid($"F# record '{type}' has invalid field sequence metadata.");
        }

        PropertyInfo[] ordered = mapped.Select(item => item.Property).ToArray();
        ConstructorInfo constructor = FindRecordConstructor(type, ordered);
        ParameterInfo[] parameters = constructor.GetParameters();
        return parameters.Select(parameter => ordered.Single(property =>
                StringComparer.OrdinalIgnoreCase.Equals(property.Name, parameter.Name) &&
                property.PropertyType.FullName == parameter.ParameterType.FullName))
            .ToArray();
    }

    private static ConstructorInfo FindRecordConstructor(Type type, PropertyInfo[] orderedProperties)
    {
        ConstructorInfo[] matches = type.GetConstructors(Instance).Where(candidate =>
            candidate.GetParameters().Length == orderedProperties.Length && candidate.GetParameters()
                .Select(parameter => orderedProperties.Any(property =>
                    StringComparer.OrdinalIgnoreCase.Equals(property.Name, parameter.Name) &&
                    property.PropertyType.FullName == parameter.ParameterType.FullName))
                .All(matches => matches)).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw Invalid(
                $"Settings type '{type}' must have exactly one record constructor matching its mapped fields.");
    }

    private static bool IsFSharpRecord(Type type)
    {
        return GetAttributes(type, CompilationMappingAttributeName)
            .Any(attribute => MappingKind(attribute) == RecordTypeFlag);
    }

    private static int MappingKind(CustomAttributeData attribute)
    {
        if (attribute.ConstructorArguments.Count == 0)
        {
            return -1;
        }

        return Convert.ToInt32(attribute.ConstructorArguments[0].Value, CultureInfo.InvariantCulture) &
               SourceConstructKindMask;
    }

    private static void ValidateConstantType(object value, Type declaredType, ConfigValueKind kind, string key)
    {
        Type target = IsNullable(declaredType) ? declaredType.GetGenericArguments()[0] : declaredType;
        if (target.IsGenericType &&
            target.GetGenericTypeDefinition().FullName == "Microsoft.FSharp.Core.FSharpOption`1")
        {
            target = target.GetGenericArguments()[0];
        }

        string? typeName = target.FullName;
        Type constantType = value.GetType();
        bool compatible = kind switch
        {
            ConfigValueKind.Model => typeName == "Patchouli.Extensions.Contracts.ExtensionModelSelection" &&
                                     constantType == typeof(string),
            ConfigValueKind.Text or ConfigValueKind.MultilineText or ConfigValueKind.Language or ConfigValueKind.Choice
                or
                ConfigValueKind.CredentialReference or ConfigValueKind.Documents or ConfigValueKind.PageRange =>
                typeName == "System.String" && constantType == typeof(string),
            ConfigValueKind.Boolean => typeName == "System.Boolean" && constantType == typeof(bool),
            ConfigValueKind.Integer => typeName == "System.Int32" && constantType == typeof(int),
            ConfigValueKind.Decimal when typeName == "System.Decimal" => constantType == typeof(string),
            ConfigValueKind.Decimal when typeName == "System.Double" =>
                constantType == typeof(double) || constantType == typeof(int),
            _ => false
        };
        if (!compatible)
        {
            throw Invalid(
                $"Default or override value for '{key}' is not compatible with member type '{declaredType}'. " +
                "Decimal members use an invariant-culture string constant.");
        }
    }

    private static bool HasAttribute(MemberInfo member, Type attributeType)
    {
        return GetAttribute(member, attributeType) is not null;
    }

    private static CustomAttributeData? GetAttribute(MemberInfo member, Type attributeType)
    {
        return GetAttributes(member, attributeType).SingleOrDefault();
    }

    private static IEnumerable<CustomAttributeData> GetAttributes(MemberInfo member, Type attributeType)
    {
        return CustomAttributeData.GetCustomAttributes(member)
            .Where(attribute => attribute.AttributeType.FullName == attributeType.FullName);
    }

    private static IEnumerable<CustomAttributeData> GetAttributes(MemberInfo member, string attributeTypeFullName)
    {
        return CustomAttributeData.GetCustomAttributes(member)
            .Where(attribute => attribute.AttributeType.FullName == attributeTypeFullName &&
                                StringComparer.Ordinal.Equals(attribute.AttributeType.Assembly.GetName().Name,
                                    "FSharp.Core"));
    }

    private static string RequiredStringArgument(CustomAttributeData attribute, int index)
    {
        return attribute.ConstructorArguments.Count > index &&
               attribute.ConstructorArguments[index].Value is string value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw Invalid($"Attribute '{attribute.AttributeType.Name}' requires a non-empty string argument.");
    }

    private static string? GetNamedString(CustomAttributeData attribute, string name)
    {
        return attribute.NamedArguments.FirstOrDefault(argument => argument.MemberName == name).TypedValue
            .Value as string;
    }

    private static bool GetNamedBoolean(CustomAttributeData attribute, string name)
    {
        return attribute.NamedArguments.FirstOrDefault(argument => argument.MemberName == name).TypedValue
            .Value is true;
    }

    private static string[] GetNamedStrings(CustomAttributeData? attribute, string name)
    {
        if (attribute is null)
        {
            return [];
        }

        CustomAttributeTypedArgument? named = attribute.NamedArguments
            .FirstOrDefault(argument => argument.MemberName == name).TypedValue;
        return named?.Value is IEnumerable<CustomAttributeTypedArgument> items
            ? items.Select(item => item.Value as string ?? "").ToArray()
            : [];
    }

    private static string ToKebabCase(string value)
    {
        System.Text.StringBuilder result = new(value.Length + 8);
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (char.IsUpper(current) && index > 0 && (char.IsLower(value[index - 1]) ||
                                                       (index + 1 < value.Length && char.IsUpper(value[index - 1]) &&
                                                        char.IsLower(value[index + 1]))))
            {
                result.Append('-');
            }

            result.Append(char.ToLowerInvariant(current));
        }

        return result.ToString();
    }

    private static InvalidOperationException Invalid(string message)
    {
        return new InvalidOperationException($"EXTCFG001: {message}");
    }
}
