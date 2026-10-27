using FluentAssertions;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Tests;

public sealed class ExtensionAdditiveCompatibilityTests
{
    [Fact]
    public void Existing_message_and_request_constructor_signatures_remain_available()
    {
        typeof(LlmMessage).GetConstructor([typeof(string), typeof(string)])
            .Should().NotBeNull("image attachments must not replace the existing text-message constructor");
        typeof(LlmRequest).GetConstructor([
                typeof(IReadOnlyList<LlmMessage>), typeof(string), typeof(decimal?), typeof(int?), typeof(string)
            ])
            .Should().NotBeNull("OCR domain and reasoning options are optional additions, not new mandatory arguments");

        LlmMessage message = new("user", "Existing text-only request");
        LlmRequest request = new([message]);
        message.Images.Should().BeNull();
        request.Domain.Should().BeNull();
        request.ReasoningEffort.Should().BeNull();
    }

    [Fact]
    public void Existing_configuration_fields_need_no_managed_file_declaration()
    {
        ConfigFieldDescriptor field = new("existing-key", "Existing field", ConfigValueKind.Text, false, "");

        field.ManagedFiles.Should().BeEmpty();
        ((int)ConfigValueKind.CredentialReference).Should().Be(10);
        ((int)ConfigValueKind.FilePath).Should().BeGreaterThan((int)ConfigValueKind.CredentialReference);
        ((int)ConfigValueKind.DirectoryPath).Should().BeGreaterThan((int)ConfigValueKind.CredentialReference);
    }

    [Fact]
    public void Existing_extension_kinds_keep_their_identity_and_do_not_inherit_ocr()
    {
        ((int)ExtensionKind.Translation).Should().Be(0);
        ((int)ExtensionKind.Metadata).Should().Be(1);
        ((int)ExtensionKind.Retrieval).Should().Be(3);
        ((int)ExtensionKind.PdfTextLayer).Should().BeGreaterThan((int)ExtensionKind.Retrieval);

        Type[] existingContracts =
            [typeof(ITranslationExtension), typeof(IMetadataExtension), typeof(IRetrievalExtension)];
        existingContracts.Should().OnlyContain(contract => !typeof(IOcrExtension).IsAssignableFrom(contract));
        typeof(IExtensionLease).GetProperty(nameof(IExtensionLease.PdfTextLayer))!
            .GetMethod!.IsAbstract.Should()
            .BeFalse("the new optional provider member must have a default implementation");
    }
}
