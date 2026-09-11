namespace Patchouli.Infrastructure.Ocr.NdlLite;

/// <summary>
/// Reads the detector label list from the official <c>config/ndl.yaml</c>.
/// The DEIM model reports one-based labels; index <c>i</c> of the returned list is
/// label <c>i + 1</c>, matching the official <c>classes = yaml['names']</c> dict.
/// </summary>
public static class NdlLiteClassNames
{
    public const int TextBlockIndex = 0;
    public const int LineMainIndex = 1;
    public const int BlockAdIndex = 7;
    public const int BlockTableIndex = 15;

    private const string TextBlockName = "text_block";
    private const string LinePrefix = "line_";
    private const string BlockAdName = "block_ad";
    private const string BlockTableName = "block_table";

    public static IReadOnlyList<string> Parse(string yamlText)
    {
        SortedDictionary<int, string> names = new();
        bool inNames = false;
        foreach (string rawLine in yamlText.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = rawLine.Trim();
            if (trimmed.StartsWith("names:", StringComparison.Ordinal))
            {
                inNames = true;
                continue;
            }

            if (!inNames || trimmed.Length == 0 || !char.IsDigit(trimmed[0]))
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
                names[id] = trimmed[(colon + 1)..].Trim();
            }
        }

        if (names.Count == 0)
        {
            throw new InvalidOperationException("No class names found in ndl.yaml.");
        }

        string[] ordered = new string[names.Keys.Max() + 1];
        foreach ((int id, string name) in names)
        {
            ordered[id] = name;
        }

        for (int i = 0; i < ordered.Length; i++)
        {
            if (ordered[i] is null)
            {
                throw new InvalidOperationException($"ndl.yaml is missing the class name for id {i}.");
            }
        }

        return ordered;
    }

    /// <summary>
    /// Returns whether a class index is a text line the recognizer should read.
    /// The official <c>get_relationship_rect</c> only considers names starting with
    /// <c>line_</c> as recognizable lines.
    /// </summary>
    public static bool IsTextLine(IReadOnlyList<string> classNames, int classIndex)
    {
        if (classIndex < 0 || classIndex >= classNames.Count)
        {
            return false;
        }

        return classNames[classIndex].StartsWith(LinePrefix, StringComparison.Ordinal);
    }

    public static bool IsTextBlock(IReadOnlyList<string> classNames, int classIndex)
    {
        return classIndex >= 0 && classIndex < classNames.Count
                               && string.Equals(classNames[classIndex], TextBlockName, StringComparison.Ordinal);
    }

    public static bool IsAdBlock(IReadOnlyList<string> classNames, int classIndex)
    {
        return classIndex >= 0 && classIndex < classNames.Count
                               && string.Equals(classNames[classIndex], BlockAdName, StringComparison.Ordinal);
    }

    public static bool IsTableBlock(IReadOnlyList<string> classNames, int classIndex)
    {
        return classIndex >= 0 && classIndex < classNames.Count
                               && string.Equals(classNames[classIndex], BlockTableName, StringComparison.Ordinal);
    }
}
