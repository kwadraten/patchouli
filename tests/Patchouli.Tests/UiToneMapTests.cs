using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Host.Mcp;
using Patchouli.UI.Themes;

namespace Patchouli.Tests;

public sealed class UiToneMapTests
{
    [Fact]
    public void Box_types_map_to_their_category_tones()
    {
        UiToneMap.ForBoxType(DocumentBoxType.Title).Should().Be(UiTone.Heading);
        UiToneMap.ForBoxType(DocumentBoxType.Text).Should().Be(UiTone.Body);
        UiToneMap.ForBoxType(DocumentBoxType.Table).Should().Be(UiTone.Table);
        UiToneMap.ForBoxType(DocumentBoxType.Image).Should().Be(UiTone.Media);
        UiToneMap.ForBoxType(DocumentBoxType.Equation).Should().Be(UiTone.Equation);
        UiToneMap.ForBoxType(DocumentBoxType.LogicalPage).Should().Be(UiTone.LogicalPage);
        UiToneMap.ForBoxType("unrecognized").Should().Be(UiTone.Neutral);
    }

    [Fact]
    public void Suppression_overrides_the_box_type_tone()
    {
        UiToneMap.ForVisualBox(DocumentBoxType.Text, true).Should().Be(UiTone.Neutral);
        UiToneMap.ForVisualBox(DocumentBoxType.Text, false).Should().Be(UiTone.Body);
    }

    [Fact]
    public void Mcp_host_status_maps_to_semantic_tones()
    {
        UiToneMap.ForMcpHostStatus(McpServerHostStatus.Running).Should().Be(UiTone.Success);
        UiToneMap.ForMcpHostStatus(McpServerHostStatus.Starting).Should().Be(UiTone.Warning);
        UiToneMap.ForMcpHostStatus(McpServerHostStatus.Error).Should().Be(UiTone.Danger);
        UiToneMap.ForMcpHostStatus(McpServerHostStatus.Stopped).Should().Be(UiTone.Neutral);
    }
}
