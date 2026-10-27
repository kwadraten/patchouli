using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace Patchouli.Ocr.Engines;

/// <summary>
/// Collects the real binary payload the OCR extension packages carry: this support assembly plus
/// its transitive non-framework managed dependency closure and the current process RID's native
/// runtime libraries. The collector walks actual assembly-reference metadata — no assembly is
/// loaded to discover its dependencies — and reads bytes from the directory the support assembly
/// was deployed to (the application output directory at runtime, the schema generator's output
/// directory at build time), so a package never references an ambient path the artifact loader
/// could not resolve. Model weights are deliberately excluded: they live in the configured model
/// directory only.
/// </summary>
public static class NativeOcrSupportFiles
{
    public const string SupportAssemblyFileName = "Patchouli.Ocr.Engines.dll";

    /// <summary>Package-relative directory the binaries ship under; FSX sources use <c>#r "lib/..."</c>.</summary>
    public const string PackageBinaryDirectory = "lib";

    /// <summary>
    /// Names the collectible extension load context resolves from the running host or the shared
    /// framework; shipping them in a package would shadow nothing and only bloat fingerprints.
    /// </summary>
    private static readonly HashSet<string> SharedAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Patchouli.Core",
        "Patchouli.Extensions",
        "Patchouli.Extensions.Contracts",
        "FSharp.Core"
    };

    /// <summary>
    /// Returns the package binary map (<c>lib/&lt;file&gt;</c> keys) for one snapshot build.
    /// Managed dependencies are required: a missing reference means the deployment is broken and
    /// must surface as an error here, not as a load failure inside the collectible context.
    /// Native libraries are included whenever the build produced them for the current process
    /// architecture; a host that already loads the same runtime keeps working through default
    /// probing.
    /// </summary>
    public static IReadOnlyDictionary<string, ReadOnlyMemory<byte>> CollectBinaries()
    {
        string directory = Path.GetDirectoryName(typeof(NativeOcrSession).Assembly.Location) ?? "";
        if (directory.Length == 0 || !Directory.Exists(directory))
        {
            throw new InvalidOperationException(
                "The Patchouli.Ocr.Engines deployment directory is unavailable; the OCR extension packages cannot be built.");
        }

        Dictionary<string, ReadOnlyMemory<byte>> files = new(StringComparer.Ordinal);
        AddManaged(files, directory, SupportAssemblyFileName);

        // Breadth-first walk over real assembly-reference metadata: every non-shared,
        // non-framework reference in the transitive closure must be packaged or the collectible
        // context would silently fall back to whatever the host app happens to ship.
        Queue<string> pending = new();
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase) { SupportAssemblyFileName };
        foreach (string name in ManagedReferenceNames(
                     Path.Combine(directory, SupportAssemblyFileName)))
        {
            pending.Enqueue(name);
        }

        while (pending.Count > 0)
        {
            string name = pending.Dequeue();
            string fileName = name + ".dll";
            if (!visited.Add(fileName) || SharedAssemblyNames.Contains(name) ||
                IsFrameworkAssembly(name))
            {
                continue;
            }

            AddManaged(files, directory, fileName);
            foreach (string transitive in ManagedReferenceNames(Path.Combine(directory, fileName)))
            {
                pending.Enqueue(transitive);
            }
        }

        foreach (string nativePath in NativeLibraryCandidates(directory))
        {
            string key = PackageBinaryDirectory + "/" + Path.GetFileName(nativePath);
            if (!files.ContainsKey(key))
            {
                files[key] = File.ReadAllBytes(nativePath);
            }
        }

        return files;
    }

    private static void AddManaged(Dictionary<string, ReadOnlyMemory<byte>> files, string directory,
        string fileName)
    {
        string path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"The OCR support dependency '{fileName}' is missing next to {SupportAssemblyFileName}; rebuild the deployment before packaging built-in OCR extensions.");
        }

        files[PackageBinaryDirectory + "/" + fileName] = File.ReadAllBytes(path);
    }

    /// <summary>Assembly names a managed dll references, read from metadata without loading it.</summary>
    private static IEnumerable<string> ManagedReferenceNames(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using PEReader reader = new(stream);
        if (!reader.HasMetadata)
        {
            yield break;
        }

        MetadataReader metadata = reader.GetMetadataReader();
        foreach (AssemblyReferenceHandle handle in metadata.AssemblyReferences)
        {
            yield return metadata.GetString(metadata.GetAssemblyReference(handle).Name);
        }
    }

    private static bool IsFrameworkAssembly(string name)
    {
        string? runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
        return runtimeDirectory is not null && File.Exists(Path.Combine(runtimeDirectory, name + ".dll"));
    }

    private static IEnumerable<string> NativeLibraryCandidates(string directory)
    {
        foreach (string path in ScanNativeNames(directory))
        {
            yield return path;
        }

        string? rid = CurrentRuntimeIdentifier();
        if (rid is not null)
        {
            foreach (string layout in new[] { "native", "lib", "nativeassets" })
            {
                string runtimeSubdirectory = Path.Combine(directory, "runtimes", rid, layout);
                foreach (string path in ScanNativeNames(runtimeSubdirectory))
                {
                    yield return path;
                }
            }
        }
    }

    private static IEnumerable<string> ScanNativeNames(string directory)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (string path in Directory.EnumerateFiles(directory))
        {
            string name = Path.GetFileName(path);
            string extension = Path.GetExtension(name);
            if (extension is not (".dll" or ".so" or ".dylib"))
            {
                continue;
            }

            string simple = Path.GetFileNameWithoutExtension(name);
            if (simple.StartsWith("onnxruntime", StringComparison.OrdinalIgnoreCase) ||
                simple.StartsWith("libSkiaSharp", StringComparison.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    private static string? CurrentRuntimeIdentifier()
    {
        string system = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
            : "";
        if (system.Length == 0)
        {
            return null;
        }

        // The PROCESS architecture selects the RID: an x86 process on an x64 OS must ship the
        // x86 native libraries, not the host's.
        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };
        return system + "-" + architecture;
    }
}
