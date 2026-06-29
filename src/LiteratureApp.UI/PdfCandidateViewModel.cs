using LiteratureApp.Core.Import;

namespace LiteratureApp.UI;

public sealed class PdfCandidateViewModel : ViewModelBase
{
    public PdfCandidateViewModel(PdfCandidate candidate)
    {
        Path = candidate.Path;
        FileName = candidate.FileName;
        SizeBytes = candidate.SizeBytes;
        ModifiedAt = candidate.ModifiedAt;
        Status = candidate.Status;
    }

    public string Path { get; }
    public string FileName { get; }
    public long SizeBytes { get; }
    public DateTimeOffset? ModifiedAt { get; }
    public string Status { get; }

    public string SizeDisplay => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:F1} KB",
        _ => $"{SizeBytes / (1024.0 * 1024.0):F1} MB"
    };

    public string ModifiedDisplay => ModifiedAt?.ToString("g") ?? "Unknown";
}
