using FluentAssertions;
using Patchouli.UI.Services;

namespace Patchouli.Tests;

public sealed class LinkNavigatorTests
{
    [Theory]
    [InlineData("patchouli://items/00000000-0000-0000-0000-000000000001.bib")]
    [InlineData("patchouli://texts/00000000-0000-0000-0000-000000000001/")]
    [InlineData(
        "patchouli://texts/00000000-0000-0000-0000-000000000001/page-2.md?rev=00000000-0000-0000-0000-000000000002&box=00000000-0000-0000-0000-000000000003")]
    [InlineData("patchouli://csl-styles/apa.csl")]
    public async Task Library_links_preserve_query_parameters_and_use_internal_navigation(string target)
    {
        string? navigated = null;
        LinkNavigator navigator = CreateNavigator(uri =>
        {
            navigated = uri;
            return Task.CompletedTask;
        });

        (await navigator.NavigateAsync(target)).Should().BeTrue();
        navigated.Should().Be(target);
        PatchouliUriNavigationParser.ParseInput(navigated).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("https://example.org/paper?doi=10.1000/test")]
    [InlineData("http://example.org/paper")]
    public async Task Confirmed_web_links_are_opened_by_the_desktop_launcher(string target)
    {
        Uri? confirmed = null;
        Uri? launched = null;
        LinkNavigator navigator = CreateNavigator(
            confirmExternal: uri =>
            {
                confirmed = uri;
                return Task.FromResult(true);
            },
            openExternal: uri =>
            {
                launched = uri;
                return Task.FromResult(true);
            });

        (await navigator.NavigateAsync(target)).Should().BeTrue();
        confirmed!.AbsoluteUri.Should().Be(target);
        launched!.AbsoluteUri.Should().Be(target);
    }

    [Fact]
    public async Task Declining_the_confirmation_never_opens_the_link()
    {
        bool opened = false;
        LinkNavigator navigator = CreateNavigator(
            confirmExternal: _ => Task.FromResult(false),
            openExternal: _ =>
            {
                opened = true;
                return Task.FromResult(true);
            });

        (await navigator.NavigateAsync("https://example.org/paper")).Should().BeFalse();
        opened.Should().BeFalse();
    }

    [Fact]
    public async Task A_launcher_returning_false_reports_the_failure()
    {
        Uri? failure = null;
        LinkNavigator navigator = CreateNavigator(
            openExternal: _ => Task.FromResult(false),
            reportOpenFailure: uri =>
            {
                failure = uri;
                return Task.CompletedTask;
            });

        (await navigator.NavigateAsync("https://example.org/paper")).Should().BeFalse();
        failure!.AbsoluteUri.Should().Be("https://example.org/paper");
    }

    [Fact]
    public async Task A_throwing_launcher_reports_the_failure()
    {
        Uri? failure = null;
        LinkNavigator navigator = CreateNavigator(
            openExternal: _ => throw new InvalidOperationException("No handler."),
            reportOpenFailure: uri =>
            {
                failure = uri;
                return Task.CompletedTask;
            });

        (await navigator.NavigateAsync("https://example.org/paper")).Should().BeFalse();
        failure.Should().NotBeNull();
    }

    [Theory]
    [InlineData("../relative")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Invalid_addresses_are_rejected_without_navigation(string? target)
    {
        string? rejected = null;
        LinkNavigator navigator = CreateNavigator(
            reportRejected: url =>
            {
                rejected = url;
                return Task.CompletedTask;
            });

        (await navigator.NavigateAsync(target)).Should().BeFalse();
        rejected.Should().Be(target ?? string.Empty);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("ftp://example.org/file")]
    public async Task Unsupported_schemes_are_rejected_without_confirmation_or_launch(string target)
    {
        string? rejected = null;
        bool confirmed = false;
        bool opened = false;
        LinkNavigator navigator = CreateNavigator(
            confirmExternal: _ =>
            {
                confirmed = true;
                return Task.FromResult(true);
            },
            openExternal: _ =>
            {
                opened = true;
                return Task.FromResult(true);
            },
            reportRejected: url =>
            {
                rejected = url;
                return Task.CompletedTask;
            });

        (await navigator.NavigateAsync(target)).Should().BeFalse();
        rejected.Should().NotBeNull();
        confirmed.Should().BeFalse();
        opened.Should().BeFalse();
    }

    [Fact]
    public async Task A_single_activation_never_prompts_or_opens_twice()
    {
        int confirmCalls = 0;
        int openCalls = 0;
        TaskCompletionSource<bool> confirmation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        LinkNavigator navigator = CreateNavigator(
            confirmExternal: _ =>
            {
                Interlocked.Increment(ref confirmCalls);
                return confirmation.Task;
            },
            openExternal: _ =>
            {
                Interlocked.Increment(ref openCalls);
                return Task.FromResult(true);
            });

        Task<bool> first = navigator.NavigateAsync("https://example.org/paper");
        Task<bool> second = navigator.NavigateAsync("https://example.org/paper");

        (await second).Should().BeFalse();
        confirmation.SetResult(true);
        (await first).Should().BeTrue();
        confirmCalls.Should().Be(1);
        openCalls.Should().Be(1);
    }

    [Fact]
    public async Task Sequential_activations_are_processed_independently()
    {
        int openCalls = 0;
        LinkNavigator navigator = CreateNavigator(
            openExternal: _ =>
            {
                Interlocked.Increment(ref openCalls);
                return Task.FromResult(true);
            });

        (await navigator.NavigateAsync("https://example.org/a")).Should().BeTrue();
        (await navigator.NavigateAsync("https://example.org/b")).Should().BeTrue();
        openCalls.Should().Be(2);
    }

    private static LinkNavigator CreateNavigator(
        Func<string, Task>? navigateInternal = null,
        Func<Uri, Task<bool>>? confirmExternal = null,
        Func<Uri, Task<bool>>? openExternal = null,
        Func<Uri, Task>? reportOpenFailure = null,
        Func<string, Task>? reportRejected = null)
    {
        return new LinkNavigator(
            navigateInternal ?? (_ => throw new InvalidOperationException("Unexpected library navigation.")),
            confirmExternal ?? (_ => Task.FromResult(true)),
            openExternal ?? (_ => throw new InvalidOperationException("Unexpected launch.")),
            reportOpenFailure ?? (_ => Task.CompletedTask),
            reportRejected ?? (_ => Task.CompletedTask));
    }
}
