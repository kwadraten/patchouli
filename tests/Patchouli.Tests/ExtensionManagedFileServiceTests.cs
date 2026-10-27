using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Extensions;
using Xunit;

namespace Patchouli.Tests;

/// <summary>
/// Generic host-managed extension files: default injection with managed-vs-custom recognition,
/// missing/invalid discovery under conditions and scopes, confirmed-download boundary, verified
/// atomic install, single-flight deduplication by required set, caller-cancellation ownership and
/// dispose draining. All transport is an in-memory handler — no test ever reaches the network.
/// </summary>
public sealed class ExtensionManagedFileServiceTests : IDisposable
{
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public int Calls;
        public readonly List<string> RequestedUrls = [];
        public readonly List<string?> UserAgents = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            RequestedUrls.Add(request.RequestUri!.ToString());
            UserAgents.Add(request.Headers.UserAgent.ToString());
            return Task.FromResult(respond(request, cancellationToken));
        }
    }

    private sealed class RecordingInteraction(
        Func<IExtensionManagedFileInteraction, ExtensionManagedFilesPlan,
            Func<IProgress<double>?, CancellationToken, Task<Result>>, CancellationToken, Task<Result>> body)
        : IExtensionManagedFileInteraction
    {
        public int Calls;
        public ExtensionManagedFilesPlan? LastPlan;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RecordingInteraction(bool confirm, Func<Task>? gate = null) : this(async (self, plan, download, token) =>
        {
            if (gate is not null)
            {
                await gate();
            }

            return confirm
                ? await download(new Progress<double>(), token)
                : Result.Failure(AppErrorCodes.ValidationFailed, "User declined the download.");
        })
        {
        }

        public Task<Result> ConfirmAndDownloadAsync(ExtensionManagedFilesPlan plan,
            Func<IProgress<double>?, CancellationToken, Task<Result>> download,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastPlan = plan;
            Entered.TrySetResult();
            return body(this, plan, download, cancellationToken);
        }
    }

    /// <summary>A stream whose reads only finish under cancellation — used for mid-download cancel coverage.</summary>
    private sealed class NeverEndingStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// A source that ignores cancellation entirely: reads block until the test releases them.
    /// Used to prove drains wait for real work to exit, not merely for cancellation to be
    /// requested.
    /// </summary>
    private sealed class ReleasedStream : Stream
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Allow()
        {
            _release.TrySetResult();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await _release.Task.ConfigureAwait(false); // deliberately ignores the token
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"managed-files-{Guid.NewGuid():N}");
    private readonly string _modelsRoot;

    public ExtensionManagedFileServiceTests()
    {
        _modelsRoot = Path.Combine(_root, "models");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static string Sha256Of(byte[] content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    private static ConfigFieldDescriptor FileField(string key = "model-path",
        ConfigValueKind kind = ConfigValueKind.DirectoryPath,
        params ExtensionManagedFile[] files)
    {
        return new ConfigFieldDescriptor(key, "Model files", kind, false, "", "")
        {
            ManagedFiles = files
        };
    }

    private static ExtensionConfigSnapshot Config(params (string Key, string Value)[] values)
    {
        return new ExtensionConfigSnapshot(
            values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), 0);
    }

    private ExtensionManagedFileService Service(StubHandler? handler = null,
        Func<string, string?>? resolver = null)
    {
        return new ExtensionManagedFileService(_modelsRoot, resolver,
            handler is null ? null : new HttpMessageInvoker(handler));
    }

    /// <summary>A FilePath field with two layout-model-conditioned files in different kinds.</summary>
    private static ConfigFieldDescriptor[] LayoutFields()
    {
        return
        [
            new ConfigFieldDescriptor("layout-model", "Layout model", ConfigValueKind.Choice, false, "",
                "pp", Choices: ["pp", "yolo"]),
            FileField("model-path", ConfigValueKind.FilePath,
                new ExtensionManagedFile("inference.onnx", "https://example.test/pp.onnx", 4,
                    DirectoryKind: "pp-models", WhenKey: "layout-model", WhenValue: "pp"),
                new ExtensionManagedFile("yolo.onnx", "https://example.test/yolo.onnx", 4,
                    DirectoryKind: "yolo-models", WhenKey: "layout-model", WhenValue: "yolo"))
        ];
    }

    [Fact]
    public async Task Custom_directory_inventory_never_combines_different_model_variants_at_the_same_target()
    {
        await using ExtensionManagedFileService service = Service();
        ConfigFieldDescriptor[] fields =
        [
            new("variant", "Variant", ConfigValueKind.Choice, false, "", "first", Choices: ["first", "second"]),
            FileField("model-path", ConfigValueKind.DirectoryPath,
                new ExtensionManagedFile("model.onnx", "https://example.test/first.onnx", 4,
                    DirectoryKind: "first", WhenKey: "variant", WhenValue: "first"),
                new ExtensionManagedFile("model.onnx", "https://example.test/second.onnx", 8,
                    DirectoryKind: "second", WhenKey: "variant", WhenValue: "second"))
        ];
        string custom = Path.Combine(_root, "custom");

        IReadOnlyList<ExtensionManagedFileGroup> groups = await service.DescribeGroupsAsync("test.variants", fields,
            Config(("variant", "second"), ("model-path", custom)));

        ExtensionManagedFileDownload file = groups.Should().ContainSingle().Subject.Files.Should().ContainSingle()
            .Subject;
        file.DownloadUrl.Should().Be("https://example.test/second.onnx");
        file.DestinationPath.Should().Be(Path.Combine(custom, "model.onnx"));
        Directory.Exists(custom).Should().BeFalse("inventory is passive and never creates a destination");
    }

    [Fact]
    public void ResolveDefaults_injects_the_managed_directory_and_keeps_custom_values()
    {
        ExtensionManagedFile file = new("det.onnx", "https://example.test/det.onnx",
            DirectoryKind: "test-models");
        ConfigFieldDescriptor[] fields =
        [
            FileField(files: [file])
        ];
        using ExtensionManagedFileService service = Service(resolver: kind =>
            kind == "test-models" ? Path.Combine(_modelsRoot, "test") : null);

        ExtensionConfigSnapshot injected = service.ResolveDefaults("test.ext", fields, Config());
        injected.Values["model-path"].Should().Be(Path.Combine(_modelsRoot, "test"));

        ExtensionConfigSnapshot custom =
            service.ResolveDefaults("test.ext", fields, Config(("model-path", "D:\\custom")));
        custom.Values["model-path"].Should().Be("D:\\custom", "an explicit custom path always wins");
    }

    [Fact]
    public void ResolveDefaults_selects_the_conditioned_file_and_falls_back_for_unknown_kinds()
    {
        ExtensionManagedFile[] files =
        [
            new("pp.onnx", "https://example.test/pp.onnx", DirectoryKind: "pp-models",
                WhenKey: "layout-model", WhenValue: "pp"),
            new("yolo.onnx", "https://example.test/yolo.onnx", DirectoryKind: "yolo-models",
                WhenKey: "layout-model", WhenValue: "yolo")
        ];
        ConfigFieldDescriptor[] fields =
        [
            new("layout-model", "Layout model", ConfigValueKind.Choice, false, "",
                "pp", Choices: ["pp", "yolo"]),
            FileField("model-path", ConfigValueKind.FilePath, files)
        ];
        using ExtensionManagedFileService service = Service(resolver: kind =>
            kind == "yolo-models" ? Path.Combine(_modelsRoot, "yolo") : null);

        ExtensionConfigSnapshot pp = service.ResolveDefaults("test.ext", fields, Config());
        // "pp-models" is unknown to the resolver → modelsRoot/extensions/{id}/{fieldKey}.
        pp.Values["model-path"].Should().Be(Path.Combine(_modelsRoot, "extensions", "test.ext",
            "model-path", "pp.onnx"));

        ExtensionConfigSnapshot yolo = service.ResolveDefaults("test.ext", fields,
            Config(("layout-model", "yolo")));
        yolo.Values["model-path"].Should().Be(Path.Combine(_modelsRoot, "yolo", "yolo.onnx"));
    }

    [Fact]
    public async Task A_saved_managed_default_is_re_resolved_when_the_model_selection_changes()
    {
        string ppDir = Path.Combine(_modelsRoot, "pp");
        string yoloDir = Path.Combine(_modelsRoot, "yolo");
        ConfigFieldDescriptor[] fields = LayoutFields();
        using ExtensionManagedFileService service = Service(resolver: kind =>
            kind == "pp-models" ? ppDir : yoloDir);
        string ppDefault = Path.Combine(ppDir, "inference.onnx");

        // The UI earlier saved the injected PP default; switching layout-model must re-resolve
        // the YOLO default, not treat the PP path as a user override.
        ExtensionConfigSnapshot resolved = service.ResolveDefaults("test.ext", fields,
            Config(("layout-model", "yolo"), ("model-path", ppDefault)));
        string yoloDefault = Path.Combine(yoloDir, "yolo.onnx");
        resolved.Values["model-path"].Should().Be(yoloDefault,
            "a stored value equal to any declared managed destination is the injected default");

        // Planning aims the YOLO bytes at the YOLO destination — never at the stale PP path.
        ExtensionManagedFilesPlan plan = await service.PlanAsync("test.ext", fields,
            Config(("layout-model", "yolo"), ("model-path", ppDefault)));
        plan.Files.Should().ContainSingle().Which.DestinationPath.Should().Be(yoloDefault);
    }

    [Fact]
    public void A_saved_managed_directory_default_re_resolves_to_the_new_kind()
    {
        string ppDir = Path.Combine(_modelsRoot, "pp");
        string yoloDir = Path.Combine(_modelsRoot, "yolo");
        ConfigFieldDescriptor[] fields =
        [
            new("layout-model", "Layout model", ConfigValueKind.Choice, false, "",
                "pp", Choices: ["pp", "yolo"]),
            FileField("model-dir", ConfigValueKind.DirectoryPath,
                new ExtensionManagedFile("inference.onnx", "https://example.test/pp.onnx",
                    DirectoryKind: "pp-models", WhenKey: "layout-model", WhenValue: "pp"),
                new ExtensionManagedFile("yolo.onnx", "https://example.test/yolo.onnx",
                    DirectoryKind: "yolo-models", WhenKey: "layout-model", WhenValue: "yolo"))
        ];
        using ExtensionManagedFileService service = Service(resolver: kind =>
            kind == "pp-models" ? ppDir : yoloDir);

        ExtensionConfigSnapshot resolved = service.ResolveDefaults("test.ext", fields,
            Config(("layout-model", "yolo"), ("model-dir", ppDir)));
        resolved.Values["model-dir"].Should().Be(yoloDir);
    }

    [Fact]
    public void Genuine_custom_paths_survive_selection_changes()
    {
        string ppDir = Path.Combine(_modelsRoot, "pp");
        string yoloDir = Path.Combine(_modelsRoot, "yolo");
        ConfigFieldDescriptor[] fields = LayoutFields();
        using ExtensionManagedFileService service = Service(resolver: kind =>
            kind == "pp-models" ? ppDir : yoloDir);
        string custom = Path.Combine(_root, "elsewhere", "my.onnx");

        ExtensionConfigSnapshot resolved = service.ResolveDefaults("test.ext", fields,
            Config(("layout-model", "yolo"), ("model-path", custom)));
        resolved.Values["model-path"].Should().Be(Path.GetFullPath(custom),
            "a path that matches no declared managed destination is a real override");
    }

    [Fact]
    public async Task Plan_reports_missing_and_invalid_files_only_when_required()
    {
        byte[] real = "weights"u8.ToArray();
        ExtensionManagedFile[] files =
        [
            new("det.onnx", "https://example.test/det.onnx", real.Length, Sha256Of(real),
                DirectoryKind: "m"),
            new("cls.onnx", "https://example.test/cls.onnx", real.Length, Sha256Of(real),
                DirectoryKind: "m", RequiredWhenKey: "use-cls", RequiredWhenValue: "true")
        ];
        ConfigFieldDescriptor[] fields =
        [
            new("use-cls", "Classifier", ConfigValueKind.Boolean, false, "",
                "true"),
            FileField(files: files)
        ];
        string managed = Path.Combine(_modelsRoot, "m");
        using ExtensionManagedFileService service = Service(resolver: _ => managed);

        ExtensionManagedFilesPlan plan = await service.PlanAsync("test.ext", fields, Config());
        plan.Files.Select(file => Path.GetFileName(file.DestinationPath))
            .Should().BeEquivalentTo("det.onnx", "cls.onnx");
        plan.TotalExpectedBytes.Should().Be(real.Length * 2);
        plan.RequiredFiles.Should().HaveCount(2, "the required set covers every applicable file");

        // use-cls=false lifts the classifier requirement.
        plan = await service.PlanAsync("test.ext", fields, Config(("use-cls", "false")));
        plan.Files.Should().ContainSingle().Which.RelativePath.Should().Be("det.onnx");

        // A satisfied file leaves the plan; a size-mismatched one is a flagged replacement.
        Directory.CreateDirectory(managed);
        await File.WriteAllBytesAsync(Path.Combine(managed, "det.onnx"), real);
        plan = await service.PlanAsync("test.ext", fields, Config());
        plan.Files.Should().ContainSingle().Which.RelativePath.Should().Be("cls.onnx");
        plan.RequiredFiles.Should().HaveCount(2);
        await File.WriteAllBytesAsync(Path.Combine(managed, "cls.onnx"), [1, 2]);
        plan = await service.PlanAsync("test.ext", fields, Config());
        plan.Files.Should().ContainSingle().Which.OverwritesExisting.Should().BeTrue();
        plan.Files[0].ExistingLength.Should().Be(2, "plan-time identity feeds the revalidation");
        plan.Files[0].ExistingWriteUtcTicks.Should().Be(
            new FileInfo(Path.Combine(managed, "cls.onnx")).LastWriteTimeUtc.Ticks);
    }

    [Fact]
    public async Task Required_scopes_gate_full_page_only_files()
    {
        ExtensionManagedFile[] files =
        [
            new("layout.onnx", "https://example.test/layout.onnx", 4, DirectoryKind: "m",
                RequiredWhenKey: "use-layout", RequiredWhenValue: "true",
                RequiredScopes: ["document", "pages"])
        ];
        ConfigFieldDescriptor[] fields =
        [
            new("use-layout", "Layout", ConfigValueKind.Boolean, false, "", "true"),
            FileField("layout-path", ConfigValueKind.FilePath, files)
        ];
        using ExtensionManagedFileService service = Service(resolver: _ =>
            Path.Combine(_modelsRoot, "m"));

        (await service.PlanAsync("test.ext", fields, Config(), "document"))
            .Files.Should().ContainSingle("a full-page scope needs the weights");
        (await service.PlanAsync("test.ext", fields, Config(), "region"))
            .IsEmpty.Should().BeTrue("an explicit region crop bypasses layout weights entirely");
        // Null scope cannot disprove the declared list — the file stays required.
        (await service.PlanAsync("test.ext", fields, Config())).Files.Should().ContainSingle();
        (await service.PlanAsync("test.ext", fields, Config(("use-layout", "false")), "document"))
            .IsEmpty.Should().BeTrue("the disabled feature must not demand its weights");
    }

    [Fact]
    public async Task Custom_file_path_redirects_the_declared_file()
    {
        string customDir = Path.Combine(_root, "custom");
        ExtensionManagedFile[] files =
        [
            new("layout.onnx", "https://example.test/layout.onnx", 4, DirectoryKind: "m")
        ];
        ConfigFieldDescriptor[] fields = [FileField("model-path", ConfigValueKind.FilePath, files)];
        using ExtensionManagedFileService service = Service(resolver: _ =>
            Path.Combine(_modelsRoot, "m"));
        string customPath = Path.Combine(customDir, "my-model.onnx");

        ExtensionManagedFilesPlan plan = await service.PlanAsync("test.ext", fields,
            Config(("model-path", customPath)));
        plan.Files.Should().ContainSingle().Which.DestinationPath.Should()
            .Be(Path.GetFullPath(customPath), "the user's explicit file path wins");
    }

    [Fact]
    public async Task Ensure_without_interaction_is_an_actionable_failure_and_never_downloads()
    {
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3])
        });
        ExtensionManagedFile[] files =
        [
            new("det.onnx", "https://example.test/det.onnx", 3, DirectoryKind: "m")
        ];
        ConfigFieldDescriptor[] fields = [FileField(files: files)];
        using ExtensionManagedFileService service = Service(handler, _ =>
            Path.Combine(_modelsRoot, "m"));

        Result result = await service.EnsureAsync("test.ext", fields, Config());
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.NotFound);
        result.ErrorMessage.Should().Contain("det.onnx");
        handler.Calls.Should().Be(0, "headless ensure must never touch the network");
    }

    [Fact]
    public async Task Ensure_asks_the_interaction_once_and_downloads_on_confirmation()
    {
        byte[] content = "onnx"u8.ToArray();
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content)
        });
        ExtensionManagedFile[] files =
        [
            new("det.onnx", "https://example.test/det.onnx", content.Length, Sha256Of(content),
                DirectoryKind: "m")
        ];
        ConfigFieldDescriptor[] fields = [FileField(files: files)];
        using ExtensionManagedFileService service = Service(handler, _ =>
            Path.Combine(_modelsRoot, "m"));
        RecordingInteraction interaction = new(true);
        service.Interaction = interaction;

        Result result = await service.EnsureAsync("test.ext", fields, Config());
        result.IsSuccess.Should().BeTrue();
        interaction.Calls.Should().Be(1);
        interaction.LastPlan!.Files.Should().ContainSingle();
        handler.RequestedUrls.Should().Equal("https://example.test/det.onnx");
        handler.UserAgents.Should().Equal(new[] { "Patchouli" },
            "public model hosts reject anonymous clients — the shipped downloader identifies itself");
        File.ReadAllBytes(Path.Combine(_modelsRoot, "m", "det.onnx")).Should().Equal(content);
    }

    [Fact]
    public async Task Declining_the_confirmation_never_downloads()
    {
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1])
        });
        ExtensionManagedFile[] files =
        [
            new("det.onnx", "https://example.test/det.onnx", 1, DirectoryKind: "m")
        ];
        ConfigFieldDescriptor[] fields = [FileField(files: files)];
        using ExtensionManagedFileService service = Service(handler, _ =>
            Path.Combine(_modelsRoot, "m"));
        service.Interaction = new RecordingInteraction(false);

        Result result = await service.EnsureAsync("test.ext", fields, Config());
        result.IsFailure.Should().BeTrue();
        handler.Calls.Should().Be(0, "a declined confirmation must not start the download");
    }

    [Fact]
    public async Task A_handler_reporting_success_without_downloading_fails_the_ensure()
    {
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1])
        });
        ExtensionManagedFile[] files =
        [
            new("det.onnx", "https://example.test/det.onnx", 1, DirectoryKind: "m")
        ];
        ConfigFieldDescriptor[] fields = [FileField(files: files)];
        using ExtensionManagedFileService service = Service(handler, _ =>
            Path.Combine(_modelsRoot, "m"));
        service.Interaction = new RecordingInteraction((_, _, _, _) =>
            Task.FromResult(Result.Success()));

        Result result = await service.EnsureAsync("test.ext", fields, Config());
        result.IsFailure.Should().BeTrue(
            "a claimed success is re-verified against the same frozen plan inputs");
        result.ErrorCode.Should().Be(AppErrorCodes.NotFound);
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_ensures_share_one_confirmation_and_download()
    {
        byte[] content = [7];
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content)
        });
        ExtensionManagedFile[] files =
        [
            new("det.onnx", "https://example.test/det.onnx", content.Length, DirectoryKind: "m")
        ];
        ConfigFieldDescriptor[] fields = [FileField(files: files)];
        RecordingInteraction interaction = new(true, () => gate.Task);
        using ExtensionManagedFileService service = Service(handler, _ =>
            Path.Combine(_modelsRoot, "m"));
        service.Interaction = interaction;

        Task<Result> first = service.EnsureAsync("test.ext", fields, Config(), "pages");
        await interaction.Entered.Task;
        Task<Result> second = service.EnsureAsync("test.ext", fields, Config(), "pages");
        gate.SetResult();
        Result[] results = await Task.WhenAll(first, second);
        results.Should().AllSatisfy(r => r.IsSuccess.Should().BeTrue());
        interaction.Calls.Should().Be(1, "the page/task prompt storm collapses to one confirmation");
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_smaller_missing_subset_after_a_partial_download_shares_the_flight()
    {
        byte[] first = [1];
        byte[] second = [2];
        string managed = Path.Combine(_modelsRoot, "m");
        TaskCompletionSource downloaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(first)
        });
        ExtensionManagedFile[] files =
        [
            new("a.onnx", "https://example.test/a.onnx", first.Length, DirectoryKind: "m"),
            new("b.onnx", "https://example.test/b.onnx", second.Length, DirectoryKind: "m")
        ];
        ConfigFieldDescriptor[] fields = [FileField(files: files)];
        RecordingInteraction interaction = new(async (_, plan, download, token) =>
        {
            // Simulate a partial landing while the confirmation is still open: the second
            // caller then plans with only 'b.onnx' missing but the same required set.
            Directory.CreateDirectory(managed);
            await File.WriteAllBytesAsync(Path.Combine(managed, "a.onnx"), first, token);
            downloaded.TrySetResult();
            await gate.Task;
            return await download(new Progress<double>(), token);
        });
        using ExtensionManagedFileService service = Service(handler, _ => managed);
        service.Interaction = interaction;

        Task<Result> firstCall = service.EnsureAsync("test.ext", fields, Config(), "pages");
        await downloaded.Task;
        Task<Result> secondCall = service.EnsureAsync("test.ext", fields, Config(), "pages");
        gate.SetResult();
        Result[] results = await Task.WhenAll(firstCall, secondCall);
        results.Should().AllSatisfy(r => r.IsSuccess.Should().BeTrue());
        interaction.Calls.Should().Be(1,
            "single-flight identity is the immutable required set, not the shrinking missing subset");
    }

    [Fact]
    public async Task Different_manifests_for_the_same_destination_never_share_a_flight()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1])
        });
        string managed = Path.Combine(_modelsRoot, "m");
        ConfigFieldDescriptor[] fieldsA =
        [
            FileField(files:
            [
                new ExtensionManagedFile("det.onnx", "https://a.test/det.onnx", 1,
                    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    DirectoryKind: "m")
            ])
        ];
        ConfigFieldDescriptor[] fieldsB =
        [
            FileField(files:
            [
                new ExtensionManagedFile("det.onnx", "https://b.test/det.onnx", 1,
                    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    DirectoryKind: "m")
            ])
        ];
        RecordingInteraction interaction = new(true, () => gate.Task);
        using ExtensionManagedFileService service = Service(handler, _ => managed);
        service.Interaction = interaction;

        Task<Result> first = service.EnsureAsync("test.ext", fieldsA, Config(), "pages");
        await interaction.Entered.Task;
        Task<Result> second = service.EnsureAsync("test.ext", fieldsB, Config(), "pages");
        for (int waited = 0; waited < 500 && interaction.Calls < 2; waited++)
        {
            await Task.Delay(20);
        }

        interaction.Calls.Should().Be(2,
            "a changed URL or pinned digest under the same path is a different required set");
        gate.SetResult();
        await Task.WhenAll(first, second);
        handler.RequestedUrls.Should().BeEquivalentTo("https://a.test/det.onnx",
            "https://b.test/det.onnx");
    }

    [Fact]
    public async Task Cancelling_one_waiter_leaves_the_shared_operation_for_the_rest()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1])
        });
        ExtensionManagedFile[] files =
        [
            new("det.onnx", "https://example.test/det.onnx", 1, DirectoryKind: "m")
        ];
        ConfigFieldDescriptor[] fields = [FileField(files: files)];
        RecordingInteraction interaction = new(true, () => gate.Task);
        using ExtensionManagedFileService service = Service(handler, _ =>
            Path.Combine(_modelsRoot, "m"));
        service.Interaction = interaction;

        using CancellationTokenSource withdrawn = new();
        Task<Result> first = service.EnsureAsync("test.ext", fields, Config(), "pages",
            withdrawn.Token);
        await interaction.Entered.Task;
        Task<Result> second = service.EnsureAsync("test.ext", fields, Config(), "pages");
        await withdrawn.CancelAsync();
        await FluentActions.Invoking(() => first)
            .Should().ThrowAsync<OperationCanceledException>(
                "withdrawing a waiter cancels only that caller's wait");
        gate.SetResult();
        (await second).IsSuccess.Should().BeTrue(
            "the remaining waiter still gets the shared confirmed download");
        interaction.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Cancelling_the_only_waiter_cancels_the_operation_without_network()
    {
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1])
        });
        ExtensionManagedFile[] files =
        [
            new("det.onnx", "https://example.test/det.onnx", 1, DirectoryKind: "m")
        ];
        ConfigFieldDescriptor[] fields = [FileField(files: files)];
        RecordingInteraction interaction = new((_, _, _, token) =>
            Task.Delay(Timeout.Infinite, token).ContinueWith(_ => Result.Success(),
                CancellationToken.None));
        using ExtensionManagedFileService service = Service(handler, _ =>
            Path.Combine(_modelsRoot, "m"));
        service.Interaction = interaction;

        using CancellationTokenSource caller = new();
        Task<Result> ensure = service.EnsureAsync("test.ext", fields, Config(), "pages",
            caller.Token);
        await interaction.Entered.Task;
        await caller.CancelAsync();
        await FluentActions.Invoking(() => ensure).Should().ThrowAsync<OperationCanceledException>();
        handler.Calls.Should().Be(0, "a cancellation before confirmation never reaches transport");
    }

    [Fact]
    public async Task Download_verifies_url_size_and_sha_and_installs_atomically()
    {
        byte[] content = new byte[100];
        new Random(42).NextBytes(content);
        string sha = Sha256Of(content);
        StubHandler handler = new((request, _) => request.RequestUri!.ToString() ==
                                                  "https://example.test/det.onnx"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        string managed = Path.Combine(_modelsRoot, "m");
        using ExtensionManagedFileService service = Service(handler);
        ExtensionManagedFilesPlan plan = new("test.ext", "Test", Config(),
        [
            new ExtensionManagedFileDownload("det.onnx", "https://example.test/det.onnx",
                Path.Combine(managed, "det.onnx"), content.Length, sha)
        ]);

        Result result = await service.DownloadAsync(plan);
        result.IsSuccess.Should().BeTrue();
        File.ReadAllBytes(Path.Combine(managed, "det.onnx")).Should().Equal(content);
        Directory.GetFiles(managed).Should().Equal(new[] { Path.Combine(managed, "det.onnx") },
            "no partial file may survive the atomic install");
    }

    [Fact]
    public async Task A_target_appearing_mid_download_that_satisfies_counts_as_installed()
    {
        byte[] content = [4, 5, 6];
        string managed = Path.Combine(_modelsRoot, "m");
        string target = Path.Combine(managed, "det.onnx");
        StubHandler handler = new((_, _) =>
        {
            Directory.CreateDirectory(managed);
            File.WriteAllBytes(target, content);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            };
        });
        using ExtensionManagedFileService service = Service(handler);
        ExtensionManagedFilesPlan plan = new("test.ext", "Test", Config(),
        [
            new ExtensionManagedFileDownload("det.onnx", "https://example.test/det.onnx", target,
                content.Length, Sha256Of(content))
        ]);

        Result result = await service.DownloadAsync(plan);
        result.IsSuccess.Should().BeTrue(
            "the concurrent writer produced exactly the declared bytes — nothing to fight over");
        File.ReadAllBytes(target).Should().Equal(content);
    }

    [Fact]
    public async Task A_target_appearing_mid_download_with_wrong_content_is_a_conflict()
    {
        byte[] content = [4, 5, 6];
        string managed = Path.Combine(_modelsRoot, "m");
        string target = Path.Combine(managed, "det.onnx");
        StubHandler handler = new((_, _) =>
        {
            Directory.CreateDirectory(managed);
            File.WriteAllBytes(target, [9]);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            };
        });
        using ExtensionManagedFileService service = Service(handler);
        ExtensionManagedFilesPlan plan = new("test.ext", "Test", Config(),
        [
            new ExtensionManagedFileDownload("det.onnx", "https://example.test/det.onnx", target,
                content.Length)
        ]);

        Result result = await service.DownloadAsync(plan);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        File.ReadAllBytes(target).Should().Equal([9],
            "the conflicting user file is never overwritten");
        Directory.GetFiles(managed, "*.part").Should().BeEmpty();
    }

    [Fact]
    public async Task A_confirmed_replacement_revalidates_the_plan_time_file_identity()
    {
        byte[] fresh = [1, 2];
        string managed = Path.Combine(_modelsRoot, "m");
        Directory.CreateDirectory(managed);
        string target = Path.Combine(managed, "det.onnx");
        await File.WriteAllBytesAsync(target, [9]);
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(fresh)
        });
        ExtensionManagedFile[] files =
        [
            new("det.onnx", "https://example.test/det.onnx", fresh.Length, DirectoryKind: "m")
        ];
        ConfigFieldDescriptor[] fields = [FileField(files: files)];
        using ExtensionManagedFileService service = Service(handler, _ => managed);
        ExtensionManagedFilesPlan plan = await service.PlanAsync("test.ext", fields, Config());
        plan.Files.Should().ContainSingle().Which.OverwritesExisting.Should().BeTrue();

        // The user rewrote the file between confirmation and install — a newer write wins.
        await File.WriteAllBytesAsync(target, [8]);
        File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddDays(-1));
        Result result = await service.DownloadAsync(plan);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        File.ReadAllBytes(target).Should().Equal([8],
            "a newer user file between prompt and download is never clobbered");
        handler.Calls.Should().Be(1, "the conflict is detected after the download, at install");

        // The untouched plan-time identity still installs as confirmed.
        plan = await service.PlanAsync("test.ext", fields, Config());
        result = await service.DownloadAsync(plan);
        result.IsSuccess.Should().BeTrue();
        File.ReadAllBytes(target).Should().Equal(fresh);
    }

    [Fact]
    public async Task Download_rejects_wrong_bytes_and_never_installs()
    {
        byte[] content = [1, 2, 3];
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content)
        });
        string managed = Path.Combine(_modelsRoot, "m");
        using ExtensionManagedFileService service = Service(handler);
        ExtensionManagedFilesPlan plan = new("test.ext", "Test", Config(),
        [
            new ExtensionManagedFileDownload("det.onnx", "https://example.test/det.onnx",
                Path.Combine(managed, "det.onnx"), content.Length, Sha256Of([9, 9, 9]))
        ]);

        Result result = await service.DownloadAsync(plan);
        result.IsFailure.Should().BeTrue();
        File.Exists(Path.Combine(managed, "det.onnx")).Should()
            .BeFalse("a failed verification must not install the payload");
        Directory.GetFiles(managed).Should().BeEmpty("the partial must be cleaned up");
    }

    [Fact]
    public async Task Failure_messages_carry_no_url_query_credentials_or_local_paths()
    {
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        string managed = Path.Combine(_modelsRoot, "m");
        using ExtensionManagedFileService service = Service(handler);
        ExtensionManagedFilesPlan plan = new("test.ext", "Test", Config(),
        [
            new ExtensionManagedFileDownload("det.onnx",
                "https://example.test/det.onnx?sig=SECRETTOKEN123&expires=9",
                Path.Combine(managed, "det.onnx"), 3)
        ]);

        Result result = await service.DownloadAsync(plan);
        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().NotContain("SECRETTOKEN123",
            "signed-URL credentials never reach public diagnostics");
        result.ErrorMessage.Should().NotContain("sig=");
        result.ErrorMessage.Should().NotContain(managed,
            "local paths stay out of shareable error text; the UI shows them itself");
        result.ErrorMessage.Should().Contain("det.onnx",
            "the relative declaration name still identifies the failed file");
    }

    [Fact]
    public async Task Download_refuses_to_replace_a_mismatched_user_file_without_the_plan_flag()
    {
        string managed = Path.Combine(_modelsRoot, "m");
        Directory.CreateDirectory(managed);
        string target = Path.Combine(managed, "det.onnx");
        await File.WriteAllBytesAsync(target, [9]);
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2])
        });
        using ExtensionManagedFileService service = Service(handler);
        ExtensionManagedFilesPlan plan = new("test.ext", "Test", Config(),
        [
            new ExtensionManagedFileDownload("det.onnx", "https://example.test/det.onnx", target, 2)
        ]);

        Result result = await service.DownloadAsync(plan);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        File.ReadAllBytes(target).Should().Equal([9], "an unconfirmed overwrite must not touch the user's file");
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Cancellation_removes_only_its_own_partial()
    {
        byte[] first = [1];
        int calls = 0;
        StubHandler handler = new((_, _) =>
        {
            calls++;
            return calls == 1
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(first) }
                : new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StreamContent(new NeverEndingStream()) };
        });
        string managed = Path.Combine(_modelsRoot, "m");
        using ExtensionManagedFileService service = Service(handler);
        using CancellationTokenSource cancellation = new();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(250));
        ExtensionManagedFilesPlan plan = new("test.ext", "Test", Config(),
        [
            new ExtensionManagedFileDownload("a.onnx", "https://example.test/a.onnx",
                Path.Combine(managed, "a.onnx"), first.Length),
            new ExtensionManagedFileDownload("b.onnx", "https://example.test/b.onnx",
                Path.Combine(managed, "b.onnx"), first.Length)
        ]);

        ExtensionManagedFileService downloading = service;
        CancellationToken token = cancellation.Token;
        await FluentActions.Invoking(() => downloading.DownloadAsync(plan, null, token))
            .Should().ThrowAsync<OperationCanceledException>();
        File.Exists(Path.Combine(managed, "a.onnx")).Should()
            .BeTrue("the finished install stays behind");
        File.Exists(Path.Combine(managed, "b.onnx")).Should().BeFalse();
        Directory.GetFiles(managed, "*.part").Should().BeEmpty("only this call's partials may be removed");
    }

    [Fact]
    public async Task DisposeAsync_cancels_and_drains_a_direct_download_before_release()
    {
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NeverEndingStream())
        });
        string managed = Path.Combine(_modelsRoot, "m");
        ExtensionManagedFileService service = Service(handler);
        ExtensionManagedFilesPlan plan = new("test.ext", "Test", Config(),
        [
            new ExtensionManagedFileDownload("b.onnx", "https://example.test/b.onnx",
                Path.Combine(managed, "b.onnx"), 5)
        ]);

        Task<Result> download = service.DownloadAsync(plan);
        // The stream blocks until the linked shutdown token fires — dispose must cancel the
        // real work and wait for its file cleanup before releasing the transport.
        await service.DisposeAsync();
        await FluentActions.Invoking(() => download).Should().ThrowAsync<OperationCanceledException>();
        Directory.GetFiles(managed, "*.part").Should().BeEmpty(
            "the partial was removed before the transport was released — the drain is real");
    }

    [Fact]
    public async Task A_closed_service_rejects_new_operations()
    {
        ExtensionManagedFileService service = Service();
        await service.DisposeAsync();
        ExtensionManagedFilesPlan plan = new("test.ext", "Test", Config(),
        [
            new ExtensionManagedFileDownload("a.onnx", "https://example.test/a.onnx",
                Path.Combine(_modelsRoot, "a.onnx"), 1)
        ]);

        await FluentActions.Invoking(() => service.DownloadAsync(plan))
            .Should().ThrowAsync<ObjectDisposedException>();
        await FluentActions.Invoking(() => service.EnsureAsync("test.ext", [], Config()))
            .Should().ThrowAsync<ObjectDisposedException>();
        await FluentActions.Invoking(() => service.PlanAsync("test.ext", [], Config()))
            .Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public void Plan_and_resolve_outputs_are_read_only()
    {
        ExtensionManagedFile[] files =
        [
            new("det.onnx", "https://example.test/det.onnx", 3, DirectoryKind: "m")
        ];
        ConfigFieldDescriptor[] fields = [FileField(files: files)];
        using ExtensionManagedFileService service = Service(resolver: _ =>
            Path.Combine(_modelsRoot, "m"));

        ExtensionConfigSnapshot resolved = service.ResolveDefaults("test.ext", fields, Config());
        resolved.Values.Should().NotBeAssignableTo<Dictionary<string, string>>(
            "callers must not mutate the shared snapshot through a downcast");
    }

    [Fact]
    public async Task DescribeGroups_collects_all_declared_directories_with_missing_subsets()
    {
        ExtensionManagedFile[] files =
        [
            new("det.onnx", "https://example.test/det.onnx", 3, DirectoryKind: "m",
                RequiredWhenKey: "use-det", RequiredWhenValue: "true"),
            new("cls.onnx", "https://example.test/cls.onnx", 4, DirectoryKind: "m",
                RequiredWhenKey: "use-cls", RequiredWhenValue: "true")
        ];
        ConfigFieldDescriptor[] fields =
        [
            new("use-det", "Det", ConfigValueKind.Boolean, false, "", "true"),
            new("use-cls", "Cls", ConfigValueKind.Boolean, false, "", "false"),
            FileField(files: files)
        ];
        string managed = Path.Combine(_modelsRoot, "m");
        Directory.CreateDirectory(managed);
        await File.WriteAllBytesAsync(Path.Combine(managed, "det.onnx"), [1, 2, 3]);
        using ExtensionManagedFileService service = Service(resolver: _ => managed);

        IReadOnlyList<ExtensionManagedFileGroup> groups =
            await service.DescribeGroupsAsync("test.ext", fields, Config());
        groups.Should().ContainSingle("both files share one managed directory");
        ExtensionManagedFileGroup group = groups[0];
        group.Directory.Should().Be(managed);
        group.Files.Should().HaveCount(2, "inventory shows every declaration, not the selected ones");
        group.Files.Should().NotBeAssignableTo<List<ExtensionManagedFileDownload>>(
            "group lists are read-only snapshots");
        // cls is missing even though use-cls=false — manual management covers all declarations.
        group.Missing.Should().ContainSingle().Which.RelativePath.Should().Be("cls.onnx");
        group.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task Synchronously_completing_ensures_return_results_without_spinning()
    {
        // Every bounded result below completes synchronously inside the flight: a regression
        // that treats "already completed" as "dead" would spin forever instead of returning.
        TimeSpan bound = TimeSpan.FromSeconds(15);
        string managed = Path.Combine(_modelsRoot, "m");
        ExtensionManagedFile[] files =
        [
            new("det.onnx", "https://example.test/det.onnx", 1, DirectoryKind: "m")
        ];
        ConfigFieldDescriptor[] fields = [FileField(files: files)];
        using ExtensionManagedFileService service = Service(new StubHandler((_, _) =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) }),
            _ => managed);

        // Missing + headless: an immediate actionable failure — three times, no flight stacks.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Result result = await service.EnsureAsync("test.ext", fields, Config())
                .WaitAsync(bound);
            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should().Be(AppErrorCodes.NotFound);
        }

        // Synchronous decline → classified failure; one prompt per ensure, never a reprompt loop.
        RecordingInteraction decline = new(false);
        service.Interaction = decline;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Result declined = await service.EnsureAsync("test.ext", fields, Config())
                .WaitAsync(bound);
            declined.IsFailure.Should().BeTrue();
        }

        decline.Calls.Should().Be(2);

        // Synchronous success-without-install → classified failure, still bounded.
        service.Interaction = new RecordingInteraction((_, _, _, _) =>
            Task.FromResult(Result.Success()));
        Result falseSuccess = await service.EnsureAsync("test.ext", fields, Config())
            .WaitAsync(bound);
        falseSuccess.IsFailure.Should().BeTrue();
        falseSuccess.ErrorCode.Should().Be(AppErrorCodes.NotFound);

        // Already satisfied → immediate success with no interaction at all.
        Directory.CreateDirectory(managed);
        await File.WriteAllBytesAsync(Path.Combine(managed, "det.onnx"), [1]);
        service.Interaction = null;
        Result satisfied = await service.EnsureAsync("test.ext", fields, Config())
            .WaitAsync(bound);
        satisfied.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_modal_user_cancellation_propagates_once_and_never_reopens_the_prompt()
    {
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1])
        });
        ExtensionManagedFile[] files =
        [
            new("det.onnx", "https://example.test/det.onnx", 1, DirectoryKind: "m")
        ];
        ConfigFieldDescriptor[] fields = [FileField(files: files)];
        using ExtensionManagedFileService service = Service(handler, _ =>
            Path.Combine(_modelsRoot, "m"));
        // A modal-cancel implemented as a cancelled handler task faults the shared operation
        // without the flight's own cancel flag — that is a real user cancellation for every
        // waiter, never an orphaned flight to retry.
        RecordingInteraction interaction = new((_, _, _, _) =>
            Task.FromCanceled<Result>(new CancellationToken(true)));
        service.Interaction = interaction;

        ExtensionManagedFileService ensuring = service;
        await FluentActions.Invoking(() => ensuring.EnsureAsync("test.ext", fields, Config()))
            .Should().ThrowAsync<OperationCanceledException>();
        interaction.Calls.Should().Be(1,
            "an explicit shared cancellation must not be retried into a second prompt");
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_last_withdrawing_waiter_drains_the_operation_before_its_cancellation_returns()
    {
        ReleasedStream released = new();
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(released)
        });
        ExtensionManagedFile[] files =
        [
            new("det.onnx", "https://example.test/det.onnx", 5, DirectoryKind: "m")
        ];
        ConfigFieldDescriptor[] fields = [FileField(files: files)];
        using ExtensionManagedFileService service = Service(handler, _ =>
            Path.Combine(_modelsRoot, "m"));
        service.Interaction = new RecordingInteraction(true);

        using CancellationTokenSource caller = new();
        Task<Result> ensure = service.EnsureAsync("test.ext", fields, Config(), "pages",
            caller.Token);
        await released.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await caller.CancelAsync();

        // The source ignores cancellation: the withdrawing caller must not complete — and the
        // queue upstream must not see the task as cancelled — while the real download still runs.
        ensure.IsCompleted.Should().BeFalse(
            "the last waiter's cancellation awaits the actual operation drain");
        released.Allow();
        await FluentActions.Invoking(() => ensure)
            .Should().ThrowAsync<OperationCanceledException>();
        Directory.GetFiles(Path.Combine(_modelsRoot, "m"), "*.part").Should()
            .BeEmpty("the drained operation removed its own partial");
    }

    [Fact]
    public async Task Concurrent_DisposeAsync_calls_await_the_same_drain()
    {
        ReleasedStream released = new();
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(released)
        });
        string managed = Path.Combine(_modelsRoot, "m");
        ExtensionManagedFileService service = Service(handler);
        ExtensionManagedFilesPlan plan = new("test.ext", "Test", Config(),
        [
            new ExtensionManagedFileDownload("b.onnx", "https://example.test/b.onnx",
                Path.Combine(managed, "b.onnx"), 5)
        ]);

        Task<Result> download = service.DownloadAsync(plan);
        await released.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Both callers get the same pending shutdown — neither may return while the op runs.
        Task first = service.DisposeAsync().AsTask();
        Task second = service.DisposeAsync().AsTask();
        first.IsCompleted.Should().BeFalse(
            "the drain cannot complete while a cancellation-ignoring source still runs");
        second.IsCompleted.Should().BeFalse();
        released.Allow();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        (await download).IsFailure.Should()
            .BeTrue("the drained read produced zero of the declared five bytes");
        Directory.GetFiles(managed, "*.part").Should().BeEmpty();
    }

    [Fact]
    public async Task DisposeAsync_drains_an_in_flight_ensure_before_releasing()
    {
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1])
        });
        ExtensionManagedFile[] files =
        [
            new("det.onnx", "https://example.test/det.onnx", 1, DirectoryKind: "m")
        ];
        ConfigFieldDescriptor[] fields = [FileField(files: files)];
        RecordingInteraction interaction = new(async (_, _, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Result.Success();
        });
        ExtensionManagedFileService service = Service(handler, _ =>
            Path.Combine(_modelsRoot, "m"));
        service.Interaction = interaction;

        Task<Result> ensure = service.EnsureAsync("test.ext", fields, Config(), "pages");
        await interaction.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.DisposeAsync();
        await FluentActions.Invoking(() => ensure)
            .Should().ThrowAsync<ObjectDisposedException>(
                "shutdown cancellation surfaces as a closed service, and DisposeAsync only " +
                "returned after the flight's real work exited");
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_same_size_rewrite_with_a_preserved_timestamp_is_still_a_conflict()
    {
        byte[] fresh = [1, 2];
        string managed = Path.Combine(_modelsRoot, "m");
        Directory.CreateDirectory(managed);
        string target = Path.Combine(managed, "det.onnx");
        await File.WriteAllBytesAsync(target, [9]);
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(fresh)
        });
        ExtensionManagedFile[] files =
        [
            new("det.onnx", "https://example.test/det.onnx", fresh.Length, DirectoryKind: "m")
        ];
        ConfigFieldDescriptor[] fields = [FileField(files: files)];
        using ExtensionManagedFileService service = Service(handler, _ => managed);
        ExtensionManagedFilesPlan plan = await service.PlanAsync("test.ext", fields, Config());
        plan.Files[0].OverwritesExisting.Should().BeTrue();
        plan.Files[0].ExistingSha256.Should().Be(Sha256Of([9]),
            "identity is pinned from the actual preexisting bytes, not just length+mtime");

        // Same length, same timestamp — only the bytes changed. Length+mtime cannot see this;
        // the captured digest does.
        DateTime preserved = new FileInfo(target).LastWriteTimeUtc;
        await File.WriteAllBytesAsync(target, [8]);
        File.SetLastWriteTimeUtc(target, preserved);
        Result result = await service.DownloadAsync(plan);
        result.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        File.ReadAllBytes(target).Should().Equal([8],
            "a same-size rewrite that kept the timestamp is still newer user content");
    }
}
