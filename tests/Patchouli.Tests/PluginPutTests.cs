using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Host.Plugins;
using Patchouli.Mcp;
using Patchouli.Testing;
using Xunit;

namespace Patchouli.Tests;

/// <summary>
/// Plugin put verb: the manifest declares put on a file and a directory node, the
/// in-memory stdio plugin stores the content and returns the concrete result (its
/// readme-declared semantics), and the host routes, schema-validates and rewrites the
/// result URI onto the mount. Also covers the handshake tool derivation: a declared
/// put requires patchouli.plugin.put, and a find/fetch-only plugin no longer fails
/// the handshake for lacking send. No test ever starts a real process.
/// </summary>
public sealed class PluginPutTests : IDisposable
{
    private const string PluginId = "kwadraten.kuzushi-classifier";

    private static readonly string TestRoot =
        Path.Combine(RepositoryTestEnvironment.TestRoot, "plugins-put");

    private readonly string _root = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _cleanup = [];

    public PluginPutTests()
    {
    }

    public void Dispose()
    {
        foreach (IDisposable item in _cleanup)
        {
            item.Dispose();
        }

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

    [Fact]
    public async Task Put_on_a_declared_file_node_stores_and_returns_the_concrete_result()
    {
        FakePluginState state = new();
        HostPluginApi api = NewRegisteredApi(state, statusVerbs: "\"fetch\",\"put\"");

        Result<McpPluginPutOutcome> outcome = await api.PutAsync(PluginId, "status.json",
            """{"ok":true}""");
        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.State.Should().Be("written");
        state.Puts.Should().ContainSingle();
        state.Puts[0].Uri.Should().Be("status.json");
        state.Puts[0].Content.Should().Be("""{"ok":true}""");
        state.Puts[0].ContentType.Should().Be("application/json");
        state.Store["status.json"].Should().Be("""{"ok":true}""");
        ((JsonElement)outcome.Value.Result!).GetProperty("resource_uri").GetString().Should()
            .Be("patchouli://plugin/kwadraten.kuzushi-classifier/status.json");
    }

    [Fact]
    public async Task Put_on_a_declared_directory_node_works_without_a_schema()
    {
        FakePluginState state = new();
        HostPluginApi api = NewRegisteredApi(state, statusVerbs: "\"fetch\",\"put\"");

        Result<McpPluginPutOutcome> outcome = await api.PutAsync(PluginId, "notes/", "hello notes");
        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.State.Should().Be("written");
        state.Puts[0].ContentType.Should().BeNull();
        state.Store["notes/"].Should().Be("hello notes");
    }

    [Fact]
    public async Task Put_on_an_undeclared_path_fails_and_never_reaches_the_process()
    {
        FakePluginState state = new();
        HostPluginApi api = NewRegisteredApi(state, statusVerbs: "\"fetch\",\"put\"");

        Result<McpPluginPutOutcome> outcome = await api.PutAsync(PluginId, "nope.json",
            """{"ok":true}""");
        outcome.IsFailure.Should().BeTrue();
        outcome.ErrorCode.Should().Be("not_found");
        state.Puts.Should().BeEmpty();
    }

    [Fact]
    public async Task Put_on_a_node_without_the_put_verb_fails()
    {
        FakePluginState state = new();
        HostPluginApi api = NewRegisteredApi(state, statusVerbs: "\"fetch\"");

        Result<McpPluginPutOutcome> outcome = await api.PutAsync(PluginId, "status.json",
            """{"ok":true}""");
        outcome.IsFailure.Should().BeTrue();
        outcome.ErrorCode.Should().Be("invalid_argument");
        outcome.ErrorMessage.Should().Contain("does not declare the put verb");
        state.Puts.Should().BeEmpty();
    }

    [Fact]
    public async Task Put_validates_input_against_the_node_schema()
    {
        FakePluginState state = new();
        HostPluginApi api = NewRegisteredApi(state, statusVerbs: "\"fetch\",\"put\"");

        Result<McpPluginPutOutcome> wrongType = await api.PutAsync(PluginId, "status.json",
            """{"ok":"yes"}""");
        wrongType.IsFailure.Should().BeTrue();
        wrongType.ErrorCode.Should().Be("invalid_argument");

        Result<McpPluginPutOutcome> notJson = await api.PutAsync(PluginId, "status.json",
            "not json");
        notJson.IsFailure.Should().BeTrue();
        notJson.ErrorCode.Should().Be("invalid_argument");
        state.Puts.Should().BeEmpty();
    }

    [Fact]
    public async Task Put_on_an_unregistered_plugin_fails()
    {
        FakePluginState state = new();
        HostPluginApi api = NewRegisteredApi(state, statusVerbs: "\"fetch\",\"put\"");

        Result<McpPluginPutOutcome> outcome = await api.PutAsync("other.plugin", "status.json",
            """{"ok":true}""");
        outcome.IsFailure.Should().BeTrue();
        outcome.ErrorCode.Should().Be("not_found");
        state.Puts.Should().BeEmpty();
    }

    [Fact]
    public void Registry_grant_gates_put_through_AllowsVerb()
    {
        FakePluginState state = new();
        HostPluginApi granted = NewRegisteredApi(state, statusVerbs: "\"fetch\",\"put\"",
            allowedVerbs: ["find", "fetch", "put"]);
        granted.AllowsVerb(PluginId, "put").Should().BeTrue();

        HostPluginApi ungranted = NewRegisteredApi(state, statusVerbs: "\"fetch\",\"put\"",
            allowedVerbs: ["find", "fetch"]);
        ungranted.AllowsVerb(PluginId, "put").Should().BeFalse();
    }

    [Fact]
    public async Task Find_fetch_only_plugin_no_longer_fails_the_handshake_for_lacking_send()
    {
        FakePluginState state = new()
        {
            Tools = ["patchouli.plugin.find", "patchouli.plugin.fetch"]
        };
        HostPluginApi api = NewRegisteredApi(state, statusVerbs: "\"fetch\"",
            allowedVerbs: ["find", "fetch"]);

        Result<McpPluginFetchContent> fetch = await api.FetchAsync(PluginId, "status.json");
        fetch.IsSuccess.Should().BeTrue();
        api.Mounts.Should().ContainSingle(mount => mount.State == "ready");
    }

    [Fact]
    public async Task Declared_put_without_the_process_tool_fails_cleanly_at_handshake()
    {
        FakePluginState state = new()
        {
            Tools = ["patchouli.plugin.find", "patchouli.plugin.fetch"]
        };
        HostPluginApi api = NewRegisteredApi(state, statusVerbs: "\"fetch\",\"put\"");

        Result<McpPluginPutOutcome> outcome = await api.PutAsync(PluginId, "status.json",
            """{"ok":true}""");
        outcome.IsFailure.Should().BeTrue();
        outcome.ErrorCode.Should().Be("unavailable");
        outcome.ErrorMessage.Should().Contain("patchouli.plugin.put");
        api.Mounts.Should().ContainSingle(mount => mount.State == "failed");
        state.Puts.Should().BeEmpty();
    }

    [Fact]
    public async Task Put_with_a_malformed_plugin_result_fails_unavailable()
    {
        FakePluginState state = new() { MalformedPutResult = true };
        HostPluginApi api = NewRegisteredApi(state, statusVerbs: "\"fetch\",\"put\"");

        Result<McpPluginPutOutcome> outcome = await api.PutAsync(PluginId, "status.json",
            """{"ok":true}""");
        outcome.IsFailure.Should().BeTrue();
        outcome.ErrorCode.Should().Be("unavailable");
        outcome.ErrorMessage.Should().Contain("malformed put result");
    }

    [Fact]
    public async Task Put_whose_result_escapes_the_mount_fails_unavailable()
    {
        FakePluginState state = new() { EscapingPutResult = true };
        HostPluginApi api = NewRegisteredApi(state, statusVerbs: "\"fetch\",\"put\"");

        Result<McpPluginPutOutcome> outcome = await api.PutAsync(PluginId, "status.json",
            """{"ok":true}""");
        outcome.IsFailure.Should().BeTrue();
        outcome.ErrorCode.Should().Be("unavailable");
        outcome.ErrorMessage.Should().Contain("outside the mount");
    }

    private string NewPackage(string statusVerbs)
    {
        string directory = Path.Combine(_root, "packages", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "schemas"));
        Directory.CreateDirectory(Path.Combine(directory, ".agents"));
        File.WriteAllText(Path.Combine(directory, ".agents", "plugin-usage.md"),
            "# usage\n\nput stores the given JSON under the node path and returns the stored URI.\n");
        File.WriteAllText(Path.Combine(directory, "schemas", "status.json"),
            """{"type":"object","required":["ok"],"properties":{"ok":{"type":"boolean"}}}""");
        File.WriteAllText(Path.Combine(directory, PluginManifestLoader.ManifestFileName),
            $$"""
              id = "kwadraten.kuzushi-classifier"
              version = "0.1.0"
              schema = "patchouli.plugin/1"
              host_api = "patchouli.plugins/1"
              name = "Kuzushi Classifier"
              description = "Classifier plugin"
              readme = ".agents/plugin-usage.md"

              [runtime]
              transport = "stdio"
              command = "dotnet"
              args = ["KuzushiClassifierApp.Plugin.dll", "--serve"]

              [[vfs.nodes]]
              path = ""
              kind = "directory"
              verbs = ["find","fetch"]

              [[vfs.nodes]]
              path = "status.json"
              kind = "file"
              verbs = [{{statusVerbs}}]
              media = "application/json"
              schema = "schemas/status.json"

              [[vfs.nodes]]
              path = "notes/"
              kind = "directory"
              verbs = ["find","fetch","put"]
              """);
        return directory;
    }

