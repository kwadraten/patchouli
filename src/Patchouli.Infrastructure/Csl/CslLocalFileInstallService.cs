using System.Security.Cryptography;
using System.Text;
using Patchouli.Core.Csl;
using Patchouli.Core.Import;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Csl;

/// <summary>
/// Issue #11: installs CSL styles from host-local .csl files with create/reuse/conflict
/// semantics in the domain service. Concurrent installs are serialized on a process-wide
/// gate so two racing installs of the same id cannot interleave check-then-write; the
/// style-file write vs DB commit inconsistency is handled with a restore attempt.
/// Installing never changes the default citation style, and a dependent style whose
/// parent is missing reports its dependencies and renderable status without any
/// silent parent download.
/// </summary>
public sealed class CslLocalFileInstallService : ICslLocalFileInstallService
{
    private static readonly SemaphoreSlim InstallGate = new(1, 1);

    private readonly ICslStyleStore _store;

    public CslLocalFileInstallService(ICslStyleStore store)
    {
        _store = store;
    }

    public async Task<Result<CslLocalInstallResult>> InstallFromFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Result<CslLocalInstallResult>.Failure(AppErrorCodes.NotFound,
                "CSL style file was not found.");
        }

        string contentXml;
        try
        {
            contentXml = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<CslLocalInstallResult>.Failure(AppErrorCodes.ValidationFailed,
                "CSL style file could not be read.");
        }

        Result<CslLocalStyleInfo> parsed = CslStyleXmlParser.Parse(contentXml);
        if (parsed.IsFailure)
        {
            return Result<CslLocalInstallResult>.Failure(
                parsed.ErrorCode ?? AppErrorCodes.ValidationFailed,
                parsed.ErrorMessage ?? "CSL style XML is invalid.");
        }

        CslLocalStyleInfo info = parsed.Value;
        await InstallGate.WaitAsync(cancellationToken);
        try
        {
            return await InstallCoreAsync(info, contentXml, cancellationToken);
        }
        finally
        {
            InstallGate.Release();
        }
    }

    private async Task<Result<CslLocalInstallResult>> InstallCoreAsync(
        CslLocalStyleInfo info,
        string contentXml,
        CancellationToken cancellationToken)
    {
        string contentHash = ComputeHash(contentXml);
        Result<CslStyle> existing = await _store.GetStyleAsync(info.StyleId, cancellationToken);
        if (existing.IsFailure && existing.ErrorCode != AppErrorCodes.NotFound)
        {
            return Result<CslLocalInstallResult>.Failure(existing.ErrorCode!, existing.ErrorMessage!);
        }

        if (existing.IsSuccess)
        {
            if (string.Equals(existing.Value.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase))
            {
                return await BuildResultAsync(info, reused: true, conflict: false, cancellationToken);
            }

            // Same id, different content: explicit conflict; the update path is put.
            Result<CslLocalInstallResult> conflict =
                await BuildResultAsync(info, reused: false, conflict: true, cancellationToken);
            return conflict.IsSuccess
                ? Result<CslLocalInstallResult>.Failure(
                    FileImportErrorCodes.StyleConflict,
                    $"CSL style '{info.StyleId}' already exists with different content; update it via put.")
                : conflict;
        }

        Result<CslStyle> installed = await _store.InstallStyleAsync(
            new CslCatalogStyle(info.StyleId, info.Title, null, "local-file", info.DefaultLocale),
            contentXml,
            cancellationToken);
        if (installed.IsFailure)
        {
            // Style-file write vs DB commit inconsistency: the store writes the style file
            // before its DB transaction, so a DB failure can leave an orphaned file with no
            // DB row. Retrying the install is the recovery: the upsert overwrites the orphan
            // and completes the row. Surface that explicitly instead of hiding it.
            return Result<CslLocalInstallResult>.Failure(installed.ErrorCode!,
                installed.ErrorMessage + " If this was a database failure after the style file was written, " +
                "retrying the install completes the interrupted install.");
        }

        return await BuildResultAsync(info, reused: false, conflict: false, cancellationToken);
    }

    private async Task<Result<CslLocalInstallResult>> BuildResultAsync(
        CslLocalStyleInfo info,
        bool reused,
        bool conflict,
        CancellationToken cancellationToken)
    {
        string? missingParent = null;
        bool renderable = true;
        if (info.IndependentParentId is not null)
        {
            Result<CslStyle> parent = await _store.GetStyleAsync(info.IndependentParentId, cancellationToken);
            if (parent.IsFailure)
            {
                missingParent = info.IndependentParentId;
                renderable = false;
            }
        }

        return Result<CslLocalInstallResult>.Success(new CslLocalInstallResult(
            true,
            info.StyleId,
            $"patchouli://csl-styles/{info.StyleId}.csl",
            info.Title,
            info.Dependencies,
            reused,
            conflict,
            renderable,
            missingParent,
            missingParent is null
                ? null
                : $"Dependent style '{info.StyleId}' requires parent style '{missingParent}', which is not installed."));
    }

    private static string ComputeHash(string content)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }
}
