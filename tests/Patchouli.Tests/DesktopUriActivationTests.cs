using FluentAssertions;
using Patchouli.UI;
using Patchouli.UI.Services;

namespace Patchouli.Tests;

public sealed class DesktopUriActivationTests
{
    [Theory]
    [InlineData("patchouli://")]
    [InlineData("patchouli://library.yml")]
    [InlineData("patchouli://items/00000000-0000-0000-0000-000000000001.yml")]
    [InlineData(
        "patchouli://texts/00000000-0000-0000-0000-000000000001/page-2.xml?rev=00000000-0000-0000-0000-000000000002&box=00000000-0000-0000-0000-000000000003")]
    public void Startup_accepts_existing_resource_addresses(string uri)
    {
        DesktopUriActivation.FromArguments([uri]).Should().Be(uri);
        DesktopUriActivation.FromArguments(["--open-uri", uri]).Should().Be(uri);
    }

    [Theory]
    [InlineData("file:///C:/anything")]
    [InlineData("https://example.org")]
    [InlineData("patchouli://texts/invalid/page-0.xml")]
    [InlineData("patchouli://\n")]
    public void Activation_rejects_other_schemes_and_invalid_addresses(string uri)
    {
        DesktopUriActivation.IsValid(uri).Should().BeFalse();
    }

    [Fact]
    public void Startup_rejects_multiple_links()
    {
        Action parse = () => DesktopUriActivation.FromArguments(["patchouli://", "patchouli://items/"]);
        parse.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("--headless")]
    [InlineData("--db")]
    public void Protocol_entry_point_rejects_additional_process_options(string option)
    {
        Action parse = () => DesktopUriActivation.FromArguments(["--open-uri", "patchouli://", option]);
        parse.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Secondary_forwards_exact_uri_and_retains_pre_window_links_in_order()
    {
        string suffix = Guid.NewGuid().ToString("N");
        DesktopInstanceCoordinatorOptions options = new($"patchouli-test-{suffix}", $"pt.{suffix[..16]}");
        await using DesktopInstanceCoordinator primary = new(options);
        primary.StartListener();
        await using DesktopInstanceCoordinator secondary = new(options);
        const string first =
            "patchouli://texts/00000000-0000-0000-0000-000000000001/page-2.xml?rev=00000000-0000-0000-0000-000000000002&box=00000000-0000-0000-0000-000000000003";
        (await secondary.NotifyPrimaryUriAsync(first)).Should().BeTrue();
        (await secondary.NotifyPrimaryUriAsync("patchouli://library.yml")).Should().BeTrue();
        List<string> received = [];
        using (primary.SubscribeUris(received.Add))
        {
            received.Should().Equal(first, "patchouli://library.yml");
        }

        using IDisposable subscription = primary.SubscribeUris(received.Add);
        received.Count.Should().Be(2);
        (await secondary.NotifyPrimaryUriAsync("patchouli://items/")).Should().BeTrue();
        received.Should().Equal(first, "patchouli://library.yml", "patchouli://items/");
        (await secondary.NotifyPrimaryUriAsync("https://example.org")).Should().BeFalse();
        received.Count.Should().Be(3);
    }
}
