using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Workflows.Scripting;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>One editor row for a parameter declared in the workflow script.</summary>
public sealed partial class WorkflowParameterFieldViewModel : ObservableObject
{
    private readonly Action<WorkflowParameterFieldViewModel> _onChanged;
    private string? _errorValue;
    private string? _effectiveContextValue;
    private bool _isUpdatingModel;
    private bool _isInitializing = true;

    public WorkflowParameterFieldViewModel(ParameterDescriptor descriptor, string? value,
        IEnumerable<WorkflowModelOption>? modelOptions, Action<WorkflowParameterFieldViewModel> onChanged)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(onChanged);
        Descriptor = descriptor;
        _onChanged = onChanged;
        Value = value ?? (descriptor.HasDefault ? descriptor.DefaultValue : "");
        foreach (WorkflowModelOption option in modelOptions ?? [])
        {
            ModelOptions.Add(option);
        }

        ModelProviders = new ObservableCollection<WorkflowModelProviderOption>(ModelOptions
            .GroupBy(option => option.ProviderId, StringComparer.Ordinal)
            .Select(group => new WorkflowModelProviderOption(group.First().ProviderLabel, group.Key)));
        if (IsModel && !string.IsNullOrWhiteSpace(Value))
        {
            _isUpdatingModel = true;
            try
            {
                ModelSelection selection = ModelSelectionCodec.Decode(Value);
                ModelProviderId = selection.ProviderId;
                ModelName = selection.Model;
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException)
            {
                ErrorMessage = "当前模型选择不可用。";
            }
            finally
            {
                _isUpdatingModel = false;
            }
        }

        if (IsModel && !string.IsNullOrWhiteSpace(ModelProviderId) &&
            ModelProviders.All(option => option.Value != ModelProviderId))
        {
            ModelProviders.Add(new WorkflowModelProviderOption(
                $"当前提供方不可用：{ModelProviderId}", ModelProviderId, true));
        }

