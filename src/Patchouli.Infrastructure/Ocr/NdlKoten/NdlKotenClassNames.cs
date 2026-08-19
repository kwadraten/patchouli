namespace Patchouli.Infrastructure.Ocr.NdlKoten;

public static class NdlKotenClassNames
{
    public static IReadOnlyDictionary<int, string> Parse(string yamlText)
    {
        Dictionary<int, string> result = new();
        bool inNames = false;
        foreach (string line in yamlText.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("names:", StringComparison.Ordinal))
            {
                inNames = true;
                continue;
            }

            if (!inNames)
            {
                continue;
            }

            if (trimmed.Length == 0 || !char.IsDigit(trimmed[0]))
            {
                continue;
            }

            int colon = trimmed.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            if (int.TryParse(trimmed[..colon].Trim(), out int id))
            {
                result[id] = trimmed[(colon + 1)..].Trim();
            }
        }

        return result;
    }
}
