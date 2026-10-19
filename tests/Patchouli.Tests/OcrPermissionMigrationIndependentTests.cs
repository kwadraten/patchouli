using FluentAssertions;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Mcp;

namespace Patchouli.Tests;

public sealed class OcrPermissionMigrationIndependentTests
{
    [Fact]
    public void New_profiles_require_each_explicit_target_action_grant()
    {
        McpServerSettings settings = McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow);
        McpPermissionPolicy.Allows(settings, "ocr.start", "patchouli://texts/document/").Should().BeFalse();
        McpPermissionPolicy.Allows(settings, "ocr.cancel", "patchouli://runs/ocr/task/").Should().BeFalse();
        McpPermissionPolicy.Allows(settings, "ocr.retry", "patchouli://runs/ocr/task/").Should().BeFalse();
        McpPermissionPolicy.Verbs.Should().NotContain("ocr");
        McpPermissionPolicy.Verbs.Should().Contain(["ocr.start", "ocr.cancel", "ocr.retry"]);
        settings.ToolOverrides.Should().NotContain(value => value.ToolName == "patchouli.ocr");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_send_workflow_and_ocr_grants_never_promote_to_new_action_grants(bool oldOcrEnabled)
    {
        using TemporaryAppSettingsFile file = new();
        string enabled = oldOcrEnabled ? "true" : "false";
        await File.WriteAllTextAsync(file.Path,
            """{"Mcp":{"Port":4540,"BindAddress":"127.0.0.1","AuthRequired":false,"ToolOverrides":[{"ToolName":"patchouli.send","Enabled":true},{"ToolName":"patchouli.ocr","Enabled":"""
            + enabled +
            """}],"DomainPermissions":[{"Domain":"texts","Verb":"put","Enabled":true},{"Domain":"texts","Verb":"send","Enabled":true},{"Domain":"runs","Verb":"send","Enabled":true},{"Domain":"workflows","Verb":"send","Enabled":true},{"Domain":"texts","Verb":"ocr","Enabled":true},{"Domain":"runs","Verb":"ocr","Enabled":true}]}}""");
        McpServerSettingsService service = new(file.Path, new FixedClock(DateTimeOffset.UtcNow));
        Result<McpServerSettings> loaded = await service.GetSettingsAsync();
        loaded.IsSuccess.Should().BeTrue(loaded.ErrorMessage);
        McpPermissionPolicy.Allows(loaded.Value, "put", "patchouli://texts/document/page-1.xml").Should().BeTrue();
        McpPermissionPolicy.Allows(loaded.Value, "ocr.start", "patchouli://texts/document/").Should().BeFalse();
        McpPermissionPolicy.Allows(loaded.Value, "ocr.cancel", "patchouli://runs/ocr/task/").Should().BeFalse();
        McpPermissionPolicy.Allows(loaded.Value, "ocr.retry", "patchouli://runs/ocr/task/").Should().BeFalse();
        loaded.Value.DomainPermissions.Should()
            .Contain(value => value.Domain == "texts" && value.Verb == "ocr.start" && !value.Enabled);
        loaded.Value.DomainPermissions.Should()
            .Contain(value => value.Domain == "runs" && value.Verb == "ocr.cancel" && !value.Enabled);
        loaded.Value.DomainPermissions.Should()
            .Contain(value => value.Domain == "runs" && value.Verb == "ocr.retry" && !value.Enabled);
        loaded.Value.ToolOverrides.Should().NotContain(value => value.ToolName == "patchouli.ocr");
    }

    [Fact]
    public void Send_switch_and_each_action_grant_are_independent()
    {
        McpServerSettings startOnly = new(4540, "127.0.0.1", false, [], false, null,
            [new McpToolOverride("patchouli.send", true)], DateTimeOffset.UtcNow)
        {
            DomainPermissions = [new McpDomainPermission("texts", "ocr.start", true)]
        };
        McpPermissionPolicy.Allows(startOnly, "ocr.start", "patchouli://texts/document/").Should().BeTrue();
        McpPermissionPolicy.Allows(startOnly, "ocr.cancel", "patchouli://runs/ocr/task/").Should().BeFalse();
        McpPermissionPolicy.Allows(startOnly, "ocr.retry", "patchouli://runs/ocr/task/").Should().BeFalse();
        McpServerSettings sendOff = startOnly with { ToolOverrides = [new McpToolOverride("patchouli.send", false)] };
        McpPermissionPolicy.Allows(sendOff, "ocr.start", "patchouli://texts/document/").Should().BeFalse();
        McpPermissionPolicy.Allows(sendOff, "cite", "patchouli://texts/document/page-1.xml").Should().BeTrue();
    }

    [Theory]
    [InlineData("texts", "ocr.start", true)]
    [InlineData("texts", "ocr.cancel", false)]
    [InlineData("texts", "ocr.retry", false)]
    [InlineData("runs", "ocr.start", false)]
    [InlineData("runs", "ocr.cancel", true)]
    [InlineData("runs", "ocr.retry", true)]
    [InlineData("items", "ocr.start", false)]
    [InlineData("workflows", "ocr.start", false)]
    [InlineData("translations", "ocr.start", false)]
    [InlineData("csl-styles", "ocr.start", false)]
    [InlineData("texts", "ocr", false)]
    public void Capability_matrix_exposes_only_supported_target_action_combinations(string domain, string action,
        bool expected)
    {
        McpPermissionPolicy.Supports(domain, action).Should().Be(expected);
    }
}
