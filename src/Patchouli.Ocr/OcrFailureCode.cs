namespace Patchouli.Ocr;

public static class OcrFailureCode
{
    public const string BBoxCoordinateTransformFailed = "bbox_coordinate_transform_failed";
    public const string SourceFileMissing = "source_file_missing";
    public const string SourceFileChanged = "source_file_changed";
    public const string UnsupportedFile = "unsupported_file";
    public const string MockPageFailure = "mock_page_failure";
    public const string Cancelled = "cancelled";
    public const string Interrupted = "interrupted";
    public const string Unknown = "unknown";
    public const string LocalOcrProcessFailed = "local_ocr_process_failed";
    public const string LocalOcrTimeout = "local_ocr_timeout";
    public const string EmptyOcrOutput = "empty_ocr_output";
    public const string ImageTooLargeForOcr = "image_too_large_for_ocr";
    public const string RendererTimeout = "renderer_timeout";
}
