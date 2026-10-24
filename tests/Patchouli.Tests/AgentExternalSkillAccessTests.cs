using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Csl;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Host.Lifecycle;
using Patchouli.Host.Mcp;
using Patchouli.Mcp;
using Patchouli.UI;

namespace Patchouli.Tests;

/// <summary>External agents can use the real CLI outside the managed workspace and PTY environment.</summary>
public sealed class AgentExternalSkillAccessTests
{
    [Fact]
    public async Task External_working_directory_can_find_fetch_and_cite_without_managed_agent_environment()
    {
        using AgentTestDirectory directory = new();
        string libraryDirectory = directory.Combine("library");
        string database = Path.Combine(libraryDirectory, "runtime.sqlite");
        string externalDirectory = directory.Combine("external-project");
        Directory.CreateDirectory(externalDirectory);
        PatchouliAppSettings defaults = PatchouliAppSettings.Default();
        PatchouliAppSettings settings = defaults with
        {
            Runtime = defaults.Runtime with
            {
                RuntimeDatabasePath = database,
                DefaultSyncRoot = directory.Combine("sync"),
                DefaultStagingRoot = directory.Combine("staging"),
                LogDirectory = directory.Combine("logs")
            }
        };
        HostServices services = await HostServices.CreateAsync(database, settings, directory.Combine("settings.json"));
        try
        {
            (await services.Library.CreateLibraryAsync("External Agent Skill Access")).IsSuccess.Should().BeTrue();
            const string title = "External Agent Skill Library Access";
            Result<ItemMetadata> created = await services.Items.CreateItemAsync(new CreateItemRequest(
                "book", title,
                Creators: [new ItemCreatorInput(ItemCreatorRoles.Author, "Reader", "External")]));
            created.IsSuccess.Should().BeTrue(created.ErrorMessage);
            (await services.CslStore.InstallStyleAsync(
                    new CslCatalogStyle("external-agent", "External Agent", null, "test"), StyleXml))
                .IsSuccess.Should().BeTrue();

            ConcurrentQueue<Exception> failures = new();
            await using McpServerHost host = new(services, (exception, _, _) => failures.Enqueue(exception));
            McpServerSettings mcp = new(RuntimeHostCoordinator.ReserveEphemeralLoopbackPort(),
                "127.0.0.1", false, [], false, null, [], DateTimeOffset.UtcNow);
            await host.StartAsync(mcp);
            host.IsRunning.Should().BeTrue(host.StatusDetail);

            using JsonDocument found = JsonDocument.Parse(await RunCliAsync(externalDirectory, host.Endpoint,
                "find", title, "--in", "patchouli://items/", "--literal"));
            JsonElement[] entries = found.RootElement.GetProperty("entries").EnumerateArray().ToArray();
            entries.Should().ContainSingle();
            string uri = entries[0].GetProperty("uri").GetString()!;
            uri.Should().Be(McpResourceUris.ItemUri(created.Value.ItemId));

            using JsonDocument fetched = JsonDocument.Parse(await RunCliAsync(externalDirectory, host.Endpoint,
                "fetch", uri));
            fetched.RootElement.GetProperty("entries")[0].GetProperty("content").GetString()
                .Should().Contain(title);

            using JsonDocument cited = JsonDocument.Parse(await RunCliAsync(externalDirectory, host.Endpoint,
                "cite", uri, "--style", "patchouli://csl-styles/external-agent.csl", "--bibliography"));
            cited.RootElement.GetProperty("entries")[0].GetProperty("citation").GetString()
                .Should().Contain(title);
            cited.RootElement.GetProperty("meta").GetProperty("bibliography").GetString()
                .Should().Contain(title);
            failures.Should().BeEmpty();
            Directory.Exists(services.AgentWorkspace.GetWorkspaceDirectory()).Should().BeFalse(
                "external CLI access must not depend on creating a managed Agent workspace");
        }
        finally
        {
            await services.ShutdownAsync();
            SqliteTestCleanup.ReleasePoolsInDirectory(directory.Path);
        }
    }

    private static async Task<string> RunCliAsync(string workingDirectory, string endpoint, params string[] arguments)
    {
        string cli = Path.Combine(AppContext.BaseDirectory, "patchouli-cli.dll");
        File.Exists(cli).Should().BeTrue("the test project builds and stages the real CLI");
        ProcessStartInfo start = new("dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string name in start.Environment.Keys
                     .Where(name => name.StartsWith("PATCHOULI_", StringComparison.Ordinal)).ToArray())
        {
            start.Environment.Remove(name);
        }

        start.ArgumentList.Add(cli);
        start.ArgumentList.Add("--json");
        start.ArgumentList.Add("--mcp-url");
        start.ArgumentList.Add(endpoint);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            string stdout = await output;
            string stderr = await error;
            process.ExitCode.Should().Be(0, stderr + "\n" + stdout);
            return stdout;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private const string StyleXml = """
                                    <?xml version="1.0" encoding="utf-8"?>
                                    <style xmlns="http://purl.org/net/xbiblio/csl" version="1.0" class="in-text" default-locale="en-US">
                                      <info>
                                        <title>External Agent</title>
                                        <id>http://www.zotero.org/styles/external-agent</id>
                                        <updated>2026-10-24T00:00:00+00:00</updated>
                                      </info>
                                      <citation><layout><text variable="title"/></layout></citation>
                                      <bibliography><layout><text variable="title"/></layout></bibliography>
                                    </style>
                                    """;
}
