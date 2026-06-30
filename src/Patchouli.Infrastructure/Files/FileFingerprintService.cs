using System.Security.Cryptography;
using Patchouli.Core.Files;
using Patchouli.Core.Results;

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

            return Result<FileFingerprint>.Success(new FileFingerprint(
                normalizedPath,
                fileInfo.Name,
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc,
                quickHash.Value,
                FullBlake3: null));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
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

            using var sha256 = SHA256.Create();
            TransformBlock(sha256, BitConverter.GetBytes(fileInfo.Length));
            await HashWindowAsync(stream, sha256, 0, cancellationToken);

            if (fileInfo.Length > SampleSize)
            {
                var middle = Math.Max(0, (fileInfo.Length / 2) - (SampleSize / 2));
                await HashWindowAsync(stream, sha256, middle, cancellationToken);
            }

            if (fileInfo.Length > SampleSize * 2)
            {
                var tail = Math.Max(0, fileInfo.Length - SampleSize);
                await HashWindowAsync(stream, sha256, tail, cancellationToken);
            }

            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Result<string>.Success(Convert.ToHexString(sha256.Hash!).ToLowerInvariant());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result<string>.Failure(
                AppErrorCodes.DatabaseError,
                $"Quick hash operation failed: {exception.Message}");
        }
    }

    private static async Task HashWindowAsync(
        FileStream stream,
        HashAlgorithm hashAlgorithm,
        long offset,
        CancellationToken cancellationToken)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[SampleSize];
        var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
        if (read > 0)
        {
            hashAlgorithm.TransformBlock(buffer, 0, read, null, 0);
        }
    }

    private static void TransformBlock(HashAlgorithm hashAlgorithm, byte[] inputBuffer)
    {
        hashAlgorithm.TransformBlock(inputBuffer, 0, inputBuffer.Length, null, 0);
    }
}
