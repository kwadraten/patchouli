using FSharp.Compiler.CodeAnalysis;
using FluentAssertions;
using Patchouli.Extensions;

namespace Patchouli.Tests;

[Trait("Category", "fcs")]
public sealed class ExtensionNugetDirectiveTests
{
    [Theory]
    [InlineData("missing", "EXT0033")]
    [InlineData("runtime", "EXT0034")]
    [InlineData("incompatible", "EXT0037")]
    [InlineData("unavailable", "EXT0035")]
    public async Task Missing_sdk_produces_install_guidance_at_the_real_helper_directive(string status, string code)
    {
        Dictionary<string, string> sources = new()
        {
            ["main.fsx"] = "#load \"Helper.fsx\"",
            ["Helper.fsx"] = "#r \"nuget: Example.Package, 1.2.3\""
        };
        FSharpChecker checker = FSharpChecker.Create(null, null, null, null, null, null, null,
            null, null, null, null, null, null, null);
        NugetSdkProbeResult probe = status switch
        {
            "missing" => NugetSdkProbeResult.DotnetMissing,
            "runtime" => NugetSdkProbeResult.NewRuntimeOnly("runtime"),
            "incompatible" => NugetSdkProbeResult.NewSdkIncompatible("old-sdk"),
            _ => NugetSdkProbeResult.ProbeUnavailable
        };
        ExtensionDependencyResolution result = await ExtensionDependencies.resolveWithSdkProbeAsync(checker,
            Path.GetTempPath(), Path.GetTempPath(), sources, _ => Task.FromResult(probe), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle().Which.Code.Should().Be(code);
        result.Diagnostics[0].Message.Should().Contain(".NET SDK").And.Contain("https://dotnet.microsoft.com/download");
        result.Diagnostics[0].Location.FileName.Should().Be("Helper.fsx");
    }

    [Fact]
    public async Task Comments_and_inactive_directives_never_probe_or_require_an_sdk()
    {
        Dictionary<string, string> sources = new()
        {
            ["main.fsx"] = """
                           (*
                           #r "nuget: Commented, 1.0.0"
                           *)
                           #if UNDEFINED
                           #r "nuget: Inactive, 1.0.0"
                           #endif
                           let value = 1
                           """
        };
        FSharpChecker checker = FSharpChecker.Create(null, null, null, null, null, null, null,
            null, null, null, null, null, null, null);
        ExtensionDependencyResolution result = await ExtensionDependencies.resolveWithSdkProbeAsync(checker,
            Path.GetTempPath(), Path.GetTempPath(), sources, _ => throw new InvalidOperationException(
                "No SDK should be probed for this source"), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Scanner_reads_only_real_compiler_active_directives_in_reachable_helpers()
    {
        Dictionary<string, string> sources = new()
        {
            ["main.fsx"] = """"
                           (*
                           #r "nuget: Commented, 1.0.0"
                           *)
                           let text = """
                           #r "nuget: StringLiteral, 1.0.0"
                           """
                           #if NEVER_DEFINED
                           #r "nuget: Inactive, 1.0.0"
                           #endif
                           #if COMPILED
                           #r "nuget: CompilerActive, 1.0.0"
                           #endif
                           #load "sub/Helper.fsx"
                           """",
            ["sub/Helper.fsx"] =
                "#r @\"nuget: HelperPackage, Version=2.0.0, UsePackageTargets=false\"\n#load \"../main.fsx\"",
            ["Unused.fsx"] = "#r \"nuget: Unreachable, 1.0.0\""
        };
        FSharpChecker checker = FSharpChecker.Create(null, null, null, null, null, null, null,
            null, null, null, null, null, null, null);

        ExtensionNugetInputs inputs = await ExtensionDependencies.scanNugetDirectives(
            checker, sources, CancellationToken.None);
        ExtensionNugetDirective[] directives = inputs.Packages;

        directives.Select(item => item.PackageId).Should().BeEquivalentTo("CompilerActive", "HelperPackage");
        directives.Single(item => item.PackageId == "HelperPackage").FileName.Should().Be("sub/Helper.fsx");
        directives.Single(item => item.PackageId == "HelperPackage").Line.Should().Be(1);
        directives.Single(item => item.PackageId == "HelperPackage").Version.Should().Be("2.0.0");
        directives.Single(item => item.PackageId == "HelperPackage").Reference.Should()
            .Contain("UsePackageTargets=false");
    }
}