        _isInitializing = false;
        ErrorMessage = ValidateValue();
    }

    public ParameterDescriptor Descriptor { get; }
    [ExcludeFromDerivedGeneration] public string Key => Descriptor.Key;
    [ExcludeFromDerivedGeneration] public string Label => Descriptor.Label;
    [ExcludeFromDerivedGeneration] public string Description => Descriptor.Description ?? "";
    [ExcludeFromDerivedGeneration] public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    [ExcludeFromDerivedGeneration] public string ContextBinding => Descriptor.ContextBinding ?? "";
    [ExcludeFromDerivedGeneration] public bool IsContextBound => !string.IsNullOrWhiteSpace(ContextBinding);
    [ExcludeFromDerivedGeneration] public bool IsBoolean => Descriptor.Type == WorkflowParameterValueType.Boolean;
    [ExcludeFromDerivedGeneration] public bool IsChoice => Descriptor.Type == WorkflowParameterValueType.Choice;
    [ExcludeFromDerivedGeneration] public bool IsModel => Descriptor.Type == WorkflowParameterValueType.Model;

    [ExcludeFromDerivedGeneration]
    public bool IsMultiline => Descriptor.Type is WorkflowParameterValueType.MultilineText or
        WorkflowParameterValueType.Documents or WorkflowParameterValueType.TextSelection;

    [ExcludeFromDerivedGeneration] public bool IsTextValue => !IsBoolean && !IsChoice && !IsModel;
    [ExcludeFromDerivedGeneration] public bool IsInteger => Descriptor.Type == WorkflowParameterValueType.Integer;
    [ExcludeFromDerivedGeneration] public bool IsDecimal => Descriptor.Type == WorkflowParameterValueType.Decimal;
    [ExcludeFromDerivedGeneration] public bool IsRequired => Descriptor.Required;

    [ExcludeFromDerivedGeneration]
    public string ContextSourceText => IsContextBound ? $"由当前上下文提供：{ContextBinding}" : "";

    [ExcludeFromDerivedGeneration] public string EffectiveContextText { get; private set; } = "";
    [ExcludeFromDerivedGeneration] public bool HasEffectiveContext => !string.IsNullOrWhiteSpace(EffectiveContextText);
    [ExcludeFromDerivedGeneration] public string ConstraintText => BuildConstraintText(Descriptor);
    [ExcludeFromDerivedGeneration] public bool HasConstraints => !string.IsNullOrWhiteSpace(ConstraintText);

    [ExcludeFromDerivedGeneration]
    public bool HasModelOptions => ModelOptions.Any(option =>
        option.ProviderId == ModelProviderId && !string.IsNullOrWhiteSpace(option.Model));

    public ObservableCollection<WorkflowModelOption> ModelOptions { get; } = [];
    public ObservableCollection<WorkflowModelProviderOption> ModelProviders { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BooleanValue))]
    [NotifyPropertyChangedFor(nameof(SelectedChoice))]
    [NotifyPropertyChangedFor(nameof(HasValue))]
    public partial string Value { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty] public partial bool IsEnabled { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedProvider))]
    [NotifyPropertyChangedFor(nameof(AvailableModels))]
    public partial string ModelProviderId { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedModelOption))]
    public partial string ModelName { get; set; } = "";

    [ExcludeFromDerivedGeneration] public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    [ExcludeFromDerivedGeneration] public bool HasValue => !string.IsNullOrWhiteSpace(Value);

    [ExcludeFromDerivedGeneration]
    public bool BooleanValue
    {
        get => bool.TryParse(Value, out bool result) && result;
        set => Value = value ? "true" : "false";
    }

    [ExcludeFromDerivedGeneration]
    public string? SelectedChoice
    {
        get => Descriptor.Choices?.Contains(Value, StringComparer.Ordinal) == true ? Value : null;
        set
        {
            if (value is not null)
            {
                Value = value;
            }
        }
    }

    [ExcludeFromDerivedGeneration]
    public WorkflowModelProviderOption? SelectedProvider
    {
        get => ModelProviders.FirstOrDefault(option =>
            string.Equals(option.Value, ModelProviderId, StringComparison.Ordinal));
        set
        {
            if (value is not null)
            {
                ModelProviderId = value.Value;
            }
        }
    }

    [ExcludeFromDerivedGeneration]
    public WorkflowModelOption? SelectedModelOption
    {
        get => ModelOptions.FirstOrDefault(option => option.ProviderId == ModelProviderId && option.Model == ModelName);
        set
        {
            if (value is not null)
            {
                ModelName = value.Model;
            }
        }
    }

    [ExcludeFromDerivedGeneration]
    public IEnumerable<WorkflowModelOption> AvailableModels => ModelOptions
        .Where(option => option.ProviderId == ModelProviderId);

    partial void OnValueChanged(string value)
    {
        if (!_isInitializing && !string.Equals(value, _errorValue, StringComparison.Ordinal))
        {
            ErrorMessage = ValidateValue();
            _errorValue = ErrorMessage is null ? null : value;
        }

        OnPropertyChanged(nameof(BooleanValue));
        OnPropertyChanged(nameof(SelectedChoice));
        if (IsModel && !_isUpdatingModel)
        {
            _isUpdatingModel = true;
            try
            {
                ModelSelection selected = ModelSelectionCodec.Decode(Value);
                ModelProviderId = selected.ProviderId;
                ModelName = selected.Model;
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException)
            {
                if (string.IsNullOrWhiteSpace(Value))
                {
                    ModelProviderId = "";
                    ModelName = "";
                }
            }
            finally
            {
                _isUpdatingModel = false;
            }
        }

        OnPropertyChanged(nameof(HasValue));
        if (!_isInitializing)
        {
            _onChanged(this);
        }
    }

    partial void OnModelProviderIdChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedProvider));
        OnPropertyChanged(nameof(AvailableModels));
        RefreshModelValue();
    }

    partial void OnModelNameChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedModelOption));
        RefreshModelValue();
    }

    private void RefreshModelValue()
    {
        if (!IsModel || _isUpdatingModel)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ModelProviderId) || string.IsNullOrWhiteSpace(ModelName))
        {
            Value = "";
            return;
        }

        _isUpdatingModel = true;
        try
        {
            Value = ModelSelectionCodec.Encode(new ModelSelection(ModelProviderId, ModelName));
        }
        finally
        {
            _isUpdatingModel = false;
        }
    }

    public void SetValidationError(string message)
    {
        ErrorMessage = message;
        _errorValue = Value;
    }

    public void SetEffectiveContext(string? value)
    {
        _effectiveContextValue = value;
        EffectiveContextText = string.IsNullOrWhiteSpace(value) ? "" : $"本次运行值：{value}";
        OnPropertyChanged(nameof(EffectiveContextText));
        OnPropertyChanged(nameof(HasEffectiveContext));
        ErrorMessage = ValidateValue();
    }

    private string? ValidateValue()
    {
        if (IsContextBound && _effectiveContextValue is null)
        {
            return null;
        }

        string validationValue = IsContextBound ? _effectiveContextValue ?? "" : Value;
        if (Descriptor.Required && string.IsNullOrWhiteSpace(validationValue))
        {
            return "此字段为必填项。";
        }

        if (string.IsNullOrWhiteSpace(validationValue))
        {
            return null;
        }

        if (IsInteger)
        {
            if (!int.TryParse(validationValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer))
            {
                return "请输入有效整数。";
            }

            if ((Descriptor.Minimum.HasValue && integer < Descriptor.Minimum.Value) ||
                (Descriptor.Maximum.HasValue && integer > Descriptor.Maximum.Value))
            {
                return ConstraintText;
            }
        }
        else if (IsDecimal)
        {
            if (!decimal.TryParse(validationValue, NumberStyles.Number, CultureInfo.InvariantCulture,
                    out decimal number))
            {
                return "请输入有效数字。";
            }

            if ((Descriptor.Minimum.HasValue && number < Descriptor.Minimum.Value) ||
                (Descriptor.Maximum.HasValue && number > Descriptor.Maximum.Value))
            {
                return ConstraintText;
            }
        }
        else if (IsChoice && Descriptor.Choices?.Contains(validationValue, StringComparer.Ordinal) != true)
        {
            return "请选择一个有效选项。";
        }
        else if (IsModel)
        {
            try
            {
                ModelSelection selected = ModelSelectionCodec.Decode(validationValue);
                if (ModelProviders.FirstOrDefault(option => option.Value == selected.ProviderId)?.IsUnavailable == true)
                {
                    return "当前模型提供方不可用，请选择有效提供方。";
                }
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException)
            {
                return "请选择提供方并填写模型 ID。";
            }
        }

        return null;
    }

    private static string BuildConstraintText(ParameterDescriptor descriptor)
    {
        List<string> constraints = [];
        if (descriptor.Required)
        {
            constraints.Add("必填");
        }

        if (descriptor.Minimum.HasValue || descriptor.Maximum.HasValue)
        {
            string min = descriptor.Minimum.HasValue
                ? descriptor.Minimum.Value.ToString(CultureInfo.InvariantCulture)
                : "−∞";
            string max = descriptor.Maximum.HasValue
                ? descriptor.Maximum.Value.ToString(CultureInfo.InvariantCulture)
                : "+∞";
            constraints.Add($"范围 {min}–{max}");
        }

        return string.Join(" · ", constraints);
    }
}

public sealed record WorkflowModelOption(string ProviderLabel, string ProviderId, string Model)
{
    public string Label => Model;
    public string Value => ModelSelectionCodec.Encode(new ModelSelection(ProviderId, Model));
}

public sealed record WorkflowModelProviderOption(string Label, string Value, bool IsUnavailable = false);
