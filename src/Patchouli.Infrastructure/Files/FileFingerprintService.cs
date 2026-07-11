using Patchouli.Core.Files;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Hashing;

namespace Patchouli.Infrastructure.Files;

public sealed class FileFingerprintService : IFileFingerprintService
{
    private const int SampleSize = 4096;

    public async Task<Result<FileFingerprint>> GetFileMetadataAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Result<FileFingerprint>.Failure(AppErrorCodes.ValidationFailed, "File path is required.");
        }

        try
        {
            var normalizedPath = Path.GetFullPath(path);
            var fileInfo = new FileInfo(normalizedPath);
            if (!fileInfo.Exists)
            {
                return Result<FileFingerprint>.Failure(AppErrorCodes.NotFound, "File was not found.");
            }

            var quickHash = await ComputeQuickHashAsync(normalizedPath, cancellationToken);
            if (quickHash.IsFailure)
            {
                return Result<FileFingerprint>.Failure(quickHash.ErrorCode!, quickHash.ErrorMessage!);
            }

            var fullBlake3 = await Blake3Hash.ComputeFileAsync(normalizedPath, cancellationToken);

            return Result<FileFingerprint>.Success(new FileFingerprint(
                normalizedPath,
                fileInfo.Name,
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc,
                quickHash.Value,
                fullBlake3));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.file-fingerprint"))
        {
            return Result<FileFingerprint>.Failure(
                AppErrorCodes.DatabaseError,
                $"File metadata operation failed: {exception.Message}");
        }
    }

    public async Task<Result<string>> ComputeQuickHashAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Result<string>.Failure(AppErrorCodes.ValidationFailed, "File path is required.");
        }

        try
        {
            var normalizedPath = Path.GetFullPath(path);
            var fileInfo = new FileInfo(normalizedPath);
            if (!fileInfo.Exists)
            {
                return Result<string>.Failure(AppErrorCodes.NotFound, "File was not found.");
            }

            await using var stream = new FileStream(
                normalizedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: SampleSize,
                useAsync: true);

            using var hasher = global::Blake3.Hasher.New();
            hasher.Update(BitConverter.GetBytes(fileInfo.Length));
            await HashWindowAsync(stream, hasher, 0, cancellationToken);

            if (fileInfo.Length > SampleSize)
            {
                var middle = Math.Max(0, (fileInfo.Length / 2) - (SampleSize / 2));
                await HashWindowAsync(stream, hasher, middle, cancellationToken);
            }

            if (fileInfo.Length > SampleSize * 2)
            {
                var tail = Math.Max(0, fileInfo.Length - SampleSize);
                await HashWindowAsync(stream, hasher, tail, cancellationToken);
            }

            return Result<string>.Success(hasher.Finalize().ToString());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.file-fingerprint"))
        {
            return Result<string>.Failure(
                AppErrorCodes.DatabaseError,
                $"Quick hash operation failed: {exception.Message}");
        }
    }

    private static async Task HashWindowAsync(
        FileStream stream,
        global::Blake3.Hasher hasher,
        long offset,
        CancellationToken cancellationToken)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[SampleSize];
        var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
        if (read > 0)
        {
            hasher.Update(buffer.AsSpan(0, read));
        }
    }
}
