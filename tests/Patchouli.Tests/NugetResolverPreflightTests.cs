using Patchouli.Extensions;

namespace Patchouli.Tests;

public sealed class NugetResolverPreflightTests
{
    [Fact]
    public void CandidatePathsKeepExplicitRuntimePathAndPathPrecedenceBeforeWindowsGlobals()
    {
        string explicitHost = Path.Combine("explicit-sdk", "dotnet.exe");
        string runtimeHost = Path.Combine("runtime-sdk", "dotnet.exe");
        string firstPath = Path.Combine("path", "first");
        string secondPath = Path.Combine("path", "second");
        string[] candidates = NugetResolverPreflight.candidatePaths(
            explicitHost,
            runtimeHost,
            firstPath + ";" + secondPath,
            "program-w6432",
            "program-files",
            "user-profile",
            ';',
            true);

        Assert.Equal(new[]
        {
            Path.GetFullPath(explicitHost),
            Path.GetFullPath(runtimeHost),
            Path.GetFullPath(Path.Combine(firstPath, "dotnet.exe")),
            Path.GetFullPath(Path.Combine(secondPath, "dotnet.exe")),
            Path.GetFullPath(Path.Combine("program-w6432", "dotnet", "dotnet.exe")),
            Path.GetFullPath(Path.Combine("program-files", "dotnet", "dotnet.exe"))
        }, candidates);
    }

    [Fact]
    public void CandidatePathsDoNotTreatAnApplicationApphostAsDotnetAndIncludeUnixFallbacks()
    {
        string[] candidates = NugetResolverPreflight.candidatePaths(
            "",
            @"C:\app\Patchouli.Extensions.Compiler.exe",
            "/opt/dotnet:/custom/bin",
            "",
            "",
            "/home/test",
            ':',
            false);

        Assert.Equal(new[]
        {
            Path.GetFullPath("/opt/dotnet/dotnet"),
            Path.GetFullPath("/custom/bin/dotnet"),
            Path.GetFullPath("/usr/share/dotnet/dotnet"),
            Path.GetFullPath("/usr/local/share/dotnet/dotnet"),
            Path.GetFullPath("/home/test/.dotnet/dotnet")
        }, candidates);
    }

    [Fact]
    public void Empty_or_unrelated_resolver_references_do_not_confirm_a_requested_package()
    {
        Assert.False(NugetResolverPreflight.isPackageRestored("Newtonsoft.Json", "13.0.3",
            Array.Empty<Tuple<string, string>>()));
        Assert.False(NugetResolverPreflight.isPackageRestored("Newtonsoft.Json", "13.0.3",
            new[] { Tuple.Create("Another.Package", "13.0.3") }));
    }

    [Fact]
    public void Package_confirmation_requires_the_requested_version()
    {
        Tuple<string, string>[] resolved = [Tuple.Create("newtonsoft.json", "13.0.3.0")];
        Assert.True(NugetResolverPreflight.isPackageRestored("Newtonsoft.Json", "13.0.3", resolved));
        Assert.False(NugetResolverPreflight.isPackageRestored("Newtonsoft.Json", "12.0.3", resolved));
    }

    [Fact]
    public void Restore_isolation_environment_disables_ancestor_msbuild_and_central_package_imports()
    {
        Dictionary<string, string> entries = NugetResolverPreflight.restoreIsolationEnvironment
            .ToDictionary(entry => entry.Item1, entry => entry.Item2);

        Assert.Equal(4, entries.Count);
        Assert.Equal("false", entries["ImportDirectoryBuildProps"]);
        Assert.Equal("false", entries["ImportDirectoryBuildTargets"]);
        Assert.Equal("false", entries["ImportDirectoryPackagesProps"]);
        Assert.Equal("false", entries["ManagePackageVersionsCentrally"]);
    }
}
