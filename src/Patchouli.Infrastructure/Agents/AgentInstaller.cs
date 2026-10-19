using System.Formats.Tar;
using System.IO.Compression;
using Patchouli.Core.Agents;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Agents;

/// <summary>One phase of an Agent archive installation.</summary>
public enum AgentInstallStage
{
    Downloading,
    Extracting,
    Activating,
    Completed
}

/// <summary>Progress for one Agent installation. <see cref="Fraction"/> is between 0 and 1.</summary>
/// <param name="Stage">Current phase.</param>
/// <param name="Fraction">Overall completion between 0 and 1.</param>
/// <param name="BytesReceived">Bytes downloaded so far in the <see cref="AgentInstallStage.Downloading"/> phase.</param>
/// <param name="TotalBytes">Content length reported by the server, when available.</param>
public sealed record AgentInstallProgress(
    AgentInstallStage Stage,
    double Fraction,
    long BytesReceived = 0,
    long? TotalBytes = null);

/// <summary>Result of a successful Agent installation.</summary>
/// <param name="AgentId">Catalog id that was installed.</param>
/// <param name="Version">Version advertised by the install offer.</param>
/// <param name="InstallDirectory">Final managed directory of the installation.</param>
/// <param name="ExecutablePath">Executable detected inside the installed directory.</param>
public sealed record AgentInstallResult(
    string AgentId,
    string Version,
    string InstallDirectory,
    string ExecutablePath);

/// <summary>
/// Installs an Agent platform archive from the official registry URL advertised by
/// <see cref="AgentInstallOffer"/>. The archive is downloaded into a sibling staging directory,
/// extracted, and only then promoted to the final directory with a rollback-protected swap. The
/// installer never edits settings; callers persist the resulting executable path themselves.
/// </summary>
public sealed class AgentInstaller
{
    // A guard against an unexpected endless response; official Agent archives are far smaller.
    private const long MaxArchiveBytes = 512L * 1024 * 1024;

    // A guard against a decompression bomb: the downloaded archive is capped, but a highly
    // compressed archive can still expand to an unbounded amount of data on disk.
    private const long MaxExtractedBytes = 2L * 1024 * 1024 * 1024;

    // Ordinary Unix permission bits (rwx for owner, group and others); special bits are not restored.
    private const UnixFileMode UnixPermissionMask = (UnixFileMode)0x1FF;

    private readonly HttpClient _httpClient;
    private readonly string _installRoot;

    public AgentInstaller(HttpClient httpClient, string? installRoot = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _installRoot = string.IsNullOrWhiteSpace(installRoot)
            ? GetInstallRoot()
            : Path.GetFullPath(installRoot);
    }

