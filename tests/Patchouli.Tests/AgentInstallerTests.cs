using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using FluentAssertions;
using Patchouli.Core.Agents;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Agents;

namespace Patchouli.Tests;

/// <summary>
/// Issue #19 D: <see cref="AgentInstaller"/> downloads an archive, extracts it into a sibling staging
/// directory, and only then swaps it into the managed installation directory. The download is served
/// by an in-memory HTTP handler so no network or local port is involved.
/// </summary>
public sealed class AgentInstallerTests : IDisposable
{
    private static readonly Uri ZipUrl = new("https://registry.example.test/opencode.zip");
    private static readonly Uri TarGzUrl = new("https://registry.example.test/opencode.tar.gz");

    private readonly AgentTestDirectory _directory = new();
    private readonly StubHttpHandler _handler = new();
    private readonly HttpClient _client;
    private readonly string _installRoot;

    public AgentInstallerTests()
    {
        _installRoot = _directory.Combine("install");
        _client = new HttpClient(_handler, false) { Timeout = TimeSpan.FromSeconds(120) };
        RespondWith([]);
    }

    public void Dispose()
    {
        _client.Dispose();
        _directory.Dispose();
    }

    [Fact]
    public async Task A_zip_archive_is_extracted_and_activated_with_progress()
    {
        RespondWith(CreateZip(("opencode", "version-one")));
        RecordingProgress progress = new();

        Result<AgentInstallResult> result = await CreateInstaller().InstallAsync(ZipOffer(), progress);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        AgentInstallResult install = result.Value;
        install.AgentId.Should().Be("opencode");
        install.Version.Should().Be("1.19.3");
        install.InstallDirectory.Should().Be(Path.Combine(_installRoot, "opencode"));
        install.ExecutablePath.Should().Be(Path.Combine(_installRoot, "opencode", "opencode"));
        File.ReadAllText(install.ExecutablePath).Should().Be("version-one");
        Directory.Exists(install.InstallDirectory).Should().BeTrue();
        AssertNoStagingResidue();
        progress.Values.Select(value => value.Stage).Should().ContainInOrder(
            AgentInstallStage.Downloading, AgentInstallStage.Extracting, AgentInstallStage.Activating,
            AgentInstallStage.Completed);
        progress.Values.Should().OnlyContain(value => value.Fraction >= 0 && value.Fraction <= 1);
        progress.Values[^1].Fraction.Should().Be(1);
    }

    [Fact]
    public async Task A_tar_gz_archive_is_extracted_and_activated()
    {
        RespondWith(CreateTarGz(("opencode", "tar-version")));

        Result<AgentInstallResult> result = await CreateInstaller().InstallAsync(TarOffer());

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        File.ReadAllText(result.Value.ExecutablePath).Should().Be("tar-version");
        AssertNoStagingResidue();
    }

    [Fact]
    public async Task An_archive_with_one_wrapped_top_level_directory_is_unwrapped()
    {
        RespondWith(CreateZip(("opencode-v1/bin/opencode", "wrapped")));

        Result<AgentInstallResult> result = await CreateInstaller().InstallAsync(ZipOffer("bin/opencode"));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.ExecutablePath.Should().Be(
            Path.Combine(_installRoot, "opencode", "bin", "opencode"));
        File.ReadAllText(result.Value.ExecutablePath).Should().Be("wrapped");
        AssertNoStagingResidue();
    }

    [Fact]
    public async Task A_second_install_atomically_replaces_the_first_without_residue()
    {
        AgentInstaller installer = CreateInstaller();
        RespondWith(CreateZip(("opencode", "old")));
        (await installer.InstallAsync(ZipOffer())).IsSuccess.Should().BeTrue();

        RespondWith(CreateZip(("opencode", "new")));
        Result<AgentInstallResult> second = await installer.InstallAsync(ZipOffer());

        second.IsSuccess.Should().BeTrue(second.ErrorMessage);
        File.ReadAllText(second.Value.ExecutablePath).Should().Be("new");
        Directory.EnumerateDirectories(_installRoot).Select(Path.GetFileName)
            .Should().Equal("opencode");
        AssertNoStagingResidue();
    }

    [Fact]
    public async Task A_failed_reinstall_keeps_the_previous_installation_intact()
    {
        AgentInstaller installer = CreateInstaller();
        RespondWith(CreateZip(("opencode", "good")));
        AgentInstallResult first = (await installer.InstallAsync(ZipOffer())).Value;

        RespondWith("this is not a zip archive"u8.ToArray());
        Result<AgentInstallResult> failed = await installer.InstallAsync(ZipOffer());

        failed.IsFailure.Should().BeTrue();
        failed.ErrorCode.Should().Be(AppErrorCodes.InvalidState);
        File.ReadAllText(first.ExecutablePath).Should().Be("good");
        Directory.Exists(first.InstallDirectory).Should().BeTrue();
        AssertNoStagingResidue();
    }

