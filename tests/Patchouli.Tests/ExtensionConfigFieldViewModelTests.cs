using System.Text.Json;
using FluentAssertions;
using Patchouli.Extensions.Contracts;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.Tests;

public sealed class ExtensionConfigFieldViewModelTests
{
    [Fact]
    public void Required_numeric_fields_validate_integer_and_declared_range()
    {
        ExtensionConfigFieldViewModel field = new(
            new ConfigFieldDescriptor("batch-size", "Batch size", ConfigValueKind.Integer, true, "", null, 1, 8),
            "", []);

        field.Validate().Should().NotBeNull();
        field.Value = "1.5";
        field.Validate().Should().NotBeNull();
        field.Value = "9";
        field.Validate().Should().NotBeNull();
        field.Value = "4";
        field.Validate().Should().BeNull();
    }

    [Fact]
    public void Boolean_and_model_fields_keep_typed_values_in_the_config_snapshot()
    {
        ExtensionConfigFieldViewModel boolean = new(
            new ConfigFieldDescriptor("enabled", "Enabled", ConfigValueKind.Boolean, false, "", "false"),
            "false", []);
        ExtensionConfigFieldViewModel model = new(
            new ConfigFieldDescriptor("model", "Translation model", ConfigValueKind.Model, false, ""),
            "", ["openai", "local"]);

        boolean.BooleanValue.Should().BeFalse();
        boolean.BooleanValue = true;
        boolean.Value.Should().Be("true");
        boolean.Validate().Should().BeNull();

        model.Providers.Should().ContainInOrder("", "openai", "local");
        model.ModelProvider = "openai";
        model.ModelName = "gpt-translation";
        using JsonDocument saved = JsonDocument.Parse(model.Value);
        saved.RootElement.GetProperty("providerId").GetString().Should().Be("openai");
        saved.RootElement.GetProperty("model").GetString().Should().Be("gpt-translation");
        model.ModelProvider.Should().Be("openai");
        model.ModelName.Should().Be("gpt-translation");
    }
}
