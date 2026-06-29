using LiteratureApp.Core.Results;

namespace LiteratureApp.Ocr.MinerU;

public interface IMinerUResultImporter
{
    Task<Result<MinerUImportResult>> ImportResultZipAsync(
        MinerUImportRequest request,
        CancellationToken cancellationToken = default);
}
