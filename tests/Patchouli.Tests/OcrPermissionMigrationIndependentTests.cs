using FluentAssertions;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Mcp;

namespace Patchouli.Tests;

public sealed class OcrPermissionMigrationIndependentTests
{
    [Fact]
    public void Parent_and_old_action_grants_cannot_authorize_subdomains()
    {
        McpServerSettings settings = McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow) with
        {
            DomainPermissions =
            [
                new McpDomainPermission("runs", "send", true), new McpDomainPermission("texts", "ocr.start", true),
                new McpDomainPermission("runs", "ocr.cancel", true)
            ]
        };
        McpPermissionPolicy.Allows(settings, "send", "runs/ocr/control").Should().BeFalse();
        McpPermissionPolicy.Allows(settings, "ocr.start", "texts/document/").Should().BeFalse();
        McpPermissionPolicy.Verbs.Should().NotContain(["ocr.start", "ocr.cancel", "ocr.retry", "translation.start"]);
    }

    [Fact]
    public void Subdomain_grants_and_global_switch_are_independent()
    {
        McpServerSettings settings = McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow) with
        {
            DomainPermissions = [new McpDomainPermission("runs/ocr", "send", true)]
        };
        McpPermissionPolicy.Allows(settings, "send", "runs/ocr/control").Should().BeTrue();
        McpPermissionPolicy.Allows(settings, "send", "./runs/translation/control").Should().BeFalse();
        McpPermissionPolicy.Allows(settings, "send", "runs/ocr-other/control").Should().BeFalse();
        McpPermissionPolicy.Allows(settings, "send", "runs/ocr/../translation/control").Should().BeFalse();
        McpPermissionPolicy.Allows(settings with { ToolOverrides = [new McpToolOverride("patchouli.send", false)] },
            "send", "runs/ocr/control").Should().BeFalse();
        McpPermissionPolicy.Allows(settings, "fetch", "runs/ocr/task/status").Should().BeTrue();
    }

    [Fact]
    public async Task Migration_drops_old_grants_and_persists_explicit_subdomain_choices()
    {
        using TemporaryAppSettingsFile file = new();
        await File.WriteAllTextAsync(file.Path,
            """{"Mcp":{"Port":4540,"DomainPermissions":[{"Domain":"runs","Verb":"send","Enabled":true},{"Domain":"texts","Verb":"ocr.start","Enabled":true},{"Domain":"runs","Verb":"ocr.cancel","Enabled":true},{"Domain":"texts","Verb":"put","Enabled":true}]}}""");
        McpServerSettingsService service = new(file.Path, new FixedClock(DateTimeOffset.UtcNow));
        Result<McpServerSettings> loaded = await service.GetSettingsAsync();
        loaded.IsSuccess.Should().BeTrue(loaded.ErrorMessage);
        loaded.Value.DomainPermissions.Should().NotContain(value => value.Domain == "runs" && value.Verb == "send");
        loaded.Value.DomainPermissions.Should().NotContain(value => value.Verb.Contains('.'));
        foreach (string domain in McpPermissionPolicy.RunSendDomains)
        {
            loaded.Value.DomainPermissions.Should().Contain(new McpDomainPermission(domain, "send", false));
        }

        Result<McpServerSettings> saved = await service.SaveSettingsAsync(loaded.Value with
        {
            DomainPermissions =
            [
                new McpDomainPermission("runs/ocr", "send", true),
                new McpDomainPermission("runs/translation", "send", false)
            ]
        });
        saved.IsSuccess.Should().BeTrue(saved.ErrorMessage);
        Result<McpServerSettings> reloaded = await service.GetSettingsAsync();
        McpPermissionPolicy.Allows(reloaded.Value, "send", "runs/ocr/control").Should().BeTrue();
        McpPermissionPolicy.Allows(reloaded.Value, "send", "runs/translation/control").Should().BeFalse();
    }

    [Fact]
    public void Migration_removes_parent_even_when_both_children_and_tool_markers_exist()
    {
        McpServerSettings settings = McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow) with
        {
            ToolOverrides = [new McpToolOverride("patchouli.add", true), new McpToolOverride("patchouli.view", true)],
            DomainPermissions =
            [
                new McpDomainPermission("runs", "send", true), new McpDomainPermission("runs/ocr", "send", true),
                new McpDomainPermission("runs/translation", "send", true)
            ]
        };
        McpServerSettings migrated = McpServerSettingsService.NormalizeLegacySettings(settings);
        migrated.DomainPermissions.Should().NotContain(value => value.Domain == "runs" && value.Verb == "send");
        migrated.DomainPermissions.Should().HaveCount(3);
        migrated.DomainPermissions.Should().Contain(new McpDomainPermission("runs/ocr", "send", true));
        migrated.DomainPermissions.Should().Contain(new McpDomainPermission("runs/translation", "send", true));
        migrated.DomainPermissions.Should().Contain(new McpDomainPermission("runs/metadata", "send", false));
        migrated.DomainPermissions.Should().NotContain(value => value.Domain == "runs" && value.Verb == "send");
    }
}