    private void RegisterPlugin(string directory, string statusVerbs, string[] allowedVerbs)
    {
        string package = NewPackage(statusVerbs);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "dev-plugins.json"), JsonSerializer.Serialize(new[]
        {
            new
            {
                manifest_path = Path.Combine(package, PluginManifestLoader.ManifestFileName),
                enabled = true,
                allowed_verbs = allowedVerbs
            }
        }));
    }

    private sealed class ApiDisposer(HostPluginApi api) : IDisposable
    {
        public void Dispose()
        {
            api.DisposeAsync().AsTask().Wait();
        }
    }

    private HostPluginApi NewRegisteredApi(FakePluginState state, string statusVerbs,
        string[]? allowedVerbs = null)
    {
        string registry = Path.Combine(_root, "registry", Guid.NewGuid().ToString("N"));
        RegisterPlugin(registry, statusVerbs,
            allowedVerbs ?? ["find", "fetch", "put"]);
        HostPluginApi api = new(Path.Combine(registry, "dev-plugins.json"),
            new FakePluginLauncher(state));
        _cleanup.Add(new ApiDisposer(api));
        return api;
    }

    /// <summary>
    /// In-memory stdio plugin: stores put content under the node path and returns the
    /// concrete result the readme declares (state + resource_uri + byte count).
    /// </summary>
    private sealed class FakePluginState
    {
        public string[] Tools { get; set; } =
            ["patchouli.plugin.find", "patchouli.plugin.fetch", "patchouli.plugin.put"];

        public bool MalformedPutResult { get; set; }
        public bool EscapingPutResult { get; set; }

        public List<(string Uri, string Content, string? ContentType)> Puts { get; } = [];
        public Dictionary<string, string> Store { get; } = new(StringComparer.Ordinal);
    }

    private sealed class FakePluginLauncher(FakePluginState state) : IPluginProcessLauncher
    {
        public IPluginChildProcess Start(PluginProcessSpec spec)
        {
            return new FakePluginProcess(state);
        }
    }

    private sealed class FakePluginProcess : IPluginChildProcess
    {
        private readonly FakePluginState _state;
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _server;
        private int _hasExited;

        public FakePluginProcess(FakePluginState state)
        {
            _state = state;
            _server = Task.Run(ServeAsync);
        }

        public Stream StandardInput => _input.Writer.AsStream();
        public Stream StandardOutput => _output.Reader.AsStream();
        public Stream StandardError => Stream.Null;
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
                using StreamReader reader = new(_input.Reader.AsStream(), Encoding.UTF8, false,
                    4096, true);
                await using Stream writer = _output.Writer.AsStream();
                while (await reader.ReadLineAsync(_lifetime.Token) is { } line)
                {
                    using JsonDocument request = JsonDocument.Parse(line);
                    JsonElement root = request.RootElement;
                    if (!root.TryGetProperty("id", out JsonElement id))
                    {
                        continue; // notifications are ignored
                    }

                    string method = root.GetProperty("method").GetString()!;
                    object result = method switch
                    {
                        "initialize" => new
                        {
                            protocolVersion = "2025-06-18",
                            capabilities = new { }
                        },
                        "tools/list" => new
                        {
                            tools = _state.Tools.Select(name => new { name }).ToArray()
                        },
                        "tools/call" => CallTool(root.GetProperty("params")),
                        _ => throw new InvalidOperationException($"Unexpected method {method}.")
                    };
                    await writer.WriteAsync(Encoding.UTF8.GetBytes(
                            JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }) + "\n"),
                        _lifetime.Token);
                    await writer.FlushAsync(_lifetime.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                await _output.Writer.CompleteAsync();
                Interlocked.Exchange(ref _hasExited, 1);
                _exited.TrySetResult();
            }
        }

        private object CallTool(JsonElement parameters)
        {
            string name = parameters.GetProperty("name").GetString()!;
            JsonElement arguments = parameters.GetProperty("arguments");
            return name switch
            {
                "patchouli.plugin.find" => new
                {
                    structuredContent = new { entries = Array.Empty<object>() }
                },
                "patchouli.plugin.fetch" => new
                {
                    structuredContent = new
                    {
                        uri = arguments.GetProperty("uri").GetString(),
                        content_type = "application/json",
                        content = """{"ok":true}"""
                    }
                },
                "patchouli.plugin.put" => Put(arguments),
                _ => throw new InvalidOperationException($"Unexpected tool {name}.")
            };
        }

        private object Put(JsonElement arguments)
        {
            string uri = arguments.GetProperty("uri").GetString()!;
            string content = arguments.GetProperty("content").GetString()!;
            string? contentType = arguments.TryGetProperty("content_type", out JsonElement media)
                ? media.GetString()
                : null;
            _state.Puts.Add((uri, content, contentType));
            _state.Store[uri] = content;
            if (_state.MalformedPutResult)
            {
                return new { structuredContent = new { } };
            }

            string resourceUri = _state.EscapingPutResult ? "../outside.json" : uri;
            return new
            {
                structuredContent = new
                {
                    state = "written",
                    result = new
                    {
                        resource_uri = resourceUri,
                        bytes = Encoding.UTF8.GetByteCount(content)
                    }
                }
            };
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
            _lifetime.Dispose();
        }
    }
}
