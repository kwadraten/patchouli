using FluentAssertions;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Extensions;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Translation;
using Patchouli.Llm;

namespace Patchouli.Tests;

[Trait("Category", "fcs")]
public sealed class LayoutRecognitionExtensionTests
{
    [Fact]
    public void Translation_resource_wrappers_retain_the_shared_model_capability()
    {
        LayoutResources resources = new();
        IExtensionResources wrapped = TranslationExtensionRunner.WrapWithGate(resources, null);
        wrapped.LayoutRecognition.Should().BeSameAs(resources.LayoutRecognition);
        wrapped.ForRun(new ExtensionConfigSnapshot(new Dictionary<string, string>(), 0))
            .LayoutRecognition.Should().BeSameAs(resources.LayoutRecognition);
    }

    [Theory]
    [InlineData("layout.pp-doclayout-v3")]
    [InlineData("layout.doclayout-yolo")]
    public void Built_in_models_own_their_files_and_are_caller_neutral(string id)
    {
        ExtensionPackageSnapshot package = BuiltInLayoutRecognitionExtensions.Packages.Single(p => p.Id == id);
        ExtensionEntryMetadata.Parse(package).Kind.Should().Be(ExtensionKind.LayoutRecognition);
        string source = package.Sources["main.fsx"];
        source.Should().Contain("ManagedFile(").And.Contain("ILayoutRecognitionJob");
        source.Should().NotContain("IOcrJob").And.NotContain("DocumentInstanceId");
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("empty")]
    [InlineData("invalid")]
    [InlineData("duplicate")]
    [InlineData("unfinished")]
    [InlineData("cancel")]
    public async Task Compiled_job_validates_results_and_releases_generation(string behavior)
    {
        string root = Path.Combine(Path.GetTempPath(), $"patchouli-layout-{Guid.NewGuid():N}");
        try
        {
            await using TranslationExtensionCatalog catalog = new(root, LlmAppSettings.Default,
                TestExtensionResources.Unavailable());
            string body = behavior switch
            {
                "empty" => "do! job.SubmitAsync(input, [||], token)",
                "invalid" =>
                    "do! job.SubmitAsync(input, [| LayoutRecognitionRegion(\"text\", NormalizedBBox(-1., 0., 1., 1.), 0.9) |], token)",
                "duplicate" =>
                    "do! job.SubmitAsync(input, [||], token)\n            do! job.SubmitAsync(input, [||], token)",
                "unfinished" => "()",
                "cancel" => "raise (System.OperationCanceledException(token))",
                _ =>
                    "do! job.SubmitAsync(input, [| LayoutRecognitionRegion(settings.Label, NormalizedBBox(0., 0., 1., 1.), 0.9) |], token)"
            };
            ExtensionPackageSnapshot package = new("test.layout", new Dictionary<string, string>
            {
                ["main.fsx"] = $$"""
                                 open System.Threading
                                 open System.Threading.Tasks
                                 open Patchouli.Core.Layout
                                 open Patchouli.Extensions.Contracts
                                 open Patchouli.Extensions.Sdk
                                 [<Settings>]
                                 type Settings = {
                                     [<Field("Label", Key = "label"); Default("text")>]
                                     Label: string
                                 }
                                 let run (settings: Settings) (job: ILayoutRecognitionJob) (_resources: IExtensionResources) (token: CancellationToken) : Task =
                                     task {
                                         let! next = job.NextInputAsync(token)
                                         match Option.ofObj next with
                                         | None -> ()
                                         | Some input ->
                                             {{body}}
                                         let! _ = job.NextInputAsync(token)
                                         return ()
                                     } :> Task
                                 /// <summary>Caller-neutral test model.</summary>
                                 /// <patchouli-extension id="test.layout" kind="layout-recognition" version="1.0.0" host-api="patchouli.extensions/4">
                                 /// <name>Test model</name>
                                 /// </patchouli-extension>
                                 let extension = Extension.createLayoutRecognition<Settings> run
                                 """
            });
            ExtensionCheckResult check =
                await catalog.Manager.ActivateAsync(package.Id, package, CancellationToken.None);
            check.Succeeded.Should().BeTrue(string.Join("\n", check.Diagnostics.Select(d => d.Message)));
            await catalog.Manager.SaveConfigAsync(package.Id,
                new ExtensionConfigSnapshot(new Dictionary<string, string> { ["label"] = "configured" }, 1));
            LayoutRecognitionService service = new(catalog);
            Func<Task<IReadOnlyList<LayoutRecognitionRegion>>> execute = () =>
                LayoutRecognition.DetectAsync(service, package.Id, new byte[] { 1 });
            if (behavior is "invalid" or "duplicate" or "unfinished")
            {
                await execute.Should().ThrowAsync<Exception>();
            }
            else if (behavior == "cancel")
            {
                await execute.Should().ThrowAsync<OperationCanceledException>();
            }
            else
            {
                IReadOnlyList<LayoutRecognitionRegion> result = await execute();
                if (behavior == "empty")
                {
                    result.Should().BeEmpty();
                }
                else
                {
                    result.Should().ContainSingle().Which.Label.Should().Be("configured");
                }
            }

            await using IExtensionLease? lease = await catalog.Manager.AcquireAsync(package.Id, CancellationToken.None);
            lease.Should().NotBeNull("a failed or completed job releases its invocation normally");
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryWithRetry(root);
        }
    }

    private sealed class LayoutResources : IExtensionResources, ILayoutRecognitionCapability
    {
        public ILayoutRecognitionCapability LayoutRecognition => this;
        public ILlmCapability Llm => throw new NotSupportedException();
        public HttpClient Http => throw new NotSupportedException();

        public IExtensionResources ForRun(ExtensionConfigSnapshot config)
        {
            return this;
        }

        public Task WaitToProceedAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<string?> GetCredentialAsync(string reference, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<IReadOnlyList<ExtensionDescriptor>> ListAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ExtensionDescriptor>>([]);
        }

        public Task RunAsync(string extensionId, ILayoutRecognitionJob job,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