    [Fact]
    public async Task A_zip_path_traversal_entry_is_rejected_and_cleaned_up()
    {
        RespondWith(CreateZip(("../escape.txt", "escaped")));

        Result<AgentInstallResult> result = await CreateInstaller().InstallAsync(ZipOffer());

        AssertRejected(result, "非法路径");
        Directory.EnumerateFiles(_installRoot, "escape.txt", SearchOption.AllDirectories)
            .Should().BeEmpty();
        Directory.Exists(Path.Combine(_installRoot, "opencode")).Should().BeFalse();
        AssertNoStagingResidue();
    }

    [Fact]
    public async Task A_tar_path_traversal_entry_is_rejected_and_cleaned_up()
    {
        RespondWith(CreateTarGz(("../escape.txt", "escaped")));

        Result<AgentInstallResult> result = await CreateInstaller().InstallAsync(TarOffer());

        AssertRejected(result, "非法路径");
        Directory.EnumerateFiles(_installRoot, "escape.txt", SearchOption.AllDirectories)
            .Should().BeEmpty();
        Directory.Exists(Path.Combine(_installRoot, "opencode")).Should().BeFalse();
        AssertNoStagingResidue();
    }

    [Fact]
    public async Task A_tar_symbolic_link_entry_is_rejected()
    {
        RespondWith(CreateSymlinkTarGz());

        Result<AgentInstallResult> result = await CreateInstaller().InstallAsync(TarOffer());

        AssertRejected(result, "链接");
        Directory.Exists(Path.Combine(_installRoot, "opencode")).Should().BeFalse();
        AssertNoStagingResidue();
    }

    [Fact]
    public async Task An_archive_larger_than_the_download_cap_is_rejected_and_cleaned_up()
    {
        RespondWithStream(new ZeroStream(), 600L * 1024 * 1024);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(120));

        Result<AgentInstallResult> result =
            await CreateInstaller().InstallAsync(ZipOffer(), cancellationToken: cancellation.Token);

