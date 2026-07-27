namespace Patchouli.Ocr.MinerU;

public sealed class MinerUOptions
{
    public const string SectionName = "MinerU";

    public string BaseUrl { get; set; } = "https://mineru.net";
    public string Token { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = "vlm";
    public string Language { get; set; } = "ch";
    public bool IsOcr { get; set; } = true;
    public bool EnableTable { get; set; } = true;
    public bool EnableFormula { get; set; } = true;
    public int PollingIntervalMs { get; set; } = 2000;
    public int PollingTimeoutSeconds { get; set; } = 300;
    public int DownloadTimeoutSeconds { get; set; } = 300;
    public int DownloadMaxAttempts { get; set; } = 3;
    public int DownloadRetryDelayMs { get; set; } = 5000;
    public int MaxFileSizeBytes { get; set; } = 200 * 1024 * 1024;
    public int MaxPages { get; set; } = 200;
}
