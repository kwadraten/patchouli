using System.Text.Json;
using System.Text.Json.Serialization;
using Patchouli.Core.Results;
using Patchouli.Ocr;

namespace Patchouli.Infrastructure.Csl;

internal sealed class HayagrivaCli
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly SemaphoreSlim BuildLock = new(1, 1);
    private readonly IProcessRunner _processRunner;
    private readonly string? _executablePath;

    public HayagrivaCli(IProcessRunner? processRunner = null, string? executablePath = null)
    {
        _processRunner = processRunner ?? new SystemProcessRunner();
        _executablePath = executablePath;
    }

    public async Task<Result<HayagrivaRenderResponse>> RenderAsync(
        HayagrivaRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        Result<string> executable = await ResolveExecutablePathAsync(cancellationToken);
        if (executable.IsFailure)
        {
            return Result<HayagrivaRenderResponse>.Failure(executable.ErrorCode!, executable.ErrorMessage!);
        }

        string requestPath = Path.Combine(Path.GetTempPath(), $"patchouli-hayagriva-{Guid.NewGuid():N}.json");
        try
        {
            string payload = JsonSerializer.Serialize(request, JsonOptions);
            await File.WriteAllTextAsync(requestPath, payload, cancellationToken);

            ProcessRunResult run = await _processRunner.RunAsync(
                new ProcessRunRequest(
                    executable.Value,
                    [requestPath],
                    Path.GetDirectoryName(executable.Value),
                    TimeSpan.FromSeconds(60)),
                cancellationToken);

            if (run.TimedOut)
            {
                return Result<HayagrivaRenderResponse>.Failure("csl_render_failed", "hayagriva rendering timed out.");
            }

            if (run.ExitCode != 0)
            {
                return Result<HayagrivaRenderResponse>.Failure(
                    "csl_render_failed",
                    CompactError(run.StandardError, run.StandardOutput, "hayagriva rendering failed."));
            }

            if (string.IsNullOrWhiteSpace(run.StandardOutput))
            {
                return Result<HayagrivaRenderResponse>.Failure("csl_render_failed", "hayagriva returned no output.");
            }

            HayagrivaRenderResponse? response =
                JsonSerializer.Deserialize<HayagrivaRenderResponse>(run.StandardOutput, JsonOptions);
            return response is null
                ? Result<HayagrivaRenderResponse>.Failure("csl_render_failed",
                    "hayagriva returned an unreadable response.")
                : Result<HayagrivaRenderResponse>.Success(response);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.hayagriva-cli"))
        {
            return Result<HayagrivaRenderResponse>.Failure("csl_render_failed",
                $"hayagriva execution failed: {exception.Message}");
        }
        finally
        {
            TryDelete(requestPath);
        }
    }

    private async Task<Result<string>> ResolveExecutablePathAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_executablePath))
        {
            return File.Exists(_executablePath)
                ? Result<string>.Success(_executablePath)
                : Result<string>.Failure(AppErrorCodes.NotFound,
                    $"hayagriva executable was not found at '{_executablePath}'.");
        }

        foreach (string candidate in CandidateExecutablePaths())
        {
            if (File.Exists(candidate))
            {
                return Result<string>.Success(candidate);
            }
        }

        string? toolDirectory = FindToolDirectory();
        if (toolDirectory is null)
        {
            return Result<string>.Failure(
                AppErrorCodes.NotFound,
                "The embedded hayagriva tool directory could not be located.");
        }

        await BuildLock.WaitAsync(cancellationToken);
        try
        {
            foreach (string candidate in ToolBinaryCandidates(toolDirectory))
            {
                if (File.Exists(candidate))
                {
                    return Result<string>.Success(candidate);
                }
            }

            string manifestPath = Path.Combine(toolDirectory, "Cargo.toml");
            ProcessRunResult build = await _processRunner.RunAsync(
                new ProcessRunRequest(
                    "cargo",
                    ["build", "--locked", "--manifest-path", manifestPath],
                    toolDirectory,
                    TimeSpan.FromMinutes(5)),
                cancellationToken);

            if (build.TimedOut)
            {
                return Result<string>.Failure("csl_render_failed", "Building the embedded hayagriva tool timed out.");
            }

            if (build.ExitCode != 0)
            {
                return Result<string>.Failure(
                    "csl_render_failed",
                    CompactError(build.StandardError, build.StandardOutput,
                        "Building the embedded hayagriva tool failed."));
            }

            foreach (string candidate in ToolBinaryCandidates(toolDirectory))
            {
                if (File.Exists(candidate))
                {
                    return Result<string>.Success(candidate);
                }
            }

            return Result<string>.Failure("csl_render_failed",
                "The hayagriva tool build completed, but no executable was produced.");
        }
        finally
        {
            BuildLock.Release();
        }
    }

    private static IEnumerable<string> CandidateExecutablePaths()
    {
        string executableName = ExecutableFileName();
        yield return Path.Combine(AppContext.BaseDirectory, executableName);

        string? toolDirectory = FindToolDirectory();
        if (toolDirectory is null)
        {
            yield break;
        }

        foreach (string candidate in ToolBinaryCandidates(toolDirectory))
        {
            yield return candidate;
        }
    }

    private static IEnumerable<string> ToolBinaryCandidates(string toolDirectory)
    {
        string executableName = ExecutableFileName();
        yield return Path.Combine(toolDirectory, "target", "debug", executableName);
        yield return Path.Combine(toolDirectory, "target", "release", executableName);
    }

    private static string? FindToolDirectory()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            if (string.IsNullOrWhiteSpace(start))
            {
                continue;
            }

            DirectoryInfo? directory = new(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Patchouli.sln")))
                {
                    string candidate = Path.Combine(directory.FullName, "tools", "patchouli-hayagriva");
                    if (File.Exists(Path.Combine(candidate, "Cargo.toml")))
                    {
                        return candidate;
                    }
                }

                directory = directory.Parent;
            }
        }

        return null;
    }

    private static string ExecutableFileName()
    {
        return OperatingSystem.IsWindows() ? "patchouli-hayagriva.exe" : "patchouli-hayagriva";
    }

    private static string CompactError(string standardError, string standardOutput, string fallback)
    {
        string message = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
        message = string.IsNullOrWhiteSpace(message) ? fallback : message.Trim();
        return message.ReplaceLineEndings(" ");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed record HayagrivaRenderRequest(
    [property: JsonPropertyName("style_id")]
    string StyleId,
    [property: JsonPropertyName("style_xml")]
    string StyleXml,
    [property: JsonPropertyName("locale")] string? Locale,
    [property: JsonPropertyName("items")] IReadOnlyList<Dictionary<string, object?>> Items);

internal sealed record HayagrivaRenderResponse(
    [property: JsonPropertyName("styleId")]
    string StyleId,
    [property: JsonPropertyName("locale")] string? Locale,
    [property: JsonPropertyName("renderedText")]
    string RenderedText,
    [property: JsonPropertyName("renderedHtml")]
    string RenderedHtml,
    [property: JsonPropertyName("warnings")]
    IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("errors")] IReadOnlyList<string> Errors);
