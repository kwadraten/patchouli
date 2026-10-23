using FluentAssertions;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Tests;

[Trait("Category", "fcs")]
public sealed class ExtensionCompilerTests
{
    [Fact]
    public async Task Static_check_does_not_execute_a_malicious_initializer()
    {
        string marker = Path.Combine(Path.GetTempPath(), $"patchouli-fsx-check-{Guid.NewGuid():N}.txt");
        string escapedMarker = marker.Replace("\\", "\\\\", StringComparison.Ordinal);
        ExtensionPackageSnapshot package = Package($$"""
                                                     open Patchouli.Extensions.Sdk
                                                     let sideEffect = System.IO.File.WriteAllText("{{escapedMarker}}", "executed")
                                                     """);

        try
        {
            ExtensionCheckResult result = await new ExtensionCompiler().CheckAsync(package, CancellationToken.None);

            File.Exists(marker).Should().BeFalse("checking an extension must only parse and typecheck its source");
            result.Succeeded.Should().BeTrue(FormatDiagnostics(result));
        }
        finally
        {
            if (File.Exists(marker))
            {
                File.Delete(marker);
            }
        }
    }

    [Fact]
    public async Task Renaming_a_settings_label_preserves_its_stable_key()
    {
        ExtensionCompiler compiler = new();
        ExtensionCheckResult first = await compiler.CheckAsync(
            Package(SettingsSource("Source language")),
            CancellationToken.None);
        ExtensionCheckResult renamed = await compiler.CheckAsync(
            Package(SettingsSource("Input language")),
            CancellationToken.None);

        first.Succeeded.Should().BeTrue("{0}", FormatDiagnostics(first));
        renamed.Succeeded.Should().BeTrue("{0}", FormatDiagnostics(renamed));
        first.ConfigFields.Should().ContainSingle().Which.Key.Should().Be("source.language");
        renamed.ConfigFields.Should().ContainSingle().Which.Key.Should().Be("source.language");
        renamed.ConfigFields[0].Label.Should().Be("Input language");
    }

