using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Bibliography.Biblatex;

public sealed class BiblatexHelperClient : IBiblatexHelperClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _helperPath;

    public BiblatexHelperClient(string? helperPath = null)
    {
        _helperPath = helperPath ?? ResolveDefaultHelperPath();
    }

    public async Task<Result<IReadOnlyList<BiblatexEntryDto>>> ParseAsync(
        string biblatexText,
        CancellationToken cancellationToken = default)
    {
        Result<BiblatexHelperResponse> response = await InvokeAsync(
            new Dictionary<string, object?>
            {
                ["op"] = "parse",
                ["text"] = biblatexText
            },
            cancellationToken);

        if (response.IsFailure)
        {
            return Result<IReadOnlyList<BiblatexEntryDto>>.Failure(response.ErrorCode!, response.ErrorMessage!);
        }

        if (!response.Value.Ok || response.Value.Entries is null)
        {
            return Result<IReadOnlyList<BiblatexEntryDto>>.Failure(
                MapErrorCode(response.Value.Error?.Code, AppErrorCodes.BiblatexParseFailed),
                response.Value.Error?.Message ?? "BibLaTeX parse failed.");
        }

        return Result<IReadOnlyList<BiblatexEntryDto>>.Success(response.Value.Entries);
    }

    public async Task<Result<string>> WriteAsync(
        IReadOnlyList<BiblatexWriteEntryDto> entries,
        CancellationToken cancellationToken = default)
    {
        Result<BiblatexHelperResponse> response = await InvokeAsync(
            new Dictionary<string, object?>
            {
                ["op"] = "write",
                ["entries"] = entries
            },
            cancellationToken);

        if (response.IsFailure)
        {
            return Result<string>.Failure(response.ErrorCode!, response.ErrorMessage!);
        }

        if (!response.Value.Ok || response.Value.Text is null)
        {
            return Result<string>.Failure(
                MapErrorCode(response.Value.Error?.Code, AppErrorCodes.BiblatexWriteFailed),
                response.Value.Error?.Message ?? "BibLaTeX write failed.");
        }

        return Result<string>.Success(response.Value.Text);
    }

    private async Task<Result<BiblatexHelperResponse>> InvokeAsync(
        object request,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_helperPath))
        {
            return Result<BiblatexHelperResponse>.Failure(
                AppErrorCodes.BiblatexHelperFailed,
                $"biblatex-helper was not found at '{_helperPath}'.");
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = _helperPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };

        try
        {
            using Process process = new() { StartInfo = startInfo };
            if (!process.Start())
            {
                return Result<BiblatexHelperResponse>.Failure(
                    AppErrorCodes.BiblatexHelperFailed,
                    "Failed to start biblatex-helper.");
            }

            string payload = JsonSerializer.Serialize(request, JsonOptions);
            await process.StandardInput.WriteAsync(payload.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();

            string stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            string stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(stdout))
            {
                return Result<BiblatexHelperResponse>.Failure(
                    AppErrorCodes.BiblatexHelperFailed,
                    string.IsNullOrWhiteSpace(stderr)
                        ? $"biblatex-helper exited with code {process.ExitCode} and empty stdout."
                        : stderr.Trim());
            }

            BiblatexHelperResponse? response =
                JsonSerializer.Deserialize<BiblatexHelperResponse>(stdout, JsonOptions);
            if (response is null)
            {
                return Result<BiblatexHelperResponse>.Failure(
                    AppErrorCodes.BiblatexHelperFailed,
                    "biblatex-helper returned invalid JSON.");
            }

            return Result<BiblatexHelperResponse>.Success(response);
        }
        catch (IOException exception)
        {
            return HelperFailure(exception);
        }
        catch (InvalidOperationException exception)
        {
            return HelperFailure(exception);
        }
        catch (JsonException exception)
        {
            return HelperFailure(exception);
        }
    }

    private static Result<BiblatexHelperResponse> HelperFailure(Exception exception)
    {
        return Result<BiblatexHelperResponse>.Failure(
            AppErrorCodes.BiblatexHelperFailed,
            exception.Message);
    }

    private static string MapErrorCode(string? helperCode, string fallback)
    {
        return helperCode switch
        {
            "parse_failed" => AppErrorCodes.BiblatexParseFailed,
            "write_failed" => AppErrorCodes.BiblatexWriteFailed,
            "invalid_request" => AppErrorCodes.ValidationFailed,
            _ => fallback
        };
    }

    public static string ResolveDefaultHelperPath()
    {
        string fileName = OperatingSystem.IsWindows() ? "biblatex-helper.exe" : "biblatex-helper";
        string?[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(AppContext.BaseDirectory, "tools", "biblatex-helper", fileName),
            FindFromRepositoryRoot(fileName)
        ];

        foreach (string? candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, fileName);
    }

    private static string? FindFromRepositoryRoot(string fileName)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string release = Path.Combine(current.FullName, "tools", "biblatex-helper", "target", "release", fileName);
            if (File.Exists(release))
            {
                return release;
            }

            string debug = Path.Combine(current.FullName, "tools", "biblatex-helper", "target", "debug", fileName);
            if (File.Exists(debug))
            {
                return debug;
            }

            if (File.Exists(Path.Combine(current.FullName, "Patchouli.sln")))
            {
                return release;
            }

            current = current.Parent;
        }

        return null;
    }
}
