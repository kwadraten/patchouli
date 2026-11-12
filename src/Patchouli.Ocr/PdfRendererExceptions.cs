namespace Patchouli.Ocr;

public sealed class PdfRendererUnavailableException : Exception
{
    public PdfRendererUnavailableException(string message, Exception? innerException = null) : base(message,
        innerException)
    {
    }
}

public sealed class PdfRendererTimeoutException : Exception
{
    public PdfRendererTimeoutException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}
