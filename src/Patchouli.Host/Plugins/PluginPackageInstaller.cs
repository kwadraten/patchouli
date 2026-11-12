using System.IO.Compression;
using System.Security.Cryptography;
using Patchouli.Core.Downloads;
using Patchouli.Core.Results;

namespace Patchouli.Host.Plugins;

internal static class PluginPackageInstaller
{
    private const int MaxFiles = 512;
    private const long MaxPackageBytes = 512 * 1024 * 1024;
    private const long MaxExpandedBytes = 1024 * 1024 * 1024;

    public static async Task<Result<PluginDefinition>> PrepareAsync(HttpClient client,
        PluginCatalogEntry catalogEntry, PluginCatalogArtifact artifact, string packageDirectory,
        string archivePath, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        try
        {
            Result downloaded = await DownloadVerifiedAsync(client, artifact.Url, artifact.Size,
                artifact.Sha256, archivePath, cancellationToken, progress);
            if (downloaded.IsFailure)
            {
                return Result<PluginDefinition>.Failure(downloaded.ErrorCode!, downloaded.ErrorMessage!);
            }

            ExtractArchive(archivePath, packageDirectory);
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

            if (!definition.RequestedHostVerbs.Order(StringComparer.Ordinal)
                    .SequenceEqual(catalogEntry.RequestedHostVerbs.Order(StringComparer.Ordinal)))
            {
                return Result<PluginDefinition>.Failure(AppErrorCodes.ValidationFailed,
                    "The downloaded package requests host verbs different from the signed index.");
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

    internal static async Task<Result> DownloadVerifiedAsync(HttpClient client, string url, long size,
        string sha256, string archivePath, CancellationToken cancellationToken,
        IProgress<double>? progress = null)
    {
        if (size > MaxPackageBytes)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed,
                $"The package exceeds the {MaxPackageBytes}-byte limit.");
        }

        IDownloadProgress? rich = progress as IDownloadProgress;
        string relative = Uri.TryCreate(url, UriKind.Absolute, out Uri? artifactUri)
            ? Path.GetFileName(artifactUri.AbsolutePath)
            : "package.zip";
        if (relative.Length == 0)
        {
            relative = "package.zip";
        }

        DownloadFileProgress current = new(relative, "running", 0, size);

        void ReportFile(DownloadFileProgress value)
        {
            current = value;
            rich?.ReportFile(value);
        }

        ReportFile(current);
        try
        {
            using HttpResponseMessage response = await client.GetAsync(url,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Result.Failure("unavailable",
                    $"The plugin download returned HTTP {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength is long declared && declared != size)
            {
                Result failure = Result.Failure(AppErrorCodes.ValidationFailed,
                    "The plugin download Content-Length differs from the signed index.");
                rich?.ReportFile(current with { State = "failed", ErrorMessage = failure.ErrorMessage });
                return failure;
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
                if (total > size)
                {
                    Result failure = Result.Failure(AppErrorCodes.ValidationFailed,
                        "The plugin download is larger than the signed index declares.");
                    rich?.ReportFile(current with { State = "failed", ErrorMessage = failure.ErrorMessage });
                    return failure;
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                ReportFile(new DownloadFileProgress(relative, "running", total, size));
            }

            if (total != size)
            {
                Result failure = Result.Failure(AppErrorCodes.ValidationFailed,
                    $"The plugin download size {total} does not match the signed size {size}.");
                rich?.ReportFile(current with { State = "failed", ErrorMessage = failure.ErrorMessage });
                return failure;
            }

            ReportFile(new DownloadFileProgress(relative, "verifying", total, size));
            string actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(actualHash),
                    System.Text.Encoding.ASCII.GetBytes(sha256)))
            {
                Result failure = Result.Failure(AppErrorCodes.ValidationFailed,
                    "The plugin download SHA-256 does not match the signed index.");
                rich?.ReportFile(current with { State = "failed", ErrorMessage = failure.ErrorMessage });
                return failure;
            }

            await destination.FlushAsync(cancellationToken);
            rich?.ReportFile(current with { State = "succeeded" });
            return Result.Success();
        }
        catch (HttpRequestException exception)
        {
            Result failure = Result.Failure("unavailable", $"The plugin download failed: {exception.Message}");
            rich?.ReportFile(current with { State = "failed", ErrorMessage = failure.ErrorMessage });
            return failure;
        }
        catch (OperationCanceledException)
        {
            rich?.ReportFile(current with { State = "cancelled" });
            throw;
        }
    }

    internal static void ExtractArchive(string archivePath, string packageDirectory)
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
