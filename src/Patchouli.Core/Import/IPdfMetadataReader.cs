namespace Patchouli.Core.Import;

public interface IPdfMetadataReader
{
    Task<int?> GetPageCountAsync(string pdfPath, CancellationToken cancellationToken = default);
}
