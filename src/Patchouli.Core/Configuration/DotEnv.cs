namespace Patchouli.Core.Configuration;

public static class DotEnv
{
    public static string? ReadValue(string filePath, string key)
    {
        if (!File.Exists(filePath))
            return null;

        return ReadFile(filePath).TryGetValue(key, out var value) ? value : null;
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
