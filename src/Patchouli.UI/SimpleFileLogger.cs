namespace Patchouli.UI;

public interface IAppLogger
{
    Task LogAsync(string operation, string message);
}

public sealed class SimpleFileLogger : IAppLogger
{
    private readonly string _path;
    public SimpleFileLogger(string directory) { _path = Path.Combine(directory, "patchouli.log"); }
    public Task LogAsync(string operation, string message)
    {
        var safe = Redact(message);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        return File.AppendAllTextAsync(_path, $"{DateTimeOffset.UtcNow:O} {operation} {safe}{Environment.NewLine}");
    }
    public static string Redact(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return message;
        return System.Text.RegularExpressions.Regex.Replace(message, "(?i)(secret_value|provider[_-]?secret|secret|token|api[_-]?key)\\s*[:=]\\s*[^\\s,;]+", "$1=[redacted]");
    }
}
