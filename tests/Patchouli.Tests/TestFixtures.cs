namespace Patchouli.Tests;

internal static class TestFixtures
{
    public static string RealThreePagePdf =>
        TestPaths.FromRepositoryRoot("tests", "Patchouli.Tests", "Fixtures", "Pdf", "real-three-page-sample.pdf");

    /// <summary>Typst-generated corpus PDFs (plus one hand-crafted corrupt file) covering the
    /// issue-#24 real-example matrix: multi-column LTR, upright and rotated vertical scripts,
    /// Arabic RTL, mixed BiDi, mixed direction and image-only pages. The <c>.typ</c> sources
    /// ship alongside so every fixture can be regenerated with <c>typst compile</c>.</summary>
    public static string CorpusPdfDirectory =>
        TestPaths.FromRepositoryRoot("tests", "Patchouli.Tests", "Fixtures", "Pdf", "corpus");

    public static string CorpusPdf(string name)
    {
        return Path.Combine(CorpusPdfDirectory, name);
    }

    public static string CopyRealThreePagePdfTo(string directory, string fileName = "real-three-page-sample.pdf")
    {
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, fileName);
        File.Copy(RealThreePagePdf, destination, true);
        return destination;
    }
}
