namespace Patchouli.Tests;

internal static class TestFixtures
{
    public static string RealThreePagePdf =>
        TestPaths.FromRepositoryRoot("tests", "Patchouli.Tests", "Fixtures", "Pdf", "real-three-page-sample.pdf");

    public static string CopyRealThreePagePdfTo(string directory, string fileName = "real-three-page-sample.pdf")
    {
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, fileName);
        File.Copy(RealThreePagePdf, destination, true);
        return destination;
    }
}
