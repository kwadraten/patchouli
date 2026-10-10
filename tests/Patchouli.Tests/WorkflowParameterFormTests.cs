using FluentAssertions;
using Patchouli.UI.ViewModels.Settings;
using Patchouli.Workflows.Scripting;

namespace Patchouli.Tests;

public sealed class WorkflowParameterFormTests
{
    [Fact]
    public void Numeric_validation_tracks_the_current_value_and_keeps_invalid_values_marked()
    {
        int changed = 0;
        WorkflowParameterFieldViewModel field = new(Descriptor("radius", WorkflowParameterValueType.Integer,
            minimum: 0, maximum: 5), "3", null, _ => changed++);

        field.HasError.Should().BeFalse();
        field.Value = "99";
        field.HasError.Should().BeTrue();
        field.ErrorMessage.Should().Contain("范围");
        field.SetValidationError("宿主拒绝了当前值。");
        field.ErrorMessage.Should().Contain("宿主拒绝了");
        field.Value = "100";
        field.ErrorMessage.Should().Contain("范围");
        field.Value = "4";
        field.HasError.Should().BeFalse();
        changed.Should().Be(3);
    }

    [Fact]
    public void Model_selection_allows_an_api_model_id_and_preserves_unavailable_provider()
    {
        WorkflowModelOption catalogModel = new("示例提供方", "provider-a", "catalog-model");
        WorkflowParameterFieldViewModel field = new(Descriptor("model", WorkflowParameterValueType.Model,
                true), ModelSelectionCodec.Encode(new ModelSelection("retired-provider", "old-model")),
            [catalogModel], _ => { });

        field.ModelProviders.Should().Contain(option => option.Value == "retired-provider" && option.IsUnavailable);
        field.ErrorMessage.Should().Contain("不可用");
        field.ModelProviderId = "provider-a";
        field.ModelName = "api-model-not-in-catalog";

        ModelSelection selected = ModelSelectionCodec.Decode(field.Value);
        selected.ProviderId.Should().Be("provider-a");
        selected.Model.Should().Be("api-model-not-in-catalog");
        field.HasError.Should().BeFalse();
    }

    [Fact]
    public void Effective_context_is_displayed_without_replacing_the_saved_field_value()
    {
        WorkflowParameterFieldViewModel field = new(Descriptor("documents", WorkflowParameterValueType.Documents,
                true, contextBinding: "documents"),
            "saved-document", null, _ => { });

        field.SetEffectiveContext("[\"current-document\"]");

        field.Value.Should().Be("saved-document");
        field.EffectiveContextText.Should().Contain("current-document");
        field.HasEffectiveContext.Should().BeTrue();
    }

    private static ParameterDescriptor Descriptor(string key, WorkflowParameterValueType type,
        bool required = false, decimal? minimum = null, decimal? maximum = null, string contextBinding = "")
    {
        return new ParameterDescriptor
        {
            Key = key,
            Label = key,
            Type = type,
            Required = required,
            Description = "",
            HasDefault = false,
            DefaultValue = "",
            Minimum = minimum,
            Maximum = maximum,
            Choices = [],
            ContextBinding = contextBinding,
            SourceLine = 1,
            SourceColumn = 1
        };
    }
}
