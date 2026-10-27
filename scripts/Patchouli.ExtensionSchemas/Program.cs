using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Bibliography.MetadataLookup;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Translation;

// Build-time compilation and schema inspection never initialize extension code.
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Patchouli.ExtensionSchemas <repository-root> <output-json>");
    return 2;
}

string repositoryRoot = Path.GetFullPath(args[0]);
string outputPath = Path.GetFullPath(args[1]);
Environment.CurrentDirectory = repositoryRoot;
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

string apiIdentity = RuntimeFrameworkReferences.computeApiIdentity();
RuntimeFrameworkReferences.setCompilerIdentity(apiIdentity);
var toolchainStamp = new
{
    schemaVersion = 1,
    identity = apiIdentity,
    hostApi = ExtensionHostApi.Current,
    compilerFormatRevision = RuntimeFrameworkReferences.compilerFormatRevision,
    fsharpCorePackage = RuntimeFrameworkReferences.fsharpCorePackageVersion(),
    fsharpCompilerServicePackage = RuntimeFrameworkReferences.fsharpCompilerServicePackageVersion()
};
string stampJson = JsonSerializer.Serialize(toolchainStamp, new JsonSerializerOptions { WriteIndented = true });
WriteIfChanged(Path.Combine(AppContext.BaseDirectory, "ExtensionToolchain.json"), stampJson);
WriteIfChanged(Path.Combine(Path.GetDirectoryName(outputPath)!, "ExtensionToolchain.json"), stampJson);
ExtensionPackageSnapshot[] packages = BuiltInTranslationExtensions.Packages
    .Concat(BuiltInMetadataExtensions.Packages)
    .Concat(BuiltInOcrExtensions.Packages)
    .Concat(BuiltInPdfTextLayerExtensions.Packages)
    .OrderBy(static package => package.Id, StringComparer.Ordinal)
    .ToArray();
List<object> entries = new();
List<(string Id, IReadOnlyList<ExtensionDiagnostic> Diagnostics)> diagnosticsByPackage = new();
string artifactRoot = Path.Combine(Path.GetDirectoryName(outputPath)!, "compiled");
string bundleRoot = Path.Combine(Path.GetDirectoryName(outputPath)!, "bundled");
Directory.CreateDirectory(bundleRoot);
bool hasErrors = false;

foreach (ExtensionPackageSnapshot package in packages)
{
    ExtensionCompilationResult compilation = await ExtensionCompiler.CompileAsync(package, artifactRoot,
        CancellationToken.None);
    ExtensionCheckResult result = compilation.CheckResult;
    hasErrors |= !result.Succeeded;
    diagnosticsByPackage.Add((package.Id, result.Diagnostics));
    entries.Add(new
    {
        id = package.Id,
        fingerprint = package.Fingerprint,
        sourceHash = package.Fingerprint,
        fields = result.ConfigFields,
        diagnostics = result.Diagnostics
    });
    if (result.Succeeded)
    {
        string archivePath = Path.Combine(bundleRoot, package.Fingerprint + ".zip");
        string stagingArchive = archivePath + ".tmp";
        try
        {
            File.Delete(stagingArchive);
            ZipFile.CreateFromDirectory(compilation.ArtifactDirectory, stagingArchive,
                CompressionLevel.Fastest, false);
            if (File.Exists(archivePath) && ArchiveMatchesDirectory(archivePath, compilation.ArtifactDirectory))
            {
                File.Delete(stagingArchive);
            }
            else
            {
                File.Move(stagingArchive, archivePath, true);
            }
        }
        finally
        {
            File.Delete(stagingArchive);
        }
    }
}

if (hasErrors)
{
    foreach ((string id, IReadOnlyList<ExtensionDiagnostic> diagnostics) in diagnosticsByPackage)
    {
        foreach (ExtensionDiagnostic diagnostic in diagnostics)
        {
            Console.Error.WriteLine(
                $"{id}: {diagnostic.Code} {diagnostic.Location.FileName}:{diagnostic.Location.Line}:{diagnostic.Location.Column} {diagnostic.Message}");
        }
    }

    return 1;
}

// This directory is generated build data. Only current packages may become embedded resources.
HashSet<string> currentArchives = packages.Select(package => package.Fingerprint + ".zip")
    .ToHashSet(StringComparer.Ordinal);
foreach (string archive in Directory.EnumerateFiles(bundleRoot, "*.zip"))
{
    if (!currentArchives.Contains(Path.GetFileName(archive)))
    {
        File.Delete(archive);
    }
}

var index = new
{
    schemaVersion = 2,
    toolchainIdentity = apiIdentity,
    packages = entries
};

string temporaryPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
try
{
    string json = JsonSerializer.Serialize(index, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    });
    if (!File.Exists(outputPath) ||
        !string.Equals(await File.ReadAllTextAsync(outputPath), json, StringComparison.Ordinal))
    {
        await File.WriteAllTextAsync(temporaryPath, json);
        File.Move(temporaryPath, outputPath, true);
    }
}
finally
{
    File.Delete(temporaryPath);
}

return 0;

static void WriteIfChanged(string path, string content)
{
    if (File.Exists(path) && string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
    {
        return;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content);
}

static bool ArchiveMatchesDirectory(string archivePath, string directory)
{
    using ZipArchive archive = ZipFile.OpenRead(archivePath);
    Dictionary<string, ZipArchiveEntry> entries = archive.Entries.ToDictionary(
        static entry => entry.FullName.Replace('\\', '/'), StringComparer.Ordinal);
    string[] files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
    if (entries.Count != files.Length)
    {
        return false;
    }

    foreach (string file in files)
    {
        string relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
        if (!entries.TryGetValue(relative, out ZipArchiveEntry? entry))
        {
            return false;
        }

        using Stream source = File.OpenRead(file);
        using Stream compressed = entry.Open();
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(source), SHA256.HashData(compressed)))
        {
            return false;
        }
    }

    return true;
}
