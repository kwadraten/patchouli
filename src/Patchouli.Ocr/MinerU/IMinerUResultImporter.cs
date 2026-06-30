using Patchouli.Core.Results;

namespace Patchouli.Ocr.MinerU;

public interface IMinerUResultImporter
{
    Task<Result<MinerUImportResult>> ImportResultZipAsync(
        MinerUImportRequest request,
        CancellationToken cancellationToken = default);
}
