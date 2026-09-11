namespace Patchouli.Infrastructure.Ocr.NdlLite;

/// <summary>
/// Official model and configuration manifest for the native .NET port of
/// <see href="https://github.com/ndl-lab/ndlocr-lite"/>.
/// </summary>
/// <remarks>
/// NDLOCR-Lite is a separate project from NDL Koten OCR. This manifest is pinned to
/// the immutable upstream commit <see cref="UpstreamCommit"/>, verified to contain
/// the published 1.3.1 files, and records the exact byte length and SHA256 of every
/// entry so a re-pointed tag or a corrupted same-size file cannot silently swap the
/// weights. The detection model is DEIM-S (not RTMDet) and the recognizer cascade
/// uses three PARSeq checkpoints at 24x256 / 24x384 / 24x768; none of these are
/// interchangeable with the NDL Koten models. Model files are downloaded at runtime
/// and are never bundled.
/// </remarks>
public static class NdlLiteModelFiles
{
    /// <summary>Immutable upstream commit the manifest is pinned to (release 1.3.1).</summary>
    public const string UpstreamCommit = "6feccb29e33c2467ea48894e66502a7788ae2b2d";

    /// <summary>Human-readable upstream release the commit corresponds to.</summary>
    public const string UpstreamRelease = "1.3.1";

    /// <summary>Raw content root for the pinned upstream commit.</summary>
    public const string BaseUrl = "https://raw.githubusercontent.com/ndl-lab/ndlocr-lite/" + UpstreamCommit + "/src";

    public const string LicenseName = "Creative Commons Attribution 4.0 International (CC-BY-4.0)";

    public const string Attribution =
        "Models and configuration files are from NDL Lab ndlocr-lite " +
        "(https://github.com/ndl-lab/ndlocr-lite, commit " + UpstreamCommit + ") and are used under CC-BY-4.0.";

    public const string DetectorRelativePath = "model/deim-s-1024x1024.onnx";

    public const string Recognizer30RelativePath =
        "model/parseq-ndl-24x256-30-tiny-189epoch-tegaki3-r8data-202604.onnx";

    public const string Recognizer50RelativePath =
        "model/parseq-ndl-24x384-50-tiny-300epoch-tegaki3-r8data-202604.onnx";

    public const string Recognizer100RelativePath =
        "model/parseq-ndl-24x768-100-tiny-153epoch-tegaki3-r8data-202604.onnx";

    public const string ClassesRelativePath = "config/ndl.yaml";

    public const string CharsetRelativePath = "config/NDLmoji.yaml";

    public static IReadOnlyList<NdlLiteModelFileEntry> Files { get; } =
    [
        new(DetectorRelativePath, 40_256_763L,
            "c156ce0c4e704bc3bf7e4016d0a87b949cffa8b3724f4b4cc696b8284c3c7373"),
        new(Recognizer30RelativePath, 36_457_393L,
            "9e651bae4c1a4d5254da1127e86e82e21ef62d5339b37e62d4a3d3d30831772d"),
        new(Recognizer50RelativePath, 37_808_553L,
            "49cea9db4552f19eb05c8ee202fcf74714977749b2f4c9376b127fde41b07a99"),
        new(Recognizer100RelativePath, 42_588_187L,
            "06462b0dbd5b0b8508545c8c3d485cf20dbf4ffa652fe145e69c9e7457080602"),
        new(ClassesRelativePath, 299L,
            "0c2a6a184dd322375b76f2ce3842f8ac555d53edad0ab63655c013f4c471c5a0"),
        new(CharsetRelativePath, 42_434L,
            "f6ad5a2de444b495155866af811cf1a98309dcae3225db802767ea531a2dc529")
    ];

    public static string GetLocalPath(string modelsDirectory, NdlLiteModelFileEntry entry)
    {
        return Path.Combine(modelsDirectory, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    public static string GetDetectorPath(string modelsDirectory)
    {
        return Path.Combine(modelsDirectory, "model", "deim-s-1024x1024.onnx");
    }

    public static string GetRecognizer30Path(string modelsDirectory)
    {
        return Path.Combine(modelsDirectory, "model",
            "parseq-ndl-24x256-30-tiny-189epoch-tegaki3-r8data-202604.onnx");
    }

    public static string GetRecognizer50Path(string modelsDirectory)
    {
        return Path.Combine(modelsDirectory, "model",
            "parseq-ndl-24x384-50-tiny-300epoch-tegaki3-r8data-202604.onnx");
    }

    public static string GetRecognizer100Path(string modelsDirectory)
    {
        return Path.Combine(modelsDirectory, "model",
            "parseq-ndl-24x768-100-tiny-153epoch-tegaki3-r8data-202604.onnx");
    }

    public static string GetClassesPath(string modelsDirectory)
    {
        return Path.Combine(modelsDirectory, "config", "ndl.yaml");
    }

    public static string GetCharsetPath(string modelsDirectory)
    {
        return Path.Combine(modelsDirectory, "config", "NDLmoji.yaml");
    }

    /// <summary>
    /// Returns manifest entries that are absent or whose byte length does not match.
    /// This is a fast presence check for readiness UI; the download service performs
    /// the full SHA256 verification.
    /// </summary>
    public static IReadOnlyList<NdlLiteModelFileEntry> GetMissing(string modelsDirectory)
    {
        List<NdlLiteModelFileEntry> missing = new();
        foreach (NdlLiteModelFileEntry entry in Files)
        {
            string path = GetLocalPath(modelsDirectory, entry);
            if (!File.Exists(path) || new FileInfo(path).Length != entry.ExpectedBytes)
            {
                missing.Add(entry);
            }
        }

        return missing;
    }

    public static bool IsComplete(string modelsDirectory)
    {
        foreach (NdlLiteModelFileEntry entry in Files)
        {
            string path = GetLocalPath(modelsDirectory, entry);
            if (!File.Exists(path))
            {
                return false;
            }

            if (new FileInfo(path).Length != entry.ExpectedBytes)
            {
                return false;
            }
        }

        return true;
    }

    public static long GetInstalledByteCount(string modelsDirectory)
    {
        long total = 0;
        foreach (NdlLiteModelFileEntry entry in Files)
        {
            string path = GetLocalPath(modelsDirectory, entry);
            if (File.Exists(path))
            {
                total += new FileInfo(path).Length;
            }
        }

        return total;
    }
}

/// <summary>One file in the official NDLOCR-Lite manifest.</summary>
public sealed record NdlLiteModelFileEntry(string RelativePath, long ExpectedBytes, string Sha256)
{
    public string DownloadUrl => $"{NdlLiteModelFiles.BaseUrl}/{RelativePath}";

    public string FileName => Path.GetFileName(RelativePath);
}
