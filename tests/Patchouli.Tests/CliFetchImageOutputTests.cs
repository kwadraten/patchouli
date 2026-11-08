using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Patchouli.Cli;

namespace Patchouli.Tests;

/// <summary>
/// End-to-end coverage for <c>fetch --output</c> image saving (issue #11, view→fetch
/// refactoring): the built CLI talks to a fake MCP HTTP host that returns a native
/// image content block, and the PNG must land on disk intact. The --overwrite guard
/// is exercised the same way. The base64 decode, PNG validation, temp-file write
/// and atomic move live in the CLI process itself, so only a real subprocess run
/// covers them.
/// </summary>
public sealed class CliFetchImageOutputTests
{
    // A 1x1 transparent PNG (70 bytes).
    private const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private const string DocId = "11111111-1111-1111-1111-111111111111";

    private static readonly byte[] ExpectedPng = Convert.FromBase64String(PngBase64);

    [Fact]
    public async Task Fetch_output_saves_valid_png()
    {
        string root = CreateTempRoot();
        try
        {
            using FakeMcpImageServer server = new(PngBase64);
            string outputPath = Path.Combine(root, "page-1.png");

            (int exitCode, string output, string error) = await RunCliAsync(server.Url,
                "patchouli://images/" + DocId + "/page-1.png", outputPath, false);

            exitCode.Should().Be(CliExitCode.Ok, error);
            output.Should().Contain($"output_path: {Path.GetFullPath(outputPath)}");
            File.Exists(outputPath).Should().BeTrue();
            (await File.ReadAllBytesAsync(outputPath)).Should().Equal(ExpectedPng);
            server.Requests.Should().ContainSingle(body => body.Contains("\"patchouli.fetch\""));
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryWithRetry(root);
        }
    }

    [Fact]
    public async Task Fetch_output_refuses_to_overwrite_without_flag_and_replaces_with_flag()
    {
        string root = CreateTempRoot();
        try
        {
            using FakeMcpImageServer server = new(PngBase64);
            string outputPath = Path.Combine(root, "page-1.png");
            byte[] sentinel = [0x53, 0x45, 0x4E, 0x54, 0x49, 0x4E, 0x45, 0x4C];
            await File.WriteAllBytesAsync(outputPath, sentinel);

            (int exitCode, _, string error) = await RunCliAsync(server.Url,
                "patchouli://images/" + DocId + "/page-1.png", outputPath, false);

            exitCode.Should().Be(CliExitCode.InvalidArgument, error);
            error.Should().Contain("already exists");
            (await File.ReadAllBytesAsync(outputPath)).Should().Equal(sentinel);

            (exitCode, _, error) = await RunCliAsync(server.Url,
                "patchouli://images/" + DocId + "/page-1.png", outputPath, true);

            exitCode.Should().Be(CliExitCode.Ok, error);
            (await File.ReadAllBytesAsync(outputPath)).Should().Equal(ExpectedPng);
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryWithRetry(root);
        }
    }

    private static string CreateTempRoot()
    {
        return Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"patchouli-cli-fetch-{Guid.NewGuid():N}")).FullName;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCliAsync(
        string mcpUrl, string imageUri, string outputPath, bool overwrite)
    {
        string cliDirectory = AppContext.BaseDirectory;
        string cliPath = Path.Combine(cliDirectory, "patchouli-cli.dll");
        File.Exists(cliPath).Should().BeTrue("the test build copies patchouli-cli.dll next to the test assembly");

        ProcessStartInfo start = new("dotnet")
        {
            WorkingDirectory = cliDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(cliPath);
        start.ArgumentList.Add("--mcp-url");
        start.ArgumentList.Add(mcpUrl);
        start.ArgumentList.Add("fetch");
        start.ArgumentList.Add(imageUri);
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add(outputPath);
        if (overwrite)
        {
            start.ArgumentList.Add("--overwrite");
        }

        using Process process = Process.Start(start)!;
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw new TimeoutException("patchouli-cli did not exit within 30 seconds.");
        }

        return (process.ExitCode, await outputTask, await errorTask);
    }

    /// <summary>
    /// Minimal MCP HTTP host: answers <c>initialize</c> with the protocol version the
    /// CLI requires, and answers any <c>tools/call</c> with one text block plus one
    /// native image block carrying the configured PNG.
    /// </summary>
    private sealed class FakeMcpImageServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly List<string> _requests = [];

        public FakeMcpImageServer(string imageBase64)
        {
            ImageBase64 = imageBase64;
            using TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener = new HttpListener();
            // localhost, not 127.0.0.1: this sandbox answers direct IP-literal TCP
            // connects with its "other_tcp is turned off" message, while the localhost
            // hostname reaches the loopback listener.
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            Url = $"http://localhost:{port}/mcp";
            _loop = ServeAsync(_cts.Token);
        }

        public string Url { get; }

        public string ImageBase64 { get; }

        public IReadOnlyList<string> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToArray();
                }
            }
        }

        private async Task ServeAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }

                _ = HandleAsync(context, token);
            }
        }

        private async Task HandleAsync(HttpListenerContext context, CancellationToken token)
        {
            string body;
            using (StreamReader reader = new(context.Request.InputStream, context.Request.ContentEncoding))
            {
                body = await reader.ReadToEndAsync(token);
            }

            lock (_requests)
            {
                _requests.Add(body);
            }

            string responseJson;
            using (JsonDocument request = JsonDocument.Parse(body))
            {
                JsonElement root = request.RootElement;
                string method = root.GetProperty("method").GetString() ?? "";
                int requestId = root.GetProperty("id").GetInt32();
                object response = method == "initialize"
                    ? new
                    {
                        jsonrpc = "2.0",
                        id = requestId,
                        result = new
                        {
                            protocolVersion = "2025-06-18",
                            capabilities = new { },
                            serverInfo = new { name = "fake-mcp", version = "0" }
                        }
                    }
                    : new
                    {
                        jsonrpc = "2.0",
                        id = requestId,
                        result = new
                        {
                            content = new object[]
                            {
                                new
                                {
                                    type = "text",
                                    text =
                                        "uri: patchouli://images/11111111-1111-1111-1111-111111111111/page-1.png\ntype: page_image\nmime_type: image/png\nwidth: 1\nheight: 1\n"
                                },
                                new { type = "image", mimeType = "image/png", data = ImageBase64 }
                            },
                            isError = false
                        }
                    };
                responseJson = JsonSerializer.Serialize(response);
            }

            byte[] bytes = Encoding.UTF8.GetBytes(responseJson);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, token);
            context.Response.Close();
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
            _cts.Dispose();
        }
    }
}
