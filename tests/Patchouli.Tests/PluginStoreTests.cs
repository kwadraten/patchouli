using System.IO.Compression;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NSec.Cryptography;
using Patchouli.Core.Results;
using Patchouli.Host.Plugins;
using Patchouli.Testing;

namespace Patchouli.Tests;

public sealed class PluginStoreTests
{
    private const string PluginId = "example.test-plugin";
    private const string IndexUrl = "https://example.test/index.json";
    private const string SignatureUrl = "https://example.test/index.json.minisig";
    private const string ArtifactUrl = "https://example.test/plugin.zip";

    [Fact]
    public void Minisign_verifies_content_and_trusted_comment()
    {
        using SigningKey key = new();
        byte[] content = "signed plugin index\n"u8.ToArray();
        string signature = key.Sign(content);

        PluginIndexSignature.Verify(content, signature, [key.PublicKey]).IsSuccess.Should().BeTrue();
        byte[] changed = content.ToArray();
        changed[0] ^= 1;
        PluginIndexSignature.Verify(changed, signature, [key.PublicKey]).IsFailure.Should().BeTrue();
        string changedComment = signature.Replace("timestamp:1", "timestamp:2", StringComparison.Ordinal);
        PluginIndexSignature.Verify(content, changedComment, [key.PublicKey]).IsFailure.Should().BeTrue();
        using SigningKey other = new();
        PluginIndexSignature.Verify(content, signature, [other.PublicKey]).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Signed_catalog_uses_only_a_verified_cache()
    {
        using SigningKey key = new();
        byte[] package = Package("0.1.0");
        FakeHttpHandler http = new();
        SetRelease(http, key, "0.1.0", package);
        string root = NewRoot();
        using HttpClient catalogClient = new(http);
        using HttpClient downloadClient = new(http);
        PluginStore store = CreateStore(root, key, catalogClient, downloadClient, new FakeLauncher());

        Result<PluginStoreSnapshot> online = await store.RefreshAsync(true);
        online.IsSuccess.Should().BeTrue(online.ErrorMessage);
        online.Value.FromCache.Should().BeFalse();
        online.Value.Items.Should().ContainSingle();
        online.Value.Items[0].Catalog.Version.Should().Be("0.1.0");

        http.Failure = new HttpRequestException("offline");
        Result<PluginStoreSnapshot> offline = await store.RefreshAsync(true);
        offline.IsSuccess.Should().BeTrue(offline.ErrorMessage);
        offline.Value.FromCache.Should().BeTrue();
        offline.Value.Warning.Should().Contain("verified cache");

        string invalidRoot = NewRoot();
        using PluginStore invalid = CreateStore(invalidRoot, key, catalogClient, downloadClient, new FakeLauncher());
        http.Failure = null;
        http.Routes[IndexUrl] = Encoding.UTF8.GetBytes("{\"tampered\":true}");
        Result<PluginStoreSnapshot> rejected = await invalid.RefreshAsync(true);
        rejected.IsFailure.Should().BeTrue();
        PluginRegistry.Load(PluginRegistry.RegistryPath(invalidRoot)).Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Install_update_disable_enable_and_rollback_share_the_registry()
    {
        using SigningKey key = new();
        FakeHttpHandler http = new();
        SetRelease(http, key, "0.1.0", Package("0.1.0"));
        string root = NewRoot();
        FakeLauncher launcher = new();
        using HttpClient catalogClient = new(http);
        using HttpClient downloadClient = new(http);
        using PluginStore store = CreateStore(root, key, catalogClient, downloadClient, launcher);
        (await store.RefreshAsync(true)).IsSuccess.Should().BeTrue();

        Result<PluginDefinition> installed = await store.InstallAsync(PluginId);
        installed.IsSuccess.Should().BeTrue(installed.ErrorMessage);
        launcher.Starts.Should().Be(1);
        PluginRegistryEntry current = PluginRegistry.Load(PluginRegistry.RegistryPath(root)).Entries.Single();
        current.Source.Should().Be("community");
        current.SourceRevision.Should().Be(new string('a', 40));
        current.AllowedVerbs.Should().BeEquivalentTo("find", "fetch");

        (await store.SetEnabledAsync(PluginId, false)).IsSuccess.Should().BeTrue();
        PluginRegistry.Load(PluginRegistry.RegistryPath(root)).Entries.Should().BeEmpty();
        PluginRegistry.Load(PluginRegistry.RegistryPath(root), true).Entries.Single().Enabled.Should().BeFalse();
        (await store.SetEnabledAsync(PluginId, true)).IsSuccess.Should().BeTrue();

        SetRelease(http, key, "0.2.0", Package("0.2.0"));
        (await store.RefreshAsync(true)).IsSuccess.Should().BeTrue();
        Result<PluginStoreSnapshot> beforeUpdate = await store.RefreshAsync(false);
        beforeUpdate.Value.Items.Single().UpdateAvailable.Should().BeTrue();
        (await store.InstallAsync(PluginId)).IsSuccess.Should().BeTrue();
        Result<PluginStoreSnapshot> updated = await store.RefreshAsync(false);
        updated.Value.Items.Single().InstalledVersion.Should().Be("0.2.0");
        updated.Value.Items.Single().RollbackVersion.Should().Be("0.1.0");

        Result<PluginDefinition> rollback = await store.ActivateAsync(PluginId, "0.1.0");
        rollback.IsSuccess.Should().BeTrue(rollback.ErrorMessage);
        PluginManifestLoader.Load(PluginRegistry.Load(PluginRegistry.RegistryPath(root)).Entries.Single()
            .ManifestPath).Value.Version.Should().Be("0.1.0");
        launcher.Starts.Should().Be(3);
        Result<PluginStoreSnapshot> rolledBack = await store.RefreshAsync(false);
        rolledBack.Value.Items.Single().RollbackVersion.Should().BeNull();
        (await store.ActivateAsync(PluginId, "0.2.0")).IsSuccess.Should().BeTrue();
        launcher.Starts.Should().Be(4);
    }

    [Fact]
    public async Task Hash_mismatch_and_zip_traversal_never_register_or_launch()
    {
        using SigningKey key = new();
        FakeHttpHandler http = new();
        byte[] package = Package("0.1.0");
        SetRelease(http, key, "0.1.0", package);
        string root = NewRoot();
        FakeLauncher launcher = new();
        using HttpClient catalogClient = new(http);
        using HttpClient downloadClient = new(http);
        using PluginStore store = CreateStore(root, key, catalogClient, downloadClient, launcher);
        (await store.RefreshAsync(true)).IsSuccess.Should().BeTrue();

        byte[] changed = package.ToArray();
        changed[^1] ^= 1;
        http.Routes[ArtifactUrl] = changed;
        Result<PluginDefinition> badHash = await store.InstallAsync(PluginId);
        badHash.IsFailure.Should().BeTrue();
        badHash.ErrorMessage.Should().Contain("SHA-256");
        launcher.Starts.Should().Be(0);
        PluginRegistry.Load(PluginRegistry.RegistryPath(root)).Entries.Should().BeEmpty();

        byte[] traversal = Package("0.1.0", "../escape.txt");
        SetRelease(http, key, "0.1.0", traversal);
        (await store.RefreshAsync(true)).IsSuccess.Should().BeTrue();
        Result<PluginDefinition> badPath = await store.InstallAsync(PluginId);
        badPath.IsFailure.Should().BeTrue();
        badPath.ErrorMessage.Should().Contain("Unsafe ZIP entry");
        launcher.Starts.Should().Be(0);
        PluginRegistry.Load(PluginRegistry.RegistryPath(root)).Entries.Should().BeEmpty();
    }

    private static PluginStore CreateStore(string root, SigningKey key, HttpClient catalog, HttpClient download,
        IPluginProcessLauncher launcher)
    {
        return new PluginStore(catalog, download, new PluginStoreOptions
        {
            DataDirectory = root,
            IndexUrl = IndexUrl,
            SignatureUrl = SignatureUrl,
            PublicKeys = [key.PublicKey],
            CurrentPlatform = "win-x64"
        }, launcher);
    }

    private static void SetRelease(FakeHttpHandler http, SigningKey key, string version, byte[] package)
    {
        byte[] index = Index(version, package);
        http.Routes[IndexUrl] = index;
        http.Routes[SignatureUrl] = Encoding.UTF8.GetBytes(key.Sign(index));
        http.Routes[ArtifactUrl] = package;
    }

    private static byte[] Index(string version, byte[] package)
    {
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "patchouli.community.index/1",
            generated_at = "2026-11-07T00:00:00Z",
            plugins = new[]
            {
                new
                {
                    id = PluginId,
                    name = "Test plugin",
                    description = "Integration fixture",
                    version,
                    license = "MIT",
                    repository = "https://github.com/example/plugin",
                    source_revision = new string('a', 40),
                    requested_verbs = new[] { "find", "fetch" },
                    library_grants = Array.Empty<string>(),
                    artifacts = new[]
                    {
                        new
                        {
                            platform = "win-x64",
                            url = ArtifactUrl,
                            size = package.Length,
                            sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(package))
                                .ToLowerInvariant()
                        }
                    }
                }
            }
        });
    }

    private static byte[] Package(string version, string? extraPath = null)
    {
        using MemoryStream output = new();
        using (ZipArchive archive = new(output, ZipArchiveMode.Create, true))
        {
            Add(archive, "patchouli-plugin.toml", $$"""
                                                    schema = "patchouli.plugin/1"
                                                    host-api = "patchouli.plugins/1"
                                                    id = "example.test-plugin"
                                                    version = "{{version}}"
                                                    name = "Test plugin"
                                                    description = "Integration fixture"
                                                    readme = "README.md"

                                                    [runtime]
                                                    transport = "stdio"
                                                    command = "fake"
                                                    args = []

                                                    [library]
                                                    grants = []

                                                    [[vfs.nodes]]
                                                    path = ""
                                                    kind = "directory"
                                                    verbs = ["find", "fetch"]
                                                    summary = "root"

                                                    [[vfs.nodes]]
                                                    path = "status.json"
                                                    kind = "file"
                                                    verbs = ["fetch"]
                                                    media = "application/json"
                                                    schema = "schemas/status.json"
                                                    """);
            Add(archive, "README.md", "# Test plugin\n");
            Add(archive, "schemas/status.json",
                """{"type":"object","required":["ok"],"properties":{"ok":{"type":"boolean"}}}""");
            if (extraPath is not null)
            {
                Add(archive, extraPath, "escape");
            }
        }

        return output.ToArray();
    }

    private static void Add(ZipArchive archive, string path, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path);
        using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string NewRoot()
    {
        string path = Path.Combine(RepositoryTestEnvironment.TestRoot, "plugin-store", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class SigningKey : IDisposable
    {
        private readonly Key _key = Key.Create(SignatureAlgorithm.Ed25519);
        private readonly byte[] _keyId = [1, 2, 3, 4, 5, 6, 7, 8];

        public string PublicKey { get; private set; } = "";

        public SigningKey()
        {
            PublicKey = Convert.ToBase64String("Ed"u8.ToArray().Concat(_keyId)
                .Concat(_key.PublicKey.Export(KeyBlobFormat.RawPublicKey)).ToArray());
        }

        public string Sign(byte[] content)
        {
            byte[] digest = HashAlgorithm.Blake2b_512.Hash(content);
            byte[] signature = SignatureAlgorithm.Ed25519.Sign(_key, digest);
            const string trusted = "timestamp:1\tfile:index.json\thashed";
            byte[] global = SignatureAlgorithm.Ed25519.Sign(_key,
                signature.Concat(Encoding.UTF8.GetBytes(trusted)).ToArray());
            return "untrusted comment: test signature\n" +
                   Convert.ToBase64String("ED"u8.ToArray().Concat(_keyId).Concat(signature).ToArray()) +
                   "\ntrusted comment: " + trusted + "\n" + Convert.ToBase64String(global) + "\n";
        }

        public void Dispose()
        {
            _key.Dispose();
        }
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Routes { get; } = new(StringComparer.Ordinal);
        public HttpRequestException? Failure { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            string url = request.RequestUri!.AbsoluteUri;
            if (!Routes.TryGetValue(url, out byte[]? content))
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            }

            HttpResponseMessage response = new(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            };
            return Task.FromResult(response);
        }
    }

    private sealed class FakeLauncher : IPluginProcessLauncher
    {
        public int Starts { get; private set; }

        public IPluginChildProcess Start(PluginProcessSpec spec)
        {
            Starts++;
            return new FakeProcess();
        }
    }

    private sealed class FakeProcess : IPluginChildProcess
    {
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Stream _standardInput;
        private readonly Stream _standardOutput;
        private readonly MemoryStream _standardError = new();
        private readonly Task _server;
        private int _hasExited;

        public FakeProcess()
        {
            _standardInput = _input.Writer.AsStream();
            _standardOutput = _output.Reader.AsStream();
            _server = Task.Run(ServeAsync);
        }

        public Stream StandardInput => _standardInput;
        public Stream StandardOutput => _standardOutput;
        public Stream StandardError => _standardError;
        public bool HasExited => _hasExited != 0;
        public int? ExitCode => HasExited ? 0 : null;

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            return _exited.Task.WaitAsync(cancellationToken);
        }

        private async Task ServeAsync()
        {
            try
            {
                using StreamReader reader = new(_input.Reader.AsStream(), Encoding.UTF8, false, 4096, true);
                await using Stream writer = _output.Writer.AsStream();
                while (await reader.ReadLineAsync(_lifetime.Token) is { } line)
                {
                    using JsonDocument request = JsonDocument.Parse(line);
                    JsonElement root = request.RootElement;
                    if (!root.TryGetProperty("id", out JsonElement id))
                    {
                        continue;
                    }

                    string method = root.GetProperty("method").GetString()!;
                    object result = method == "initialize"
                        ? new { protocolVersion = "2025-06-18", capabilities = new { } }
                        : new
                        {
                            tools = PluginStdioClient.RequiredTools.Select(name => new { name }).ToArray()
                        };
                    string response = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
                    await writer.WriteAsync(Encoding.UTF8.GetBytes(response + "\n"), _lifetime.Token);
                    await writer.FlushAsync(_lifetime.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                await _output.Writer.CompleteAsync();
                Interlocked.Exchange(ref _hasExited, 1);
                _exited.TrySetResult();
            }
        }

        public void KillTree()
        {
            _lifetime.Cancel();
            Interlocked.Exchange(ref _hasExited, 1);
            _exited.TrySetResult();
        }

        public void Dispose()
        {
            KillTree();
            _standardInput.Dispose();
            _standardOutput.Dispose();
            _standardError.Dispose();
            _lifetime.Dispose();
            _ = _server.Exception;
        }
    }
}
