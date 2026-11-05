using System.IO.Compression;
using System.Security;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using FluentAssertions;
using Microsoft.CodeAnalysis.Emit;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Tests;

[Trait("Category", "compiled-extension")]
public sealed class ExtensionLocalNugetTests
{
    [Fact]
    public async Task Local_nuget_dependency_restores_compiles_loads_and_then_uses_verified_cache()
    {
        string root = Path.Combine(Path.GetTempPath(), $"patchouli-nuget-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string feed = Path.Combine(root, "feed");
        Directory.CreateDirectory(feed);
        string packageId = "Patchouli.Test." + Guid.NewGuid().ToString("N");
        try
        {
            CSharpCompilation compilation = CSharpCompilation.Create("Fixture.Dependency",
                [
                    CSharpSyntaxTree.ParseText(
                        "namespace Fixture; public static class Dependency { public static int Value => 42; }")
                ],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using MemoryStream binary = new();
            EmitResult emitted = compilation.Emit(binary);
            emitted.Success.Should().BeTrue(string.Join("; ", emitted.Diagnostics));
            using (ZipArchive archive =
                   ZipFile.Open(Path.Combine(feed, packageId + ".1.0.0.nupkg"), ZipArchiveMode.Create))
            {
                using (StreamWriter manifest = new(archive.CreateEntry(packageId + ".nuspec").Open()))
                {
                    manifest.Write($"""
                                    <?xml version="1.0"?>
                                    <package><metadata><id>{packageId}</id><version>1.0.0</version>
                                    <authors>Patchouli tests</authors><description>Offline fixture.</description>
                                    </metadata></package>
                                    """);
                }

                using Stream assembly = archive.CreateEntry("lib/net10.0/Fixture.Dependency.dll").Open();
                binary.Position = 0;
                binary.CopyTo(assembly);
            }

            File.WriteAllText(Path.Combine(root, "NuGet.Config"), $"""
                                                                   <configuration>
                                                                     <config><add key="globalPackagesFolder" value="{SecurityElement.Escape(Path.Combine(root, "packages"))}" /></config>
                                                                     <packageSources><clear/><add key="local" value="{SecurityElement.Escape(feed)}" /></packageSources>
                                                                     <auditSources><clear/></auditSources>
                                                                   </configuration>
                                                                   """);
            ExtensionPackageSnapshot source = new("test.local-nuget", new Dictionary<string, string>
            {
                ["main.fsx"] = $$"""
                                 #i "nuget: {{feed.Replace("\\", "/")}}"
                                 #if COMPILED
                                 #r "nuget: {{packageId}}, Version=1.0.0"
                                 #endif
                                 #if INTERACTIVE
                                 #r "nuget: This.Package.Must.Not.Be.Resolved, 0.0.1"
                                 #endif
                                 open System.Threading.Tasks
                                 open Patchouli.Extensions.Contracts
                                 open Patchouli.Extensions.Sdk
                                 let dependencyValue = Fixture.Dependency.Value
                                 do if dependencyValue <> 42 then failwith "Wrong dependency"
                                 /// <summary>Local NuGet integration.</summary>
                                 /// <patchouli-extension id="test.local-nuget" kind="translation" version="1.0.0" host-api="patchouli.extensions/4">
                                 /// <name>Local NuGet</name>
                                 /// </patchouli-extension>
                                 let extension = Extension.createTranslation<unit> (fun () _ _ _ -> Task.CompletedTask)
                                 """
            });
            string input = Path.Combine(root, "input");
            Directory.CreateDirectory(input);
            File.WriteAllText(Path.Combine(input, "main.fsx"), source.Sources["main.fsx"]);
            string reply = Path.Combine(root, "compiled.json");
            string compiler = Path.Combine(AppContext.BaseDirectory, "extension-compiler",
                OperatingSystem.IsWindows() ? "Patchouli.Extensions.Compiler.exe" : "Patchouli.Extensions.Compiler");
            ProcessStartInfo start = new(compiler)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string argument in new[]
                     {
                         "--source", input, "--id", source.Id, "--artifacts",
                         Path.Combine(root, ".compiled"), "--result", reply
                     })
            {
                start.ArgumentList.Add(argument);
            }

            // Isolate the real SDK/NuGet subprocess without changing the test host's environment
            // or a shared compiler server used by other tests.
            start.Environment["NUGET_PACKAGES"] = Path.Combine(root, "packages");
            start.Environment["NUGET_HTTP_CACHE_PATH"] = Path.Combine(root, "http-cache");
            start.Environment["NuGetAudit"] = "false";
            using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
            using Process child = Process.Start(start)!;
            Task<string> stdout = child.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderr = child.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await child.WaitForExitAsync(timeout.Token);
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(true);
                    await child.WaitForExitAsync(CancellationToken.None);
                }
            }

            await stdout;
            string errors = await stderr;
            using JsonDocument compiled = JsonDocument.Parse(File.ReadAllText(reply));
            ExtensionCheckResult check = compiled.RootElement.GetProperty("CheckResult")
                .Deserialize<ExtensionCheckResult>()!;
            check.Succeeded.Should()
                .BeTrue(string.Join("; ", check.Diagnostics.Select(issue => issue.Message)) + errors);
            child.ExitCode.Should().Be(0);
            await using IExtensionManager manager = new ExtensionManager(root,
                TestExtensionResources.Unavailable(), new FileExtensionStore(root),
                new CompiledExtensionGenerationLoader(root));
            ExtensionCheckResult activated = await manager.ActivateAsync(source.Id, source, CancellationToken.None);
            activated.Succeeded.Should()
                .BeTrue(string.Join("; ", activated.Diagnostics.Select(issue => issue.Message)));
            await using IExtensionLease lease = (await manager.AcquireAsync(source.Id, CancellationToken.None))!;
            lease.Descriptor.HostApi.Should().Be(ExtensionHostApi.Current);
            ExtensionCompiler.TryReadCachedCompilation(source, Path.Combine(root, ".compiled"))
                .Should().NotBeNull();
            Directory.EnumerateFiles(Path.Combine(root, "packages"), "Fixture.Dependency.dll",
                    SearchOption.AllDirectories)
                .Should().ContainSingle();
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryForcingExtensionUnload(root);
        }
    }
}
