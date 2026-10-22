using FluentAssertions;

namespace Patchouli.Tests;

/// <summary>
/// Issue #31 architecture guard: PDFium native entry points live in exactly one internal
/// layer (Patchouli.Ocr/Native). Any other source file referencing a native entry point
/// would reintroduce a bypass around the single process-wide scheduler gate.
/// </summary>
public sealed class PdfiumArchitectureTests
{
    [Fact]
    public void Native_entry_points_appear_only_inside_the_native_layer()
    {
        string srcRoot = TestPaths.FromRepositoryRoot("src");
        string nativeLayer = Path.GetFullPath(Path.Combine(srcRoot, "Patchouli.Ocr", "Native")) +
                             Path.DirectorySeparatorChar;

        string[] offenders = Directory.GetFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetFullPath(file).StartsWith(nativeLayer, StringComparison.Ordinal))
            .Where(file => !IsGenerated(file))
            .Where(file => File.ReadAllText(file).Contains("FPDF_", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(srcRoot, file))
            .ToArray();

        offenders.Should().BeEmpty(
            "every PDFium native call must go through Patchouli.Ocr.Native and the PdfiumScheduler");
    }

    [Fact]
    public void No_source_file_outside_the_scheduler_defines_its_own_native_gate()
    {
        string srcRoot = TestPaths.FromRepositoryRoot("src");
        string schedulerFile = Path.GetFullPath(Path.Combine(srcRoot, "Patchouli.Ocr", "PdfiumScheduler.cs"));

        string[] offenders = Directory.GetFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !string.Equals(Path.GetFullPath(file), schedulerFile, StringComparison.Ordinal))
            .Where(file => !IsGenerated(file))
            .Where(file => File.ReadAllText(file).Contains("NativeGate", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(srcRoot, file))
            .ToArray();

        offenders.Should().BeEmpty("the PdfiumScheduler is the only native gate (issue #31)");
    }

    private static bool IsGenerated(string file)
    {
        string normalized = file.Replace('\\', '/');
        return normalized.Contains("/obj/", StringComparison.Ordinal) ||
               normalized.Contains("/bin/", StringComparison.Ordinal);
    }
}
