using System.Reflection;
using System.Text;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Infrastructure.Translation;

/// <summary>FSX source shipped with the application for each built-in translation engine.</summary>
public static class BuiltInTranslationExtensions
{
    private const string ResourcePrefix = "Patchouli.Infrastructure.Translation.Extensions.";

    private static readonly string[] EngineIds =
    [
        "translation.llm",
        "translation.google-free",
        "translation.deepl-free",
        "translation.deeplx",
        "translation.microsoft-free",
        "translation.tencent-free"
    ];

    private static readonly IReadOnlyList<ExtensionPackageSnapshot> BuiltInPackages =
        Array.AsReadOnly(EngineIds.Select(CreatePackage).ToArray());

    public static IReadOnlyList<ExtensionPackageSnapshot> Packages => BuiltInPackages;

    private static ExtensionPackageSnapshot CreatePackage(string id)
    {
        string mainResource = ResourcePrefix + id + ".main.fsx";
        Dictionary<string, string> sources = new(StringComparer.Ordinal)
        {
            ["main.fsx"] = ReadResource(mainResource),
            ["Support.fsx"] = ReadResource(ResourcePrefix + "Support.fsx")
        };
        return new ExtensionPackageSnapshot(id, sources);
    }

    private static string ReadResource(string name)
    {
        Assembly assembly = typeof(BuiltInTranslationExtensions).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(name) ??
                              throw new InvalidOperationException($"Built-in translation source '{name}' is missing.");
        using StreamReader reader = new(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }
}
