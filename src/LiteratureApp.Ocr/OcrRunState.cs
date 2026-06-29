namespace LiteratureApp.Ocr;

public static class OcrRunState
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string CompletedWithErrors = "completed_with_errors";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}
