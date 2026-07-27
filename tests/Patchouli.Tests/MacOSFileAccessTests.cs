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
    public async Task MacOSNativeFileAccessAdapter_resolves_directory_symlink()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string realDir = Path.Combine(tempDir, "real");
            Directory.CreateDirectory(realDir);
            string linkDir = Path.Combine(tempDir, "link");
            Directory.CreateSymbolicLink(linkDir, realDir);

            MacOSNativeFileAccessAdapter adapter = new();
            NativeDirectoryResolution resolution = await adapter.ResolveDirectoryAsync(linkDir, default);
            resolution.ResolvedPath.Should().Be(Path.GetFullPath(realDir));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task MacOSNativeFileAccessAdapter_materializes_local_file()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string file = Path.Combine(tempDir, "test.pdf");
            await File.WriteAllTextAsync(file, "pdf content");

            MacOSNativeFileAccessAdapter adapter = new();
            NativeFileMaterialization materialization = await adapter.MaterializeFileAsync(file, default);
            materialization.IsAvailable.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
