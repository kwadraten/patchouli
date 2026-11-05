using System.Reflection;
using System.Text;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Infrastructure.Ocr;

public static class BuiltInLayoutRecognitionExtensions
{
    public static IReadOnlyList<ExtensionPackageSnapshot> Packages { get; } = Array.AsReadOnly(
        new[] { "layout.pp-doclayout-v3", "layout.doclayout-yolo" }.Select(CreatePackage).ToArray());

    private static ExtensionPackageSnapshot CreatePackage(string id)
    {
        using Stream stream = typeof(BuiltInLayoutRecognitionExtensions).Assembly.GetManifestResourceStream(
                                  "Patchouli.Infrastructure.Ocr.Extensions." + id + ".main.fsx")
                              ?? throw new InvalidOperationException("Missing bundled layout source: " + id);
        using StreamReader reader = new(stream, Encoding.UTF8, true);
        return new ExtensionPackageSnapshot(id, new Dictionary<string, string> { ["main.fsx"] = reader.ReadToEnd() });
    }
}
