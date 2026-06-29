using System.Text.Json;
using LiteratureApp.Core.Layout;
using LiteratureApp.Core.Results;

namespace LiteratureApp.Ocr;

public sealed class TesseractCliAdapter : IRealOcrAdapter
{
    private readonly IProcessRunner _processRunner;
    public TesseractCliAdapter(IProcessRunner processRunner) => _processRunner = processRunner;
    public string EngineId => OcrEngineIds.TesseractCli;
    public string DisplayName => "Tesseract CLI";
    public string Kind => OcrAdapterKind.LocalProcess;

    public OcrEngineCapability GetCapability() => new(EngineId, DisplayName, true, false, true, false, false, false, false, false, false, [OcrInputKinds.ImageFile, OcrInputKinds.PageImage], "Local image OCR only. PDF rendering and direct PDF input are not implemented.");

    public async Task<OcrEnvironmentCheckResult> CheckEnvironmentAsync(OcrPresetVersion presetVersion, CancellationToken cancellationToken = default)
    {
        var executable = string.IsNullOrWhiteSpace(presetVersion.ModelPath) ? "tesseract" : presetVersion.ModelPath!;
        if (!string.IsNullOrWhiteSpace(presetVersion.ModelPath) && !File.Exists(executable))
            return Check(presetVersion, OcrEnvironmentStatus.MissingExecutable, false, "Configured Tesseract executable path does not exist.", OcrRequiredAction.RebindModelPath);
        try
        {
            var result = await _processRunner.RunAsync(new ProcessRunRequest(executable, ["--version"], Timeout: TimeSpan.FromSeconds(10)), cancellationToken);
            return result is { TimedOut: false, ExitCode: 0 }
                ? Check(presetVersion, OcrEnvironmentStatus.Ready, true, "Tesseract CLI is available.", OcrRequiredAction.None)
                : Check(presetVersion, OcrEnvironmentStatus.MissingExecutable, false, "Tesseract CLI could not be started successfully.", OcrRequiredAction.InstallEngine);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return Check(presetVersion, OcrEnvironmentStatus.MissingExecutable, false, "Tesseract CLI was not found or could not be executed.", OcrRequiredAction.InstallEngine);
        }
    }

    public Task<Result> ValidatePresetAsync(OcrPresetVersion presetVersion, CancellationToken cancellationToken = default)
    {
        try { _ = Options.Parse(presetVersion.ParametersJson); return Task.FromResult(Result.Success()); }
        catch (JsonException) { return Task.FromResult(Result.Failure(AppErrorCodes.ValidationFailed, "Tesseract parameters_json is invalid.")); }
    }

    public Task<Result> ValidateInputAsync(OcrInputDescriptor input, CancellationToken cancellationToken = default)
    {
        if (input.InputKind is not (OcrInputKinds.ImageFile or OcrInputKinds.PageImage))
            return Task.FromResult(Result.Failure(AppErrorCodes.UnsupportedOperation, "Tesseract CLI MVP supports image_file or page_image only; PDF input is not supported."));
        if (input.SourceFileStatus == "missing") return Task.FromResult(Result.Failure(AppErrorCodes.NotFound, OcrFailureCode.SourceFileMissing));
        if (input.SourceFileStatus is "changed" or "conflict") return Task.FromResult(Result.Failure(AppErrorCodes.InvalidState, OcrFailureCode.SourceFileChanged));
        if (string.IsNullOrWhiteSpace(input.ImagePath) || !File.Exists(input.ImagePath))
            return Task.FromResult(Result.Failure(AppErrorCodes.NotFound, OcrFailureCode.SourceFileMissing));
        return Task.FromResult(Result.Success());
    }

