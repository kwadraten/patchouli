using FluentAssertions;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Files;

namespace Patchouli.Tests;

public sealed class MacOSFileAccessTests
{
    [Fact]
    public void FileSearchRootAuthorizationKinds_contains_tcc_picker()
    {
        FileSearchRootAuthorizationKinds.TccPicker.Should().Be("tcc_picker");
    }

    [Fact]
    public async Task FileSearchRootAccess_accepts_tcc_picker_for_reopen_and_resolve()
    {
        FileSearchRootAccess access = new();
        FileSearchRoot root = new(
            FileSearchRootId.New(),
            LibraryId.New(),
            "/Users/test/Documents/pdfs",
            true,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            FileSearchRootAuthorizationKinds.TccPicker,
            null,
            null,
            null);

        Result<ResolvedFileSearchRoot> reopened = await access.ReopenAsync(root);
        reopened.IsSuccess.Should().BeTrue();
        reopened.Value.AuthorizationKind.Should().Be(FileSearchRootAuthorizationKinds.TccPicker);

        SelectedFileSearchRoot selected = new(
            "/Users/test/Documents/pdfs",
            "avalonia_storage_provider",
            FileSearchRootAuthorizationKinds.TccPicker,
            null,
            null,
            DateTimeOffset.UtcNow);

        Result<ResolvedFileSearchRoot> resolved = await access.ResolveSelectedAsync(selected);
        resolved.IsSuccess.Should().BeTrue();
        resolved.Value.AuthorizationKind.Should().Be(FileSearchRootAuthorizationKinds.TccPicker);
    }

    [Fact]
    public void PortableNativeFileAccessAdapter_classifies_io_exceptions()
    {
        PortableNativeFileAccessAdapter.Classify(new UnauthorizedAccessException("denied"))
            .Should().Be("access_denied");
        PortableNativeFileAccessAdapter.Classify(new DirectoryNotFoundException("missing"))
            .Should().Be("offline");
        PortableNativeFileAccessAdapter.Classify(new FileNotFoundException("missing"))
            .Should().Be("offline");
        PortableNativeFileAccessAdapter.Classify(new IOException("other"))
            .Should().Be("io_error");
    }

    [Fact]
    public async Task MacOSNativeFileAccessAdapter_resolves_finder_alias_with_interop_on_any_host()
    {
        TestMacOSInterop interop = new()
        {
            ResolveResult = new MacOSNativeCallResult(0, "/Volumes/Library/real", "")
        };
        MacOSNativeFileAccessAdapter adapter = new(interop, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        NativeDirectoryResolution resolution = await adapter.ResolveDirectoryAsync("/Users/test/Finder Alias", default);

        resolution.ResolvedPath.Should().Be("/Volumes/Library/real");
    }

    [Fact]
    public async Task MacOSNativeFileAccessAdapter_classifies_tcc_denial_with_interop_on_any_host()
    {
        TestMacOSInterop interop = new()
        {
            ResolveResult = new MacOSNativeCallResult(-2, null, "TCC denied folder access.")
        };
        MacOSNativeFileAccessAdapter adapter = new(interop, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        NativeDirectoryResolution resolution = await adapter.ResolveDirectoryAsync("/Users/test/Documents", default);

        resolution.ResolvedPath.Should().BeNull();
        resolution.FailureCode.Should().Be("access_denied");
        resolution.FailureReason.Should().Contain("TCC denied");
    }

    [Fact]
    public async Task MacOSNativeFileAccessAdapter_waits_for_icloud_download_with_interop_on_any_host()
    {
        TestMacOSInterop interop = new(new MacOSNativeCallResult(1, null, "Download started."),
            new MacOSNativeCallResult(0, "/Users/test/iCloud.pdf", ""));
        MacOSNativeFileAccessAdapter adapter = new(interop, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        NativeFileMaterialization materialization =
            await adapter.MaterializeFileAsync("/Users/test/iCloud.pdf", default);

        materialization.IsAvailable.Should().BeTrue();
        interop.MaterializeCallCount.Should().Be(2);
    }

    private sealed class TestMacOSInterop(params MacOSNativeCallResult[] materializeResults) : IMacOSFileSystemInterop
    {
        private readonly Queue<MacOSNativeCallResult> _materializeResults = new(materializeResults);

        public MacOSNativeCallResult ResolveResult { get; init; } = new(0, "/resolved", "");
        public int MaterializeCallCount { get; private set; }

        public MacOSNativeCallResult ResolvePath(string path)
        {
            return ResolveResult;
        }

        public MacOSNativeCallResult MaterializeFile(string path)
        {
            MaterializeCallCount++;
            return _materializeResults.Count == 0
                ? new MacOSNativeCallResult(0, path, "")
                : _materializeResults.Count > 1
                    ? _materializeResults.Dequeue()
                    : _materializeResults.Peek();
        }
    }
}
