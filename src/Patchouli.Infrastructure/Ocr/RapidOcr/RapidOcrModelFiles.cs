namespace Patchouli.Infrastructure.Ocr.RapidOcr;

/// <summary>
/// Pinned RapidOCR v3.9.2 model set: the default ONNX detector, recognizer and classifier
/// shipped by upstream (<c>default_models.yaml</c>). Files are verified by SHA256 before use.
/// </summary>
public static class RapidOcrModelFiles
{
    public const string BaseUrl = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2";

    public const string LicenseName = "Apache License 2.0";

    public const string Attribution =
        "RapidOCR models are converted from PaddleOCR by the RapidAI team " +
        "(https://github.com/RapidAI/RapidOCR) and are used under the Apache License 2.0.";

    public const string DetectorFileName = "PP-OCRv6_det_small.onnx";

    public const string RecognizerFileName = "PP-OCRv6_rec_small.onnx";

    public const string ClassifierFileName = "ch_ppocr_mobile_v2.0_cls_mobile.onnx";

    public static RapidOcrModelFile Detector { get; } = new(
        DetectorFileName,
        "onnx/PP-OCRv6/det/PP-OCRv6_det_small.onnx",
        9_929_594,
        "090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f");

    public static RapidOcrModelFile Recognizer { get; } = new(
        RecognizerFileName,
        "onnx/PP-OCRv6/rec/PP-OCRv6_rec_small.onnx",
        21_234_383,
        "6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884");

    public static RapidOcrModelFile Classifier { get; } = new(
        ClassifierFileName,
        "onnx/PP-OCRv4/cls/ch_ppocr_mobile_v2.0_cls_mobile.onnx",
        585_532,
        "e47acedf663230f8863ff1ab0e64dd2d82b838fceb5957146dab185a89d6215c");

    public static IReadOnlyList<RapidOcrModelFile> Files { get; } = [Detector, Recognizer, Classifier];

    public static string GetLocalPath(string modelsDirectory, RapidOcrModelFile file)
    {
        return Path.Combine(modelsDirectory, file.FileName);
    }

    /// <summary>
    /// Files the runtime needs for the given preset. The classifier is optional so a
    /// <c>useCls=false</c> preset can run while only the detector and recognizer are present.
    /// </summary>
    public static IReadOnlyList<RapidOcrModelFile> GetRequiredFiles(bool includeClassifier = true)
    {
        return includeClassifier ? Files : [Detector, Recognizer];
    }

    public static bool IsComplete(string modelsDirectory, bool includeClassifier = true)
    {
        foreach (RapidOcrModelFile file in GetRequiredFiles(includeClassifier))
        {
            string path = GetLocalPath(modelsDirectory, file);
            if (!File.Exists(path) || new FileInfo(path).Length != file.ExpectedBytes)
            {
                return false;
            }
        }

        return true;
    }

    public static IReadOnlyList<RapidOcrModelFile> GetMissing(string modelsDirectory, bool includeClassifier = true)
    {
        List<RapidOcrModelFile> missing = new();
        foreach (RapidOcrModelFile file in GetRequiredFiles(includeClassifier))
        {
            string path = GetLocalPath(modelsDirectory, file);
            if (!File.Exists(path) || new FileInfo(path).Length != file.ExpectedBytes)
            {
                missing.Add(file);
            }
        }

        return missing;
    }

    public static long GetInstalledByteCount(string modelsDirectory)
    {
        long total = 0;
        foreach (RapidOcrModelFile file in Files)
        {
            string path = GetLocalPath(modelsDirectory, file);
            if (File.Exists(path))
            {
                total += new FileInfo(path).Length;
            }
        }

        return total;
    }
}

public sealed record RapidOcrModelFile(string FileName, string RelativePath, long ExpectedBytes, string Sha256)
{
    public string DownloadUrl => $"{RapidOcrModelFiles.BaseUrl}/{RelativePath}";
}
