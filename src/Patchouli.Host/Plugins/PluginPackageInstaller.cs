using System.IO.Compression;
using System.Security.Cryptography;
using Patchouli.Core.Results;

namespace Patchouli.Host.Plugins;

internal static class PluginPackageInstaller
{
    private const int MaxFiles = 512;
    private const long MaxPackageBytes = 512 * 1024 * 1024;
    private const long MaxExpandedBytes = 1024 * 1024 * 1024;

    public static async Task<Result<PluginDefinition>> PrepareAsync(HttpClient client,
        PluginCatalogEntry catalogEntry, PluginCatalogArtifact artifact, string packageDirectory,
        string archivePath, CancellationToken cancellationToken)
    {
        try
        {
            Result downloaded = await DownloadAsync(client, artifact, archivePath, cancellationToken);
            if (downloaded.IsFailure)
            {
                return Result<PluginDefinition>.Failure(downloaded.ErrorCode!, downloaded.ErrorMessage!);
            }

            Extract(archivePath, packageDirectory);
            string manifestPath = Path.Combine(packageDirectory, PluginManifestLoader.ManifestFileName);
            Result<PluginDefinition> loaded = PluginManifestLoader.Load(manifestPath);
            if (loaded.IsFailure)
            {
                return loaded;
            }

            PluginDefinition definition = loaded.Value;
            if (definition.Id != catalogEntry.Id || definition.Version != catalogEntry.Version)
            {
                return Result<PluginDefinition>.Failure(AppErrorCodes.ValidationFailed,
                    "The downloaded package identity or version differs from the signed index.");
            }

            if (!definition.DeclaredVerbs.Order(StringComparer.Ordinal)
                    .SequenceEqual(catalogEntry.RequestedVerbs.Order(StringComparer.Ordinal)))
            {
                return Result<PluginDefinition>.Failure(AppErrorCodes.ValidationFailed,
                    "The downloaded package requests verbs different from the signed index.");
            }

            return loaded;
        }
        catch (OperationCanceledException)
        {
            return Result<PluginDefinition>.Failure("cancelled", "The plugin installation was cancelled.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException or InvalidOperationException)
        {
            return Result<PluginDefinition>.Failure(AppErrorCodes.ValidationFailed,
                $"The plugin package could not be installed: {exception.Message}");
        }
    }

    private static async Task<Result> DownloadAsync(HttpClient client, PluginCatalogArtifact artifact,
        string archivePath, CancellationToken cancellationToken)
    {
        if (artifact.Size > MaxPackageBytes)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed,
                $"The plugin package exceeds the {MaxPackageBytes}-byte limit.");
        }

        try
        {
            using HttpResponseMessage response = await client.GetAsync(artifact.Url,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Result.Failure("unavailable",
                    $"The plugin download returned HTTP {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength is long declared && declared != artifact.Size)
            {
                return Result.Failure(AppErrorCodes.ValidationFailed,
                    "The plugin download Content-Length differs from the signed index.");
            }

            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using FileStream destination = new(archivePath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, true);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[81920];
            long total = 0;
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > artifact.Size)
                {
                    return Result.Failure(AppErrorCodes.ValidationFailed,
                        "The plugin download is larger than the signed index declares.");
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            if (total != artifact.Size)
            {
                return Result.Failure(AppErrorCodes.ValidationFailed,
                    $"The plugin download size {total} does not match the signed size {artifact.Size}.");
            }

            string actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(actualHash),
                    System.Text.Encoding.ASCII.GetBytes(artifact.Sha256)))
            {
                return Result.Failure(AppErrorCodes.ValidationFailed,
                    "The plugin download SHA-256 does not match the signed index.");
            }

            await destination.FlushAsync(cancellationToken);
            return Result.Success();
        }
        catch (HttpRequestException exception)
        {
            return Result.Failure("unavailable", $"The plugin download failed: {exception.Message}");
        }
    }

    private static void Extract(string archivePath, string packageDirectory)
    {
        Directory.CreateDirectory(packageDirectory);
        string root = Path.GetFullPath(packageDirectory) + Path.DirectorySeparatorChar;
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaxFiles)
        {
            throw new InvalidDataException($"The plugin package contains more than {MaxFiles} entries.");
        }

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string relative = entry.FullName;
            bool directory = relative.EndsWith("/", StringComparison.Ordinal);
            if (relative.Length == 0 || relative.Contains('\\') || relative.Contains('\0') ||
                relative.Contains(':') || relative.StartsWith("/") ||
                relative.Split('/').Any(part => part is "." or ".."))
            {
                throw new InvalidDataException($"Unsafe ZIP entry path '{relative}'.");
            }

            string normalized = relative.TrimEnd('/');
            if (normalized.Length == 0 || !paths.Add(normalized))
            {
                throw new InvalidDataException($"Duplicate or empty ZIP entry path '{relative}'.");
            }

            string destination = Path.GetFullPath(Path.Combine(root, relative));
            if (!destination.StartsWith(root, PathComparison))
            {
                throw new InvalidDataException($"ZIP entry '{relative}' escapes the package directory.");
            }

            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000)
            {
                throw new InvalidDataException($"ZIP entry '{relative}' is a symbolic link.");
            }

            if (directory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExpandedBytes)
            {
                throw new InvalidDataException("The expanded plugin package exceeds the size limit.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using Stream source = entry.Open();
            using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            source.CopyTo(output);
            if (output.Length != entry.Length)
            {
                throw new InvalidDataException($"ZIP entry '{relative}' length changed while extracting.");
            }

            if (!OperatingSystem.IsWindows())
            {
                int permissions = (entry.ExternalAttributes >> 16) & 0x1FF;
                if (permissions != 0)
                {
                    File.SetUnixFileMode(destination, (UnixFileMode)permissions);
                }
            }
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
