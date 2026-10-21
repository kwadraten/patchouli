using System.Reflection;
using System.Text;
using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Infrastructure.Bibliography.MetadataLookup;

/// <summary>FSX sources embedded with the application for metadata lookup.</summary>
public static class BuiltInMetadataExtensions
{
    private const string ResourcePrefix = "Patchouli.Infrastructure.Bibliography.MetadataLookup.Extensions.";

    private static readonly IReadOnlyList<ExtensionPackageSnapshot> BuiltInPackages =
        Array.AsReadOnly(new[] { CreateCrossrefPackage() });

    public static IReadOnlyList<ExtensionPackageSnapshot> Packages => BuiltInPackages;

    public static MetadataSourceDefinition CrossrefSourceDefinition { get; } = new(
        "crossref", "Crossref", new HashSet<string>(["doi"], StringComparer.OrdinalIgnoreCase), 20, true);

    private static ExtensionPackageSnapshot CreateCrossrefPackage()
    {
        const string id = "metadata.crossref";
        Dictionary<string, string> sources = new(StringComparer.Ordinal)
        {
            ["extension.yml"] =
                $"id: {id}\nkind: metadata\nversion: 1.0.0\nhost_api: patchouli.extensions/1\nentry: main.fsx\n",
            ["main.fsx"] = ReadResource(ResourcePrefix + id + ".main.fsx")
        };
        return new ExtensionPackageSnapshot(id, sources);
    }

    private static string ReadResource(string name)
    {
        Assembly assembly = typeof(BuiltInMetadataExtensions).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(name) ??
                              throw new InvalidOperationException($"Built-in metadata source '{name}' is missing.");
        using StreamReader reader = new(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }
}
