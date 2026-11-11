using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using Patchouli.Core.Downloads;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Downloads;
using Patchouli.Host.Extensions;
using Patchouli.Host.Plugins;
using Patchouli.Testing;

namespace Patchouli.Tests;

public sealed class ManagedDownloadProgressTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(true, false, true, true)]
    public async Task Managed_services_publish_live_file_bytes_and_preserve_terminal_outcomes(
        bool plugin, bool unknownLength, bool cancel, bool useHttpClient)
    {
        string root = Path.Combine(RepositoryTestEnvironment.TestRoot,
            "managed-download-progress", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] firstBytes = [1, 2, 3, 4, 5, 6];
            byte[] secondBytes = [11, 12, 13, 14, 15, 16, 17, 18];
            using DownloadProgressTestStream second = new(secondBytes);
            ManagedHandler handler = new(firstBytes, second, unknownLength);
            // HostServices passes a real HttpClient through this base type for plugin files.
            // Its default SendAsync buffers the body unless the service requests headers only.
            using HttpMessageInvoker http = useHttpClient
                ? new HttpClient(handler)
                : new HttpMessageInvoker(handler);
            await using ExtensionManagedFileService extensions = new(root, http: http);
            await using PluginManagedFileService plugins = new(root, http);
            await using DownloadTaskService tasks = new();
            TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ConcurrentQueue<DownloadFileProgress> evidence = new();
            string[] names = cancel ? ["first.bin", "second.bin", "pending.bin"] : ["first.bin", "second.bin"];

            long? Size(int index)
            {
                return unknownLength ? null : index == 0 ? firstBytes.Length : secondBytes.Length;
            }

            string Destination(string name)
            {
                return Path.Combine(root, "test.progress", name);
            }

            ExtensionManagedFilesPlan extensionPlan = new("test.progress", "Progress",
                new ExtensionConfigSnapshot(new Dictionary<string, string>(), 0),
                names.Select((name, index) => new ExtensionManagedFileDownload(name,
                    $"https://progress.invalid/{name}", Destination(name), Size(index))).ToArray());
            PluginManagedFilesPlan pluginPlan = new("test.progress", "Progress",
                names.Select((name, index) => new PluginManagedFileDownload(name,
                    $"https://progress.invalid/{name}", Destination(name), Size(index))).ToArray());
            DownloadTaskHandle handle = tasks.Start("Managed progress",
                ManagedOperation(extensions, plugins, extensionPlan, pluginPlan, plugin, start, evidence),
                files: names.Select((name, index) =>
                    new DownloadFileProgress(name, "queued", 0, Size(index))).ToArray());
            try
            {
                DownloadTaskStatus seeded = tasks.List().Should().ContainSingle().Which;
                seeded.Files.Select(file => file.RelativePath).Should().Equal(names);
                seeded.Files.Should().OnlyContain(file => file.State == "queued" && file.DownloadedBytes == 0);
                evidence.Should().BeEmpty("the worker has not entered either managed service");
                start.TrySetResult();
                // The transport has consumed the first chunk and is blocked on the second.
                // Even a real HttpClient must already have published those bytes here:
                // ResponseContentRead would instead buffer them invisibly until release.
                await second.FirstReported.Task.WaitAsync(Deadline);
                DownloadTaskStatus running = tasks.List().Should().ContainSingle().Which;
                running.State.Should().Be(DownloadTaskService.RunningState);
                handle.Completion.IsCompleted.Should().BeFalse();
                DownloadFileProgress first = running.Files.Single(file => file.RelativePath == "first.bin");
                first.State.Should().Be("succeeded");
                first.DownloadedBytes.Should().Be(firstBytes.Length);
                first.TotalBytes.Should().Be(Size(0));
                DownloadFileProgress partial = running.Files.Single(file => file.RelativePath == "second.bin");
                partial.State.Should().Be("running");
                partial.DownloadedBytes.Should().BeGreaterThan(0,
                        "both raw invokers and real HttpClient transports must report body bytes before release")
                    .And.BeLessThan(secondBytes.Length);
                partial.TotalBytes.Should().Be(Size(1));
                running.IsIndeterminate.Should().Be(unknownLength);
                evidence.Should().Contain(file => file.RelativePath == "second.bin" &&
                                                  file.State == "running" &&
                                                  file.DownloadedBytes == partial.DownloadedBytes);
                if (cancel)
                {
                    running.Files.Single(file => file.RelativePath == "pending.bin").State.Should().Be("queued");
                    tasks.Cancel(handle.TaskId).IsSuccess.Should().BeTrue();
                    Result result = await handle.Completion.WaitAsync(Deadline);
                    result.IsFailure.Should().BeTrue();
                    DownloadTaskStatus cancelled = tasks.List().Single();
                    cancelled.State.Should().Be(DownloadTaskService.CancelledState);
                    cancelled.Files.Single(file => file.RelativePath == "first.bin").Should().Be(first);
                    DownloadFileProgress stopped = cancelled.Files.Single(file => file.RelativePath == "second.bin");
                    stopped.State.Should().Be("cancelled");
                    stopped.DownloadedBytes.Should().Be(partial.DownloadedBytes);
                    stopped.TotalBytes.Should().Be(partial.TotalBytes);
                    DownloadFileProgress pending = cancelled.Files.Single(file => file.RelativePath == "pending.bin");
                    pending.State.Should().Be("cancelled");
                    pending.DownloadedBytes.Should().Be(0);
                    File.Exists(Destination("first.bin")).Should().BeTrue();
                    File.Exists(Destination("second.bin")).Should().BeFalse();
                    File.Exists(Destination("pending.bin")).Should().BeFalse();
                }
                else
                {
                    second.Release();
                    (await handle.Completion.WaitAsync(Deadline)).IsSuccess.Should().BeTrue();
                    DownloadTaskStatus completed = tasks.List().Single();
                    completed.State.Should().Be(DownloadTaskService.SucceededState);
                    completed.Files.Should().OnlyContain(file => file.State == "succeeded");
                    completed.Files.Single(file => file.RelativePath == "first.bin").Should().Be(first);
                    DownloadFileProgress finished = completed.Files.Single(file => file.RelativePath == "second.bin");
                    finished.DownloadedBytes.Should().Be(secondBytes.Length);
                    finished.TotalBytes.Should().Be(Size(1));
                    (await File.ReadAllBytesAsync(Destination("first.bin"))).Should().Equal(firstBytes);
                    (await File.ReadAllBytesAsync(Destination("second.bin"))).Should().Equal(secondBytes);
                }
            }
            finally
            {
                // Unblock actual work before awaiting service disposal, even after an assertion fails.
                start.TrySetResult();
                second.Release();
                await handle.Completion.WaitAsync(Deadline);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static Func<IProgress<double>?, CancellationToken, Task<Result>> ManagedOperation(
        ExtensionManagedFileService extensions, PluginManagedFileService plugins,
        ExtensionManagedFilesPlan extensionPlan, PluginManagedFilesPlan pluginPlan, bool plugin,
        TaskCompletionSource start, ConcurrentQueue<DownloadFileProgress> evidence)
    {
        return async (worker, token) =>
        {
            await start.Task.WaitAsync(token);
            RecordingProgress progress = new((IDownloadProgress)worker!, evidence);
            return plugin
                ? await plugins.DownloadAsync(pluginPlan, progress, token)
                : await extensions.DownloadAsync(extensionPlan, progress, token);
        };
    }

    private sealed class RecordingProgress(
        IDownloadProgress inner,
        ConcurrentQueue<DownloadFileProgress> evidence) : IDownloadProgress
    {
        public void Report(double value)
        {
            inner.Report(value);
        }

        public void ReportFile(DownloadFileProgress value)
        {
            evidence.Enqueue(value);
            inner.ReportFile(value);
        }
    }

    private sealed class ManagedHandler(
        byte[] first,
        DownloadProgressTestStream second,
        bool unknownLength) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            Stream body = path switch
            {
                "/first.bin" => new DownloadProgressTestStream(first, false),
                "/second.bin" => second,
                _ => throw new InvalidOperationException($"Unexpected managed download: {path}")
            };
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new StreamContent(body) };
            response.Content.Headers.ContentLength =
                unknownLength ? null : path == "/first.bin" ? first.Length : second.ByteCount;
            return Task.FromResult(response);
        }
    }
}

/// <summary>
/// Nonseekable transport body shared by the real managed-file and CSL path tests.
/// Entering the second read proves the consumer has processed/reported the first read.
/// </summary>
internal sealed class DownloadProgressTestStream(byte[] bytes, bool gated = true) : Stream
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _position;

    public TaskCompletionSource FirstReported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int ByteCount => bytes.Length;

    public void Release()
    {
        _release.TrySetResult();
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_position > 0 && gated)
        {
            FirstReported.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        int remaining = bytes.Length - _position;
        int count = Math.Min(buffer.Length, _position == 0 ? Math.Max(1, bytes.Length / 2) : remaining);
        if (remaining == 0)
        {
            return 0;
        }

        bytes.AsMemory(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
        CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public override void Flush()
    {
        throw new NotSupportedException();
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