        AssertRejected(result, "大小上限");
        Directory.Exists(Path.Combine(_installRoot, "opencode")).Should().BeFalse();
        AssertNoStagingResidue();
    }

    [Fact]
    public async Task An_offer_without_a_platform_binary_is_rejected()
    {
        AgentInstallOffer offer = new("opencode", "OpenCode", "1.19.3", "windows-x86_64", null, null,
            "opencode", [], [], null, "MIT", null);

        Result<AgentInstallResult> result = await CreateInstaller().InstallAsync(offer);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Contain("没有可用的官方安装包");
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://registry.example.test/opencode.zip")]
    public async Task An_invalid_download_url_is_rejected(string url)
    {
        AgentInstallOffer offer = ZipOffer() with { DownloadUrl = url };

        Result<AgentInstallResult> result = await CreateInstaller().InstallAsync(offer);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Contain("下载地址无效");
    }

    [Fact]
    public async Task An_unknown_agent_id_is_rejected_before_downloading()
    {
        AgentInstallOffer offer = ZipOffer() with { AgentId = "not-in-catalog" };

        Result<AgentInstallResult> result = await CreateInstaller().InstallAsync(offer);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Contain("未知的 Agent");
    }

    [Fact]
    public async Task A_file_blocking_the_target_directory_is_reported_as_invalid_state()
    {
        Directory.CreateDirectory(_installRoot);
        File.WriteAllText(Path.Combine(_installRoot, "opencode"), "blocking file");

        Result<AgentInstallResult> result = await CreateInstaller().InstallAsync(ZipOffer());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.InvalidState);
        result.ErrorMessage.Should().Contain("同名文件");
        AssertNoStagingResidue();
    }

    [Fact]
    public async Task A_pre_cancelled_install_cleans_up_without_creating_the_target()
    {
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        CancellationToken cancellationToken = cancellation.Token;

        Func<Task> act = async () => await CreateInstaller().InstallAsync(ZipOffer(),
            cancellationToken: cancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Directory.Exists(Path.Combine(_installRoot, "opencode")).Should().BeFalse();
        AssertNoStagingResidue();
    }

    [Fact]
    public void Find_executable_prefers_the_archive_command_then_the_catalog_command()
    {
        string root = Directory.CreateDirectory(_directory.Combine("find", "archive")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        string archiveExecutable = Path.Combine(root, "bin", "opencode");
        File.WriteAllText(archiveExecutable, "");

        string? found = AgentInstaller.FindExecutable(root, AgentTestCatalog.OpenCode, "bin/opencode");

        found.Should().Be(archiveExecutable);
    }

    [Fact]
    public void Find_executable_falls_back_to_the_catalog_command_name()
    {
        string root = Directory.CreateDirectory(_directory.Combine("find", "fallback")).FullName;
        string executable = Path.Combine(root, OperatingSystem.IsWindows() ? "opencode.exe" : "opencode");
        File.WriteAllText(executable, "");

        string? found = AgentInstaller.FindExecutable(root, AgentTestCatalog.OpenCode, "../unsafe/command");

        found.Should().Be(executable);
    }

    [Fact]
    public void Find_executable_searches_subdirectories_for_a_multi_directory_archive()
    {
        string root = Directory.CreateDirectory(_directory.Combine("find", "nested")).FullName;
        string nested = Directory.CreateDirectory(Path.Combine(root, "vendor", "payload")).FullName;
        string executable = Path.Combine(nested, OperatingSystem.IsWindows() ? "opencode.exe" : "opencode");
        File.WriteAllText(executable, "");

        string? found = AgentInstaller.FindExecutable(root, AgentTestCatalog.OpenCode);

        found.Should().Be(executable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Find_executable_returns_null_for_a_blank_directory(string? directory)
    {
        AgentInstaller.FindExecutable(directory!, AgentTestCatalog.OpenCode).Should().BeNull();
    }

    [Fact]
    public void Find_executable_returns_null_when_no_candidate_exists()
    {
        string root = Directory.CreateDirectory(_directory.Combine("find", "empty")).FullName;

        AgentInstaller.FindExecutable(root, AgentTestCatalog.OpenCode).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("agent id")]
    public void Get_install_directory_rejects_unsafe_agent_ids(string? agentId)
    {
        Action act = () => AgentInstaller.GetInstallDirectory(agentId!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Get_install_directory_is_the_managed_root_plus_the_agent_id()
    {
        string directory = AgentInstaller.GetInstallDirectory(" opencode ");

        directory.Should().Be(Path.Combine(AgentInstaller.GetInstallRoot(), "opencode"));
        Path.IsPathFullyQualified(directory).Should().BeTrue();
    }

    private AgentInstaller CreateInstaller()
    {
        return new AgentInstaller(_client, _installRoot);
    }

    private static AgentInstallOffer ZipOffer(string archiveCommand = "bin/opencode")
    {
        return new AgentInstallOffer(
            "opencode",
            "OpenCode",
            "1.19.3",
            "windows-x86_64",
            ZipUrl.ToString(),
            "00",
            archiveCommand,
            ["acp"],
            [],
            "https://github.com/anomalyco/opencode",
            "MIT",
            "https://opensource.org/license/mit");
    }

    private static AgentInstallOffer TarOffer()
    {
        return ZipOffer() with { DownloadUrl = TarGzUrl.ToString() };
    }

    private void RespondWith(byte[] payload)
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        };
    }

    private void RespondWithStream(Stream stream, long contentLength)
    {
        _handler.Respond = _ =>
        {
            StreamContent content = new(stream);
            content.Headers.ContentLength = contentLength;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };
    }

    private void AssertRejected(Result<AgentInstallResult> result, string message)
    {
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.InvalidState);
        result.ErrorMessage.Should().Contain(message);
    }

    private void AssertNoStagingResidue()
    {
        if (!Directory.Exists(_installRoot))
        {
            return;
        }

        Directory.EnumerateFileSystemEntries(_installRoot)
            .Select(name => Path.GetFileName(name) ?? "")
            .Should().OnlyContain(name => !name.Contains(".download-", StringComparison.Ordinal) &&
                                          !name.Contains(".old-", StringComparison.Ordinal));
    }

    private static byte[] CreateZip(params (string Name, string Content)[] entries)
    {
        using MemoryStream output = new();
        using (ZipArchive archive = new(output, ZipArchiveMode.Create, true))
        {
            foreach ((string name, string content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                using Stream entryStream = entry.Open();
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
                entryStream.Write(bytes);
            }
        }

        return output.ToArray();
    }

    private static byte[] CreateTarGz(params (string Name, string Content)[] entries)
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.Fastest, true))
        using (TarWriter writer = new(gzip, TarEntryFormat.Pax, true))
        {
            foreach ((string name, string content) in entries)
            {
                PaxTarEntry entry = new(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content))
                };
                writer.WriteEntry(entry);
            }
        }

        return output.ToArray();
    }

    private static byte[] CreateSymlinkTarGz()
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.Fastest, true))
        using (TarWriter writer = new(gzip, TarEntryFormat.Pax, true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "opencode")
            {
                LinkName = "opencode-real"
            });
        }

        return output.ToArray();
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Respond(request));
        }
    }

    private sealed class RecordingProgress : IProgress<AgentInstallProgress>
    {
        public List<AgentInstallProgress> Values { get; } = [];

        public void Report(AgentInstallProgress value)
        {
            Values.Add(value);
        }
    }

    private sealed class ZeroStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            buffer.Span.Clear();
            return ValueTask.FromResult(buffer.Length);
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
}
