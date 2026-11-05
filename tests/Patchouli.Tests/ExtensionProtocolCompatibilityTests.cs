using System.Text.Json;
using FluentAssertions;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Tests;

[Trait("Category", "compiled-extension")]
public sealed class ExtensionProtocolCompatibilityTests
{
    [Theory]
    [InlineData("patchouli.extensions/3")]
    [InlineData("patchouli.extensions/4")]
    public async Task Protocol_survives_compilation_cache_restart_and_lkg_recovery(string api)
    {
        string root = Path.Combine(Path.GetTempPath(), $"patchouli-protocol-{Guid.NewGuid():N}");
        ExtensionPackageSnapshot package = new("compat.fixture", new Dictionary<string, string>
        {
            ["main.fsx"] = $$"""
                             open System.Threading.Tasks
                             open Patchouli.Extensions.Contracts
                             open Patchouli.Extensions.Sdk
                             /// <summary>Compatibility fixture.</summary>
                             /// <patchouli-extension id="compat.fixture" kind="translation" version="1.0.0" host-api="{{api}}">
                             /// <name>Compatibility</name>
                             /// </patchouli-extension>
                             let extension = Extension.createTranslation<unit> (fun () _ _ _ -> Task.CompletedTask)
                             """
        });
        try
        {
            await using (IExtensionManager manager = NewManager(root))
            {
                await manager.SavePackageAsync(package, CancellationToken.None);
                ExtensionCheckResult checkedPackage =
                    await manager.ActivateAsync(package.Id, package, CancellationToken.None);
                checkedPackage.Succeeded.Should()
                    .BeTrue(string.Join("; ", checkedPackage.Diagnostics.Select(d => d.Message)));
                await using IExtensionLease lease = (await manager.AcquireAsync(package.Id, CancellationToken.None))!;
                lease.Descriptor.HostApi.Should().Be(api);
                await manager.SaveConfigAsync(package.Id, new ExtensionConfigSnapshot(
                    new Dictionary<string, string> { ["retained"] = "setting" }, 1), CancellationToken.None);
                (await manager.PromoteLastKnownGoodAsync(package.Id, CancellationToken.None)).Should().BeTrue();
            }

            using JsonDocument state = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(root, "extensions", package.Id, "extension-state.json")));
            state.RootElement.GetProperty("hostApi").GetString().Should().Be(api);
            ExtensionCompiler.TryReadCachedCompilation(package, Path.Combine(root, ".compiled"))
                .Should().NotBeNull("both supported declarations can use the verified cache without SDK restoration");

            await using (IExtensionManager restarted = NewManager(root))
            {
                (await restarted.RestoreAsync(package.Id, CancellationToken.None)).Should().BeTrue();
                (await restarted.RollbackAsync(package.Id, CancellationToken.None)).Should().BeTrue();
                await using IExtensionLease lease = (await restarted.AcquireAsync(package.Id, CancellationToken.None))!;
                lease.Descriptor.HostApi.Should().Be(api);
                (await restarted.ReadConfigAsync(package.Id, CancellationToken.None)).Values["retained"]
                    .Should().Be("setting");
            }
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryForcingExtensionUnload(root);
        }
    }

    private static IExtensionManager NewManager(string root)
    {
        return new ExtensionManager(root,
            TestExtensionResources.Unavailable(), new FileExtensionStore(root),
            new CompiledExtensionGenerationLoader(root));
    }
}