    public async Task<Result<OcrEnginePageResult>> RunPageAsync(OcrInputDescriptor input, OcrPresetVersion presetVersion, CancellationToken cancellationToken = default)
    {
        var inputValidation = await ValidateInputAsync(input, cancellationToken);
        if (inputValidation.IsFailure) return Result<OcrEnginePageResult>.Failure(inputValidation.ErrorCode!, inputValidation.ErrorMessage!);
        var presetValidation = await ValidatePresetAsync(presetVersion, cancellationToken);
        if (presetValidation.IsFailure) return Result<OcrEnginePageResult>.Failure(presetValidation.ErrorCode!, presetValidation.ErrorMessage!);
        var imageSize = await PngImageSizeGuard.TryReadPngSizeAsync(input.ImagePath!, cancellationToken);
        if (imageSize is not null && PngImageSizeGuard.ExceedsLimit(imageSize))
            return Result<OcrEnginePageResult>.Success(Failure(input, OcrFailureCode.ImageTooLargeForOcr, PngImageSizeGuard.BuildErrorMessage(imageSize)));
        var environment = await CheckEnvironmentAsync(presetVersion, cancellationToken);
        if (!environment.IsReady) return Result<OcrEnginePageResult>.Failure(AppErrorCodes.ValidationFailed, environment.Message);

        var options = Options.Parse(presetVersion.ParametersJson);
        var executable = string.IsNullOrWhiteSpace(presetVersion.ModelPath) ? "tesseract" : presetVersion.ModelPath!;
        var arguments = new List<string> { input.ImagePath!, "stdout", "-l", options.Language };
        if (options.PageSegmentationMode is not null) { arguments.Add("--psm"); arguments.Add(options.PageSegmentationMode.Value.ToString()); }
        if (options.OcrEngineMode is not null) { arguments.Add("--oem"); arguments.Add(options.OcrEngineMode.Value.ToString()); }
        var result = await _processRunner.RunAsync(new ProcessRunRequest(executable, arguments, Timeout: TimeSpan.FromSeconds(options.TimeoutSeconds)), cancellationToken);
        if (result.TimedOut) return Result<OcrEnginePageResult>.Success(Failure(input, OcrFailureCode.LocalOcrTimeout, "Tesseract CLI timed out."));
        if (result.ExitCode != 0) return Result<OcrEnginePageResult>.Success(Failure(input, OcrFailureCode.LocalOcrProcessFailed, string.IsNullOrWhiteSpace(result.StandardError) ? "Tesseract CLI failed." : result.StandardError));
        var text = result.StandardOutput.Trim();
        return string.IsNullOrWhiteSpace(text)
            ? Result<OcrEnginePageResult>.Success(Failure(input, OcrFailureCode.EmptyOcrOutput, "Tesseract CLI returned no text."))
            : Result<OcrEnginePageResult>.Success(new OcrEnginePageResult(input.PageId, true, text, new NormalizedBBox(.05, .05, .90, .20), null, null, new SourceBBox(.05, .05, .90, .20, SourceBBoxCoordinateSystem.NormalizedPage, EngineName: EngineId)));
    }

    private static OcrEnginePageResult Failure(OcrInputDescriptor input, string code, string message) => new(input.PageId, false, null, null, code, message);
    private static OcrEnvironmentCheckResult Check(OcrPresetVersion version, string status, bool ready, string message, string action) => new(OcrEngineIds.TesseractCli, version.ModelId, version.ModelPath, status, ready, message, action, []);

    private sealed record Options(string Language, int TimeoutSeconds, int? PageSegmentationMode, int? OcrEngineMode)
    {
        public static Options Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new("eng", 60, null, null);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var language = root.TryGetProperty("lang", out var lang) && lang.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(lang.GetString()) ? lang.GetString()! : "eng";
            var requested = root.TryGetProperty("timeoutSeconds", out var timeout) && timeout.ValueKind == JsonValueKind.Number ? timeout.GetInt32() : 60;
            var psm = root.TryGetProperty("psm", out var psmValue) && psmValue.ValueKind == JsonValueKind.Number ? psmValue.GetInt32() : (int?)null;
            var oem = root.TryGetProperty("oem", out var oemValue) && oemValue.ValueKind == JsonValueKind.Number ? oemValue.GetInt32() : (int?)null;
            if (psm is < 0 or > 13 || oem is < 0 or > 3) throw new JsonException("Tesseract psm/oem parameter is out of range.");
            return new Options(language, Math.Clamp(requested, 1, 600), psm, oem);
        }
    }
}