    /// <summary>
    /// Root directory for managed Agent installations:
    /// <c>%LOCALAPPDATA%\Patchouli\Agents</c> on Windows and
    /// <c>~/.local/share/patchouli/agents</c> on Unix.
    /// </summary>
    public static string GetInstallRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                localAppData = Path.Combine(home, "AppData", "Local");
            }

            return Path.Combine(localAppData, "Patchouli", "Agents");
        }

        string unixHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(unixHome))
        {
            unixHome = AppContext.BaseDirectory;
        }

        return Path.Combine(unixHome, ".local", "share", "patchouli", "agents");
    }

    /// <summary>Managed installation directory for one catalog id.</summary>
    public static string GetInstallDirectory(string agentId)
    {
        ValidateAgentId(agentId);
        return Path.Combine(GetInstallRoot(), agentId.Trim());
    }

    /// <summary>
    /// Finds the executable inside an extracted installation directory. The registry command is
    /// tried first, then the catalog command name at the root and under <c>bin/</c>.
    /// </summary>
    public static string? FindExecutable(string directory, AgentCatalogEntry entry, string? archiveCommand = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        string root = Path.GetFullPath(directory);
        foreach (string relative in EnumerateExecutableCandidates(entry, archiveCommand))
        {
            string candidate = Path.GetFullPath(Path.Combine(root, relative));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return FindExecutableByFileName(root, entry, archiveCommand);
    }

    /// <summary>
    /// Downloads, extracts and atomically installs the platform archive described by
    /// <paramref name="offer"/>. On failure or cancellation the staging directory is removed. A
    /// previous installation is moved aside first and restored if the final move fails.
    /// </summary>
    public Task<Result<AgentInstallResult>> InstallAsync(
        AgentInstallOffer offer,
        IProgress<AgentInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(offer);
        AgentCatalogEntry? entry = AgentCatalog.Find(offer.AgentId);
        if (entry is null)
        {
            return Task.FromResult(Result<AgentInstallResult>.Failure(AppErrorCodes.ValidationFailed,
                $"未知的 Agent：{offer.AgentId}"));
        }

        return InstallCoreAsync(offer, entry, progress, cancellationToken);
    }

    private async Task<Result<AgentInstallResult>> InstallCoreAsync(
        AgentInstallOffer offer,
        AgentCatalogEntry entry,
        IProgress<AgentInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!string.Equals(offer.AgentId.Trim(), entry.Id, StringComparison.Ordinal))
        {
            return Result<AgentInstallResult>.Failure(AppErrorCodes.ValidationFailed,
                "安装信息与 Agent 条目不一致，已拒绝安装。");
        }

        if (!offer.HasPlatformBinary)
        {
            return Result<AgentInstallResult>.Failure(AppErrorCodes.ValidationFailed,
                "当前平台没有可用的官方安装包。");
        }

        // Official install sources must be encrypted. Plain http is rejected even for loopback hosts:
        // an archive fetched over an unauthenticated plaintext channel can be replaced in transit.
        if (!Uri.TryCreate(offer.DownloadUrl?.Trim(), UriKind.Absolute, out Uri? downloadUri) ||
            !string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return Result<AgentInstallResult>.Failure(AppErrorCodes.ValidationFailed,
                "安装包下载地址无效。");
        }

        ValidateAgentId(entry.Id);
        string targetDirectory = Path.Combine(_installRoot, entry.Id);
        if (File.Exists(targetDirectory))
        {
            return Result<AgentInstallResult>.Failure(AppErrorCodes.InvalidState,
                "目标安装路径被同名文件占用，无法创建安装目录。");
        }

        string stamp = Guid.NewGuid().ToString("N");
        string stagingDirectory = targetDirectory + ".download-" + stamp;
        string extractDirectory = Path.Combine(stagingDirectory, "extract");
        string archivePath = Path.Combine(stagingDirectory, GetArchiveFileName(downloadUri));

        try
        {
            Directory.CreateDirectory(extractDirectory);
            progress?.Report(new AgentInstallProgress(AgentInstallStage.Downloading, 0));
            await DownloadAsync(downloadUri, archivePath, progress, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new AgentInstallProgress(AgentInstallStage.Extracting, 0.85));
            ExtractArchive(archivePath, extractDirectory, cancellationToken);

            string contentRoot = ResolveContentRoot(extractDirectory, entry, offer.ArchiveCommand);
            string executable = FindExecutable(contentRoot, entry, offer.ArchiveCommand) ??
                                throw new InvalidDataException("安装包中未找到可执行文件。");
            MakeExecutable(executable);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new AgentInstallProgress(AgentInstallStage.Activating, 0.95));
            string relativeExecutable = Path.GetRelativePath(contentRoot, executable);
            Activate(contentRoot, targetDirectory, stamp);
            progress?.Report(new AgentInstallProgress(AgentInstallStage.Completed, 1));

            return Result<AgentInstallResult>.Success(new AgentInstallResult(
                entry.Id,
                offer.RegistryVersion,
                targetDirectory,
                Path.Combine(targetDirectory, relativeExecutable)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.agent-installer"))
        {
            return Result<AgentInstallResult>.Failure(AppErrorCodes.InvalidState,
                string.IsNullOrWhiteSpace(exception.Message) ? "发生未知安装错误。" : exception.Message);
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private async Task DownloadAsync(
        Uri downloadUri,
        string destinationPath,
        IProgress<AgentInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, downloadUri);
        request.Headers.UserAgent.ParseAdd("Patchouli-AgentInstaller/1.0");
        using HttpResponseMessage response = await _httpClient.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"下载失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        long? totalBytes = response.Content.Headers.ContentLength is > 0
            ? response.Content.Headers.ContentLength
            : null;
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using FileStream destination = new(destinationPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None);
        byte[] buffer = new byte[81920];
        long receivedBytes = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            receivedBytes += read;
            if (receivedBytes > MaxArchiveBytes)
            {
                throw new InvalidDataException("安装包超过允许的大小上限。");
            }

            double fraction = totalBytes is > 0
                ? Math.Min(0.8, 0.8 * receivedBytes / totalBytes.Value)
                : 0;
            progress?.Report(new AgentInstallProgress(AgentInstallStage.Downloading, fraction,
                receivedBytes, totalBytes));
        }
    }

    private static void ExtractArchive(string archivePath, string destinationDirectory,
        CancellationToken cancellationToken)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ExtractZip(archivePath, destinationDirectory, cancellationToken);
            return;
        }

        ExtractTarGz(archivePath, destinationDirectory, cancellationToken);
    }

    private static void ExtractZip(string archivePath, string destinationDirectory,
        CancellationToken cancellationToken)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        long extractedBytes = 0;
        foreach (ZipArchiveEntry zipEntry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = ResolveExtractPath(destinationDirectory, zipEntry.FullName);
            if (string.IsNullOrEmpty(zipEntry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            EnsureExtractionFits(extractedBytes, zipEntry.Length);
            using Stream source = zipEntry.Open();
            extractedBytes = ExtractEntry(source, destination, extractedBytes, cancellationToken);
            ApplyExtractedMetadata(destination, GetZipUnixMode(zipEntry), zipEntry.LastWriteTime);
        }
    }

    private static void ExtractTarGz(string archivePath, string destinationDirectory,
        CancellationToken cancellationToken)
    {
        using FileStream archiveStream = File.OpenRead(archivePath);
        using GZipStream gzipStream = new(archiveStream, CompressionMode.Decompress);
        using TarReader reader = new(gzipStream);
        long extractedBytes = 0;
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = ResolveExtractPath(destinationDirectory, entry.Name);
            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
            {
                throw new InvalidDataException("安装包包含不支持的链接条目。");
            }

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or
                TarEntryType.ContiguousFile))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            EnsureExtractionFits(extractedBytes, entry.Length);
            extractedBytes = ExtractEntry(entry.DataStream, destination, extractedBytes, cancellationToken);
            ApplyExtractedMetadata(destination, entry.Mode, entry.ModificationTime);
        }
    }

    /// <summary>
    /// Rejects an archive whose metadata already claims more expanded data than the remaining budget,
    /// before the entry is written. The streaming count in <see cref="ExtractEntry"/> still guards
    /// against metadata that understates the real decompressed size.
    /// </summary>
    private static void EnsureExtractionFits(long extractedBytes, long declaredEntryBytes)
    {
        if (declaredEntryBytes > MaxExtractedBytes - extractedBytes)
        {
            throw new InvalidDataException("解压后的安装包超过允许的大小上限。");
        }
    }

    /// <summary>
    /// Copies one archive entry while enforcing the expanded-size cap. The byte count is taken from
    /// the decompressed stream rather than the archive metadata so a forged entry size cannot bypass it.
    /// </summary>
    private static long ExtractEntry(Stream? source, string destination, long extractedBytes,
        CancellationToken cancellationToken)
    {
        using FileStream target = new(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        if (source is null)
        {
            return extractedBytes;
        }

        byte[] buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target.Write(buffer, 0, read);
            extractedBytes += read;
            if (extractedBytes > MaxExtractedBytes)
            {
                throw new InvalidDataException("解压后的安装包超过允许的大小上限。");
            }
        }

        return extractedBytes;
    }

    private static UnixFileMode GetZipUnixMode(ZipArchiveEntry entry)
    {
        return (UnixFileMode)((entry.ExternalAttributes >> 16) & (int)UnixPermissionMask);
    }

    /// <summary>
    /// Restores the Unix permission bits and modification time that the archive recorded. The
    /// extraction itself is streamed manually to enforce the size cap, so these two pieces of
    /// metadata are applied explicitly instead of relying on <c>ExtractToFile</c>.
    /// </summary>
    private static void ApplyExtractedMetadata(string path, UnixFileMode unixMode, DateTimeOffset lastWriteTime)
    {
        if (!OperatingSystem.IsWindows() && unixMode != UnixFileMode.None)
        {
            try
            {
                File.SetUnixFileMode(path, unixMode & UnixPermissionMask);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  PlatformNotSupportedException)
            {
                // Permission restoration is advisory; a filesystem without Unix modes is still usable.
                _ = exception;
            }
        }

        if (lastWriteTime != default)
        {
            try
            {
                File.SetLastWriteTimeUtc(path, lastWriteTime.UtcDateTime);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  PlatformNotSupportedException or ArgumentOutOfRangeException)
            {
                // Timestamp preservation is advisory and must not fail an otherwise valid extraction.
                _ = exception;
            }
        }
    }

    private static string ResolveContentRoot(string extractDirectory, AgentCatalogEntry entry,
        string? archiveCommand)
    {
        string[] directories = Directory.GetDirectories(extractDirectory);
        string[] files = Directory.GetFiles(extractDirectory);
        if (directories.Length == 1 && files.Length == 0 &&
            FindExecutable(directories[0], entry, archiveCommand) is not null)
        {
            return directories[0];
        }

        return extractDirectory;
    }

    private static void Activate(string contentRoot, string targetDirectory, string stamp)
    {
        string? parent = Path.GetDirectoryName(targetDirectory);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        string oldDirectory = targetDirectory + ".old-" + stamp;
        bool hadExistingInstallation = Directory.Exists(targetDirectory);
        if (hadExistingInstallation)
        {
            Directory.Move(targetDirectory, oldDirectory);
        }

        try
        {
            Directory.Move(contentRoot, targetDirectory);
        }
        catch
        {
            if (hadExistingInstallation && !Directory.Exists(targetDirectory) &&
                Directory.Exists(oldDirectory))
            {
                try
                {
                    Directory.Move(oldDirectory, targetDirectory);
                }
                catch (Exception rollbackException) when (UnexpectedExceptionReporter.ReportCatch(
                                                              rollbackException,
                                                              "infrastructure.agent-installer-rollback"))
                {
                    // The previous installation remains at the .old path; report the original failure.
                }
            }

            throw;
        }

        if (hadExistingInstallation)
        {
            TryDeleteDirectory(oldDirectory);
        }
    }

    private static string GetArchiveFileName(Uri downloadUri)
    {
        string path = downloadUri.AbsolutePath;
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return "archive.zip";
        }

        if (path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            return "archive.tar.gz";
        }

        return OperatingSystem.IsWindows() ? "archive.zip" : "archive.tar.gz";
    }

    private static IEnumerable<string> EnumerateExecutableCandidates(AgentCatalogEntry entry,
        string? archiveCommand)
    {
        HashSet<string> seen = new(PathComparer);
        string? registryCommand = NormalizeArchiveCommand(archiveCommand);
        if (registryCommand is not null && seen.Add(registryCommand))
        {
            yield return registryCommand;
        }

        string commandName = string.IsNullOrWhiteSpace(entry.CommandName) ? entry.Id : entry.CommandName.Trim();
        string[] fileNames = OperatingSystem.IsWindows()
            ? [commandName + ".exe", commandName]
            : [commandName];
        foreach (string fileName in fileNames)
        {
            foreach (string relative in new[] { fileName, Path.Combine("bin", fileName) })
            {
                if (seen.Add(relative))
                {
                    yield return relative;
                }
            }
        }
    }

    /// <summary>
    /// Fallback for archives that wrap the payload in an extra directory or ship several top-level
    /// files: finds the command executable by file name anywhere below the extraction root.
    /// </summary>
    private static string? FindExecutableByFileName(string root, AgentCatalogEntry entry,
        string? archiveCommand)
    {
        HashSet<string> names = new(PathComparer);
        string? registryCommand = NormalizeArchiveCommand(archiveCommand);
        if (registryCommand is not null)
        {
            names.Add(Path.GetFileName(registryCommand));
        }

        string commandName = string.IsNullOrWhiteSpace(entry.CommandName) ? entry.Id : entry.CommandName.Trim();
        names.Add(commandName);
        if (OperatingSystem.IsWindows())
        {
            names.Add(commandName + ".exe");
        }

        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        string? best = null;
        foreach (string file in Directory.EnumerateFiles(root, "*", options))
        {
            if (!names.Contains(Path.GetFileName(file)))
            {
                continue;
            }

            if (best is null || file.Length < best.Length)
            {
                best = file;
            }
        }

        return best;
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static string? NormalizeArchiveCommand(string? archiveCommand)
    {
        if (string.IsNullOrWhiteSpace(archiveCommand))
        {
            return null;
        }

        string value = archiveCommand.Trim().Replace('\\', '/');
        while (value.StartsWith("./", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        value = value.TrimStart('/');
        if (value.Length == 0)
        {
            return null;
        }

        string[] segments = value.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".."))
        {
            return null;
        }

        return Path.Combine(segments);
    }

    private static string ResolveExtractPath(string rootDirectory, string entryName)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        string relative = (entryName ?? "").Replace('\\', '/').TrimStart('/');
        if (relative.Length == 0)
        {
            return root;
        }

        string candidate = Path.GetFullPath(Path.Combine(root,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = root + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(prefix, comparison) && !string.Equals(candidate, root, comparison))
        {
            throw new InvalidDataException($"安装包包含非法路径：{entryName}");
        }

        return candidate;
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        UnixFileMode mode;
        try
        {
            mode = File.GetUnixFileMode(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              PlatformNotSupportedException)
        {
            mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute |
                                   UnixFileMode.OtherExecute);
    }

    private static void ValidateAgentId(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("Agent id is required.", nameof(agentId));
        }

        string trimmed = agentId.Trim();
        if (trimmed.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')) ||
            trimmed is "." or "..")
        {
            throw new ArgumentException($"Unsafe Agent id: {agentId}", nameof(agentId));
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; a leftover staging directory must not hide the install result.
        }
    }
}
