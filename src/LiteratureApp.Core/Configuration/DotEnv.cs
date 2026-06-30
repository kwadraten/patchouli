namespace LiteratureApp.Core.Configuration;

public static class DotEnv
{
    public static void LoadNearest(string fileName = ".env")
    {
        var directories = new[]
        {
            Environment.CurrentDirectory,
            AppContext.BaseDirectory
        };

        foreach (var start in directories)
        {
            var filePath = FindUpwards(start, fileName);
            if (filePath is not null)
            {
                LoadIfExists(filePath);
                return;
            }
        }
    }

    public static void LoadIfExists(string filePath)
    {
        if (!File.Exists(filePath))
            return;

        foreach (var pair in ReadFile(filePath))
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(pair.Key)))
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    public static string? ReadValue(string filePath, string key)
    {
        if (!File.Exists(filePath))
            return null;

        return ReadFile(filePath).TryGetValue(key, out var value) ? value : null;
    }

    private static string? FindUpwards(string startDirectory, string fileName)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        return null;
    }

    private static Dictionary<string, string> ReadFile(string filePath)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(filePath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var separator = line.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Length == 0)
                continue;

            values[key] = Unquote(value);
        }

        return values;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            return value[1..^1];

        return value;
    }
}