    [Fact]
    public async Task Duplicate_settings_keys_are_rejected()
    {
        ExtensionCheckResult result = await new ExtensionCompiler().CheckAsync(Package("""
            [<Settings>]
            type Settings = {
                [<Field("API key", Key = "api.key")>]
                First: string
                [<Field("Another API key", Key = "api.key")>]
                Second: string
            }
            """), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(issue => issue.Severity == ExtensionDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Settings_schema_reads_default_and_numeric_range_attributes()
    {
        ExtensionCheckResult result = await new ExtensionCompiler().CheckAsync(Package("""
            [<Settings>]
            type Settings = {
                [<Field("Concurrency", Kind = ConfigValueKind.Integer)>]
                [<Range(1, 8)>]
                [<Default(4)>]
                Concurrency: int
            }
            """), CancellationToken.None);

        result.Succeeded.Should().BeTrue(FormatDiagnostics(result));
        ConfigFieldDescriptor field = result.ConfigFields.Should().ContainSingle().Subject;
        field.Key.Should().Be("concurrency");
        field.DefaultValue.Should().Be("4");
        field.Minimum.Should().Be(1);
        field.Maximum.Should().Be(8);
    }

    [Fact]
    public async Task Dynamic_values_are_not_extracted_as_configuration_fields()
    {
        ExtensionCheckResult result = await new ExtensionCompiler().CheckAsync(Package("""
            let prefix = "api"
            let generatedKey = prefix + ".key"
            """), CancellationToken.None);

        result.Succeeded.Should().BeTrue(FormatDiagnostics(result));
        result.ConfigFields.Should().BeEmpty();
    }

    [Fact]
    public async Task Pipeline_step_type_mismatch_is_reported_as_a_compile_error()
    {
        ExtensionCheckResult result = await new ExtensionCompiler().CheckAsync(Package("""
            open Patchouli.Extensions.Sdk
            let first: Pipeline<string, string> = Step.sync id
            let invalid = first |>> Step.sync (fun (number: int) -> number + 1)
            """), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(issue => issue.Severity == ExtensionDiagnosticSeverity.Error &&
                                                     issue.Code == "FS0001");
    }

    [Fact]
    public async Task Model_setting_attributes_are_read_as_schema_metadata()
    {
        ExtensionCheckResult result = await new ExtensionCompiler().CheckAsync(Package("""
            open Patchouli.Extensions.Contracts
            open Patchouli.Extensions.Sdk
            [<Settings>]
            type Settings = {
                [<Model("Translation model", Key = "model")>]
                Model: ExtensionModelSelection
            }
            let readModel (snapshot: ExtensionConfigSnapshot) : ExtensionModelSelection =
                (ConfigSettings.Read<Settings>(snapshot)).Model
            """), CancellationToken.None);

        result.Succeeded.Should().BeTrue(FormatDiagnostics(result));
        ConfigFieldDescriptor field = result.ConfigFields.Should().ContainSingle().Subject;
        field.Key.Should().Be("model");
        field.Kind.Should().Be(ConfigValueKind.Model);
    }

    [Fact]
    public async Task Page_identity_rejects_untyped_string_ids_at_the_script_boundary()
    {
        ExtensionCheckResult result = await new ExtensionCompiler().CheckAsync(Package("""
            open Patchouli.Extensions.Contracts
            let invalidIdentity = ExtensionPageIdentity("document", "page", "revision", 1)
            """), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(issue => issue.Severity == ExtensionDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Null_literal_for_non_nullable_reference_is_reported_by_compiler_nullness_checks()
    {
        ExtensionCheckResult result = await new ExtensionCompiler().CheckAsync(Package("""
            let invalid: string = null
            """), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(issue => issue.Code == "FS3261" &&
                                                     issue.Severity == ExtensionDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Ordinary_source_without_settings_metadata_has_an_empty_schema()
    {
        ExtensionCheckResult result = await new ExtensionCompiler().CheckAsync(Package("let ordinaryValue = 1"),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue(FormatDiagnostics(result));
        result.ConfigFields.Should().BeEmpty();
    }

    [Fact]
    public async Task Standard_http_client_usage_is_accepted()
    {
        ExtensionCheckResult result = await new ExtensionCompiler().CheckAsync(Package("""
            open Patchouli.Extensions.Contracts
            open Patchouli.Extensions.Sdk
            let send (resources: IExtensionResources) (token: CancellationToken) =
                resources.Http.GetAsync("https://unit.test", token)
            """), CancellationToken.None);

        result.Succeeded.Should().BeTrue(FormatDiagnostics(result));
    }

    [Fact]
    public async Task Compiler_rejects_a_package_with_an_unsupported_host_api()
    {
        ExtensionCheckResult result = await new ExtensionCompiler().CheckAsync(
            Package("let ordinaryValue = 1", "2"), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(issue => issue.Severity == ExtensionDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Ordinary_dll_compilation_does_not_apply_the_removed_discarded_result_policy()
    {
        ExtensionCheckResult result = await new ExtensionCompiler().CheckAsync(Package(
                "open System\nignore (String.Concat(\"ordinary\", \" result\"))"),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue(FormatDiagnostics(result));
        result.Diagnostics.Should().NotContain(issue => issue.Severity == ExtensionDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Check_reports_loaded_helper_syntax_errors_at_the_helper_line()
    {
        ExtensionCheckResult result = await CheckPackageWithHelperAsync("helpers/foo.fsx",
            "let first = 1\nlet second = 2\nlet broken = (1 + )\n");

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(issue => issue.Location.FileName == "helpers/foo.fsx" &&
                                                     issue.Location.Line == 3);
    }

    [Fact]
    public async Task Check_reports_loaded_helper_type_errors_at_the_helper_line()
    {
        ExtensionCheckResult result = await CheckPackageWithHelperAsync("helpers/foo.fsx",
            "module Foo\nlet value: int = \"wrong\"\n");

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(issue => issue.Location.FileName == "helpers/foo.fsx" &&
                                                     issue.Location.Line == 2);
    }

    [Fact]
    public async Task Nested_helper_named_main_fsx_keeps_its_own_diagnostic_line()
    {
        ExtensionCheckResult result = await CheckPackageWithHelperAsync("nested/main.fsx",
            "module Nested\nlet value: int = \"wrong\"\n");

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(issue => issue.Location.FileName == "nested/main.fsx" &&
                                                     issue.Location.Line == 2);
    }

    private static Task<ExtensionCheckResult> CheckPackageWithHelperAsync(string helperPath, string helperSource)
    {
        string mainSource = $"#load \"{helperPath}\"\n" +
                            "open System.Threading.Tasks\n" +
                            "open Patchouli.Extensions.Contracts\n" +
                            "open Patchouli.Extensions.Sdk\n" +
                            "/// <summary>Compiler test extension.</summary>\n" +
                            "/// <patchouli-extension id=\"test.extension\" kind=\"translation\" version=\"1.0.0\" host-api=\"patchouli.extensions/3\">\n" +
                            "/// <name>Test</name>\n" +
                            "/// </patchouli-extension>\n" +
                            "let extension = Extension.createTranslation<unit> (fun () _ _ _ -> Task.CompletedTask)\n";
        ExtensionPackageSnapshot package = new("test.extension", new Dictionary<string, string>
        {
            ["main.fsx"] = mainSource,
            [helperPath] = helperSource
        });
        return new ExtensionCompiler().CheckAsync(package, CancellationToken.None);
    }

    private static string SettingsSource(string label)
    {
        return $$"""
                 [<Settings>]
                 type Settings = {
                     [<Field("{{label}}", Key = "source.language")>]
                     SourceLanguage: string
                 }
                 let readSourceLanguage (snapshot: ExtensionConfigSnapshot) =
                     (ConfigSettings.Read<Settings>(snapshot)).SourceLanguage
                 """;
    }

    private static ExtensionPackageSnapshot Package(string mainSource, string hostApi = "3")
    {
        string completeSource = "open System.Threading.Tasks\n" +
                                "open System.Threading\n" +
                                "open System.Collections.Generic\n" +
                                "open System\n" +
                                "open Patchouli.Extensions.Contracts\n" +
                                "open Patchouli.Extensions.Sdk\n" +
                                "\n" +
                                mainSource + "\n" +
                                "/// <summary>Compiler test extension.</summary>\n" +
                                $"/// <patchouli-extension id=\"test.extension\" kind=\"translation\" version=\"1.0.0\" host-api=\"patchouli.extensions/{hostApi}\">\n" +
                                "/// <name>Test extension</name>\n" +
                                "/// </patchouli-extension>\n" +
                                "let extension = Extension.createTranslation<unit> (fun () _ _ _ -> Task.CompletedTask)\n";
        return new ExtensionPackageSnapshot("test.extension", new Dictionary<string, string>
        {
            ["main.fsx"] = completeSource
        });
    }

    private static string FormatDiagnostics(ExtensionCheckResult result)
    {
        return string.Join(Environment.NewLine, result.Diagnostics.Select(issue =>
            $"{issue.Location.FileName}({issue.Location.Line},{issue.Location.Column}): {issue.Code}: {issue.Message}"));
    }
}
