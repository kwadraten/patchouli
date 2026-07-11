using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Patchouli.UI.Diagnostics;

public interface IUnexpectedExceptionSink
{
    string Report(Exception exception, string boundary, string? operation = null);
}

public sealed class FileUnexpectedExceptionSink : IUnexpectedExceptionSink
{
    private const int MaximumExceptionLength = 256 * 1024;
    private static readonly object WriteLock = new();
    private readonly string _path;

    public FileUnexpectedExceptionSink(string directory)
    {
        _path = Path.Combine(directory, "patchouli-crash.log");
        AppPathGuard.ValidateMutablePath(_path);
    }

    public string Report(Exception exception, string boundary, string? operation = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string errorId = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        try
        {
            string detail = Redact(exception.ToString());
            if (detail.Length > MaximumExceptionLength)
            {
                detail = detail[..MaximumExceptionLength] + Environment.NewLine + "[exception truncated]";
            }

            string entry = new StringBuilder()
                .AppendLine(new string('=', 80))
                .Append("TimestampUtc: ").AppendLine(DateTimeOffset.UtcNow.ToString("O"))
                .Append("ErrorId: ").AppendLine(errorId)
                .Append("Boundary: ").AppendLine(Redact(boundary))
                .Append("Operation: ").AppendLine(Redact(operation ?? "unspecified"))
                .Append("Version: ")
                .AppendLine(typeof(FileUnexpectedExceptionSink).Assembly.GetName().Version?.ToString() ?? "unknown")
                .Append("ProcessId: ").AppendLine(Environment.ProcessId.ToString())
                .Append("ThreadId: ").AppendLine(Environment.CurrentManagedThreadId.ToString())
                .Append("OS: ").AppendLine(RuntimeInformation.OSDescription)
                .Append("Runtime: ").AppendLine(RuntimeInformation.FrameworkDescription)
                .AppendLine("Exception:")
                .AppendLine(detail)
                .ToString();

            lock (WriteLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                using FileStream stream = new(_path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096,
                    FileOptions.WriteThrough);
                using StreamWriter writer = new(stream, new UTF8Encoding(false));
                writer.Write(entry);
                writer.Flush();
                stream.Flush(true);
            }
        }
        catch
        {
            try
            {
                Trace.WriteLine($"Patchouli unexpected error {errorId}: {exception}");
            }
            // This is the terminal non-recursive diagnostic fallback; no safer reporter remains.
            // ReSharper disable once EmptyGeneralCatchClause
            catch
            {
            }
        }

        return errorId;
    }

    internal static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        string redacted = SimpleFileLogger.Redact(value);
        redacted = Regex.Replace(redacted, "(?i)(authorization\\s*:\\s*bearer)\\s+[^\\s,;]+", "$1 [redacted]");
        redacted = Regex.Replace(redacted,
            "(?i)(\"(?:secret_value|provider_secret|secret|token|api[_-]?key)\"\\s*:\\s*)\"[^\"]*\"",
            "$1\"[redacted]\"");
        return Regex.Replace(redacted, "(?i)([?&](?:token|api[_-]?key|secret)=)[^&#\\s]+", "$1[redacted]");
    }
}

public sealed class RecordingUnexpectedExceptionSink : IUnexpectedExceptionSink
{
    private readonly Action<Exception, string, string?> _report;

    public RecordingUnexpectedExceptionSink(Action<Exception, string, string?> report)
    {
        _report = report;
    }

    public string Report(Exception exception, string boundary, string? operation = null)
    {
        _report(exception, boundary, operation);
        return "recorded";
    }
}

public static class UnexpectedExceptions
{
    private static IUnexpectedExceptionSink _sink = CreateBootstrapSink();

    public static IUnexpectedExceptionSink Sink
    {
        get => Volatile.Read(ref _sink);
        set => Volatile.Write(ref _sink, value ?? throw new ArgumentNullException(nameof(value)));
    }

    public static IUnexpectedExceptionSink CreateBootstrapSink()
    {
        try
        {
            return new FileUnexpectedExceptionSink(new PlatformAppPaths().Resolve().LogDirectory);
        }
        catch
        {
            return new FileUnexpectedExceptionSink(Path.Combine(Path.GetTempPath(), "Patchouli", "logs"));
        }
    }
}
