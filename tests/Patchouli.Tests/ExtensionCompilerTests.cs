using FluentAssertions;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Tests;

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
            result.Succeeded.Should().BeFalse("top-level arbitrary file writes are not a valid extension initializer");
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
    public async Task Renaming_a_binding_label_preserves_its_stable_key()
    {
        ExtensionCompiler compiler = new();
        ExtensionCheckResult first = await compiler.CheckAsync(
            Package(
                "open Patchouli.Extensions.Sdk\nlet bindings = [ Config.language \"source.language\" \"Source language\" \"auto\" ]"),
            CancellationToken.None);
        ExtensionCheckResult renamed = await compiler.CheckAsync(
            Package(
                "open Patchouli.Extensions.Sdk\nlet bindings = [ Config.language \"source.language\" \"Input language\" \"auto\" ]"),
            CancellationToken.None);

        first.Succeeded.Should().BeTrue("{0}", FormatDiagnostics(first));
        renamed.Succeeded.Should().BeTrue("{0}", FormatDiagnostics(renamed));
        first.ConfigFields.Should().ContainSingle().Which.Key.Should().Be("source.language");
        renamed.ConfigFields.Should().ContainSingle().Which.Key.Should().Be("source.language");
        renamed.ConfigFields[0].Label.Should().Be("Input language");
    }

    [Fact]
    public async Task Duplicate_binding_keys_are_rejected()
    {
        ExtensionCheckResult result = await new ExtensionCompiler().CheckAsync(Package("""
            open Patchouli.Extensions.Sdk
            let bindings = [
                Config.text "api.key" "API key" "first";
                Config.text "api.key" "Another API key" "second"
            ]
            """), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(issue => issue.Severity == ExtensionDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Dynamic_binding_keys_are_rejected()
    {
        ExtensionCheckResult result = await new ExtensionCompiler().CheckAsync(Package("""
            open Patchouli.Extensions.Sdk
            let prefix = "api"
            let bindings = [ Config.text (prefix + ".key") "API key" "default" ]
            """), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(issue => issue.Severity == ExtensionDiagnosticSeverity.Error);
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
    public async Task Typed_model_binding_cannot_be_read_as_an_integer()
    {
        ExtensionCheckResult result = await new ExtensionCompiler().CheckAsync(Package("""
            open Patchouli.Extensions.Contracts
            open Patchouli.Extensions.Sdk
            let model = Config.model "model" "Model"
            let readModelAsInteger (snapshot: ExtensionConfigSnapshot) : int = Binding.get model snapshot
            """), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(issue => issue.Severity == ExtensionDiagnosticSeverity.Error);
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
    public async Task Unrecognized_configuration_namespace_is_not_extracted_as_a_binding()
    {
        ExtensionCheckResult result = await new ExtensionCompiler().CheckAsync(Package("""
            let fake = FakeSdk.Config.text "secret" "Secret" "plaintext"
            """), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ConfigFields.Should().BeEmpty();
        result.Diagnostics.Should().Contain(issue => issue.Severity == ExtensionDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Llm_capability_descriptor_cannot_be_used_as_an_http_capability()
    {
        ExtensionCheckResult result = await new ExtensionCompiler().CheckAsync(Package("""
            open Patchouli.Extensions.Contracts
            open Patchouli.Extensions.Sdk
            let invalidCapability: IHttpCapability = Capability.llm
            """), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(issue => issue.Severity == ExtensionDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Static_capability_bindings_are_extracted_as_host_authorization_requests()
    {
        ExtensionCheckResult result = await new ExtensionCompiler().CheckAsync(Package("""
            let model = bind "llm.primary" (Capability.llm "Translation model" |> Capability.followDefault Domains.translation)
            let http = bind "http" (Capability.http "Managed HTTP")
            """), CancellationToken.None);

        result.Succeeded.Should().BeTrue(FormatDiagnostics(result));
        result.CapabilityRequests.Should().Contain(request => request.Kind == ExtensionCapabilityKind.Llm &&
                                                              request.Key == "llm.primary" &&
                                                              request.DefaultDomain == "translation");
        result.CapabilityRequests.Should().Contain(request => request.Kind == ExtensionCapabilityKind.Http &&
                                                              request.Key == "http");
    }

    [Fact]
    public async Task Results_from_http_llm_and_batch_calls_must_be_explicitly_handled()
    {
        string[] discardedSdkCalls =
        [
            """
            let discard (capabilities: IExtensionCapabilities) (token: CancellationToken) = task {
                let! _ = capabilities.Http.SendAsync(HttpRequest("GET", Uri("https://unit.test"), Dictionary<string, string>()), token)
                return ()
            }
            """,
            """
            let discard (capabilities: IExtensionCapabilities) (token: CancellationToken) = task {
                do! capabilities.Http.SendAsync(HttpRequest("GET", Uri("https://unit.test"), Dictionary<string, string>()), token)
                return ()
            }
            """,
            """
            let discard (capabilities: IExtensionCapabilities) (token: CancellationToken) = task {
                return ignore (capabilities.Http.SendAsync(HttpRequest("GET", Uri("https://unit.test"), Dictionary<string, string>()), token))
            }
            """,
            """
            let discard (capabilities: IExtensionCapabilities) (token: CancellationToken) = task {
                let request = LlmRequest([| LlmMessage("user", "input") |], null, Nullable<decimal>(), Nullable<int>(), null)
                let! _ = capabilities.Llm.CompleteAsync(request, token)
                return ()
            }
            """,
            """
            let discard (capabilities: IExtensionCapabilities) (token: CancellationToken) = task {
                let request = LlmRequest([| LlmMessage("user", "input") |], null, Nullable<decimal>(), Nullable<int>(), null)
                do! capabilities.Llm.CompleteAsync(request, token)
                return ()
            }
            """,
            """
            let discard (capabilities: IExtensionCapabilities) (token: CancellationToken) = task {
                let request = LlmRequest([| LlmMessage("user", "input") |], null, Nullable<decimal>(), Nullable<int>(), null)
                return ignore (capabilities.Llm.CompleteAsync(request, token))
            }
            """,
            """
            let discard (capabilities: IExtensionCapabilities) (token: CancellationToken) =
                let request = LlmRequest([| LlmMessage("user", "input") |], null, Nullable<decimal>(), Nullable<int>(), null)
                capabilities.Llm.CompleteAsync(request, token) |> ignore
            """,
            """
            let discard (token: CancellationToken) = task {
                let work (batch: int array) (_: CancellationToken) : Task<Result<int, exn> array> =
                    Task.FromResult(batch |> Array.map (fun value -> Ok value))
                let! _ = Batch.map 2 work [ [| 1 |] ] token
                return ()
            }
            """,
            """
            let discard (token: CancellationToken) = task {
                let work (batch: int array) (_: CancellationToken) : Task<Result<int, exn> array> =
                    Task.FromResult(batch |> Array.map (fun value -> Ok value))
                do! Batch.map 2 work [ [| 1 |] ] token
                return ()
            }
            """,
            """
            let discard (token: CancellationToken) = task {
                let work (batch: int array) (_: CancellationToken) : Task<Result<int, exn> array> =
                    Task.FromResult(batch |> Array.map (fun value -> Ok value))
                return ignore (Batch.map 2 work [ [| 1 |] ] token)
            }
            """
        ];

        foreach (string source in discardedSdkCalls)
        {
            ExtensionCheckResult result = await new ExtensionCompiler().CheckAsync(Package(source),
                CancellationToken.None);

            result.Succeeded.Should().BeFalse(source);
            result.Diagnostics.Should().Contain(issue => issue.Code == "EXT0021", source);
        }
    }

    [Fact]
    public async Task Ignoring_an_ordinary_non_sdk_result_remains_valid()
    {
        ExtensionCheckResult result = await new ExtensionCompiler().CheckAsync(Package(
                "open System\nignore (String.Concat(\"ordinary\", \" result\"))"),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue(FormatDiagnostics(result));
        result.Diagnostics.Should().NotContain(issue => issue.Code == "EXT0021");
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
        string mainSource = $"#load \"{helperPath}\"\n" + """
                                                          open System.Threading.Tasks
                                                          open Patchouli.Extensions.Contracts
                                                          open Patchouli.Extensions.Sdk
                                                          let extension : ITranslationExtension =
                                                              { new ITranslationExtension with
                                                                  member _.Descriptor = ExtensionDescriptor("test.extension", "Test", "1.0", "")
                                                                  member _.ConfigFields = [||]
                                                                  member _.TranslateAsync(_, _, _) =
                                                                      Task.FromResult(TranslationExtensionResult([||])) }
                                                          """;
        ExtensionPackageSnapshot package = new("test.extension", new Dictionary<string, string>
        {
            ["main.fsx"] = mainSource,
            [helperPath] = helperSource
        });
        return new ExtensionCompiler().CheckAsync(package, CancellationToken.None);
    }

    private static ExtensionPackageSnapshot Package(string mainSource)
    {
        return new ExtensionPackageSnapshot("test.extension", new Dictionary<string, string>
        {
            ["main.fsx"] = """
                           open System.Threading.Tasks
                           open System.Threading
                           open System.Collections.Generic
                           open System
                           open Patchouli.Extensions.Contracts
                           open Patchouli.Extensions.Sdk
                           """ + Environment.NewLine + mainSource + Environment.NewLine + """
                let extension : ITranslationExtension =
                    { new ITranslationExtension with
                        member _.Descriptor = ExtensionDescriptor("test.extension", "Test extension", "1.0", "")
                        member _.ConfigFields = [||]
                        member _.TranslateAsync(context, capabilities, cancellationToken) =
                            Task.FromResult(TranslationExtensionResult([||]))
                    }
                """
        });
    }

    private static string FormatDiagnostics(ExtensionCheckResult result)
    {
        return string.Join(Environment.NewLine, result.Diagnostics.Select(issue =>
            $"{issue.Location.FileName}({issue.Location.Line},{issue.Location.Column}): {issue.Code}: {issue.Message}"));
    }
}
