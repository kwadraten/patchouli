using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Ocr.NdlKoten;

public static class NdlKotenModelFiles
{
    public const string BaseUrl = "https://raw.githubusercontent.com/ndl-lab/ndlkotenocr-lite/master/src";

    public const string LicenseName = "Creative Commons Attribution 4.0 International (CC-BY-4.0)";

    public const string Attribution =
        "Models and configuration files are from NDL Lab ndlkotenocr-lite " +
        "(https://github.com/ndl-lab/ndlkotenocr-lite) and are used under CC-BY-4.0.";

    public static IReadOnlyList<ModelFileEntry> Files { get; } =
    [
        new(
            "model/rtmdet-s-1280x1280.onnx",
            40_188_733L),
        new(
            "model/parseq-ndl-32x384-tiny-10.onnx",
            42_442_247L),
        new(
            "config/ndl.yaml",
            276L),
        new(
            "config/NDLmoji.yaml",
            42_426L)
    ];

    public static string GetLocalPath(string modelsDirectory, ModelFileEntry entry)
    {
        return Path.Combine(modelsDirectory, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    public static bool IsComplete(string modelsDirectory)
    {
        foreach (ModelFileEntry entry in Files)
        {
            string path = GetLocalPath(modelsDirectory, entry);
            if (!File.Exists(path))
            {
                return false;
            }

            FileInfo info = new(path);
            if (info.Length != entry.ExpectedBytes)
            {
                return false;
            }
        }

        return true;
    }

    public static long GetInstalledByteCount(string modelsDirectory)
    {
        long total = 0;
        foreach (ModelFileEntry entry in Files)
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

public sealed record ModelFileEntry(string RelativePath, long ExpectedBytes)
{
    public string DownloadUrl => $"{NdlKotenModelFiles.BaseUrl}/{RelativePath}";
}
