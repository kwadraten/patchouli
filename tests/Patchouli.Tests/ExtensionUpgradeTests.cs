using FluentAssertions;
using Microsoft.FSharp.Core;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Tests;

public sealed class ExtensionUpgradeTests
{
    [Theory]
    [InlineData("1.9.0", "1.10.0", ExtensionUpgradeDecision.Upgrade)]
    [InlineData("1.10.0", "1.9.0", ExtensionUpgradeDecision.RejectDowngrade)]
    [InlineData("1.0.0-rc.1", "1.0.0", ExtensionUpgradeDecision.Upgrade)]
    [InlineData("1.0.0", "1.0.0+build", ExtensionUpgradeDecision.RetainModified)]
    public void Uses_semver_precedence(string installed, string incoming, ExtensionUpgradeDecision expected)
    {
        ExtensionUpgrade.Decide(Package(installed), Package(incoming)).Should().Be(expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Normalizes_text_eol_but_preserves_binary_bytes(bool withBinary)
    {
        ExtensionPackageSnapshot lf = Package("1.0.0", "\n", withBinary ? [13, 10] : []);
        ExtensionPackageSnapshot crlf = Package("1.0.0", "\r\n", withBinary ? [13, 10] : []);
        ExtensionPackageSnapshot cr = Package("1.0.0", "\r", withBinary ? [13, 10] : []);
        lf.Fingerprint.Should().Be(crlf.Fingerprint).And.Be(cr.Fingerprint);
        ExtensionUpgrade.Decide(crlf, lf).Should().Be(ExtensionUpgradeDecision.Normalize);
        if (withBinary)
        {
            ExtensionUpgrade.Decide(lf, Package("1.0.0", "\n", [10])).Should()
                .Be(ExtensionUpgradeDecision.RetainModified);
        }
    }

    [Fact]
    public async Task Historical_byte_digest_snapshot_remains_readable_without_renaming_its_directory()
    {
        ExtensionPackageSnapshot package = Package("1.0.0", "\r\n");
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, true))
        {
            foreach ((string path, string content) in package.Sources)
            {
                byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(path);
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
                writer.Write(pathBytes.Length);
                writer.Write(pathBytes);
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
        }

        string historical = Convert.ToHexString(Blake3.Hasher.Hash(stream.ToArray()).AsSpan().ToArray())
            .ToLowerInvariant();
        historical.Should().NotBe(package.Fingerprint);
        string root = Path.Combine(Path.GetTempPath(), $"upgrade-snapshot-{Guid.NewGuid():N}");
        string directory = Path.Combine(root, "extensions", package.Id, ".generations", historical);
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "main.fsx"), package.Sources["main.fsx"]);
            Extensions.IPersistentExtensionFileStore store = new Extensions.FileExtensionStore(root);
            FSharpOption<ExtensionPackageSnapshot>? restored =
                await store.ReadSnapshotAsync(package.Id, historical, CancellationToken.None);
            restored.Should().NotBeNull();
            restored.Value.Fingerprint.Should().Be(package.Fingerprint);
            PackageFingerprint.MatchesSnapshot(package, new string('0', 64)).Should().BeFalse();
            Directory.Exists(directory).Should().BeTrue();
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryWithRetry(root);
        }
    }

    private static ExtensionPackageSnapshot Package(string version, string eol = "\n", byte[]? binary = null)
    {
        string main = $"""
                       /// <summary>Upgrade policy fixture.</summary>
                       /// <patchouli-extension id="test.upgrade" kind="translation" version="{version}" host-api="patchouli.extensions/4">
                       /// <name>Upgrade</name>
                       /// </patchouli-extension>
                       let extension = ()
                       """;
        return new ExtensionPackageSnapshot("test.upgrade",
            new Dictionary<string, string> { ["main.fsx"] = main.Replace("\r\n", "\n").Replace("\n", eol) },
            binary is { Length: > 0 }
                ? new Dictionary<string, ReadOnlyMemory<byte>> { ["asset.bin"] = binary }
                : new Dictionary<string, ReadOnlyMemory<byte>>());
    }
}
