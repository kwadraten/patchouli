using System.IO.Compression;
using System.Reflection;
using Patchouli.Extensions.Contracts;
using Patchouli.Extensions;

namespace Patchouli.Infrastructure.Translation;

/// <summary>Installs immutable build outputs; this path never invokes the extension compiler.</summary>
public static class BuiltInExtensionArtifacts
{
    public static async Task InstallAsync(string runtimeDirectory, ExtensionPackageSnapshot package,
        CancellationToken cancellationToken = default)
    {
        string fullRuntimeDirectory = Path.GetFullPath(runtimeDirectory);
        string toolchainIdentity = ExtensionArtifact.runtimeToolchainIdentity();
        string artifactKey = ExtensionArtifact.artifactKey(package, toolchainIdentity);
        string root = Path.Combine(fullRuntimeDirectory, ".bundled");
        string destination = Path.Combine(root, artifactKey);
        if (Directory.Exists(destination))
        {
            string artifactRoot = Path.Combine(fullRuntimeDirectory, ".compiled");
            if (ExtensionArtifact.tryReadCached(package, artifactRoot, toolchainIdentity) is not null)
            {
                return;
            }

            // This exact content-addressed location is owned by the bundle installer. Repair only
            // an incomplete/incompatible artifact at the current source+toolchain key.
            Directory.Delete(destination, true);
        }

        Assembly assembly = typeof(BuiltInExtensionArtifacts).Assembly;
        string resource = $"Patchouli.Infrastructure.Extensions.Compiled.{package.Fingerprint}.zip";
        await using Stream? source = assembly.GetManifestResourceStream(resource);
        if (source is null)
        {
            throw new InvalidOperationException(
                $"Precompiled built-in extension '{package.Id}' for artifact key '{artifactKey}' is missing. Rebuild or repair the application bundle.");
        }

        Directory.CreateDirectory(root);
        string staging = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(staging);
        try
        {
            using ZipArchive archive = new(source, ZipArchiveMode.Read, true);
            string stagingPrefix = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                if (!path.StartsWith(stagingPrefix, OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The built-in artifact contains an invalid path.");
                }

                if (entry.Name.Length == 0)
                {
                    Directory.CreateDirectory(path);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using Stream input = entry.Open();
                await using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, true);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(staging, destination);
            }
            catch (IOException) when (Directory.Exists(destination))
            {
                // A concurrent catalog installed the same immutable build artifact first.
            }
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }
        }
    }
}
