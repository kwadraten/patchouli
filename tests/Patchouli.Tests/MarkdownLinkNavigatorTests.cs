using FluentAssertions;
using Patchouli.UI.Services;

namespace Patchouli.Tests;

public sealed class MarkdownLinkNavigatorTests
{
    [Theory]
    [InlineData("patchouli://items/00000000-0000-0000-0000-000000000001.bib")]
    [InlineData("patchouli://texts/00000000-0000-0000-0000-000000000001/")]
    [InlineData(
        "patchouli://texts/00000000-0000-0000-0000-000000000001/page-2.md?rev=00000000-0000-0000-0000-000000000002&box=00000000-0000-0000-0000-000000000003")]
    [InlineData("patchouli://csl-styles/apa.csl")]
    public async Task Library_links_preserve_the_target_and_use_internal_navigation(string target)
    {
        string? navigated = null;
        MarkdownLinkNavigator navigator = new(uri =>
        {
            navigated = uri;
            return Task.CompletedTask;
        }, _ => throw new InvalidOperationException("Library links must stay in the app."));

        (await navigator.NavigateAsync(new Uri(target))).Should().BeTrue();
        navigated.Should().Be(target);
        PatchouliUriNavigationParser.ParseInput(navigated).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("https://example.org/paper?doi=10.1000/test")]
    [InlineData("http://example.org/paper")]
    public async Task Web_links_use_the_desktop_launcher(string target)
    {
        Uri? launched = null;
        MarkdownLinkNavigator navigator = new(
            _ => throw new InvalidOperationException("Unexpected library navigation."),
            uri =>
            {
                launched = uri;
                return Task.FromResult(true);
            });

        (await navigator.NavigateAsync(new Uri(target))).Should().BeTrue();
        launched!.AbsoluteUri.Should().Be(target);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("../relative")]
    public async Task Unsupported_links_do_not_launch_or_navigate(string target)
    {
        MarkdownLinkNavigator navigator = new(_ => throw new InvalidOperationException("Unexpected navigation."),
            _ => throw new InvalidOperationException("Unexpected launch."));

        (await navigator.NavigateAsync(new Uri(target, UriKind.RelativeOrAbsolute))).Should().BeFalse();
    }
}
