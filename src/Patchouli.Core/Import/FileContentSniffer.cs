namespace Patchouli.Core.Import;

/// <summary>
/// Issue #11: pure content sniffing for local import files. The actual file content is
/// checked, never just the extension. No filesystem access happens here.
/// </summary>
public static class FileContentSniffer
{
    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();

    /// <summary>Sniffs the kind from the file's leading bytes.</summary>
    public static LocalImportKind SniffKind(ReadOnlySpan<byte> leadingBytes)
    {
        if (StartsWith(leadingBytes, PdfMagic))
        {
            return LocalImportKind.Pdf;
        }

        if (LooksLikeCslXml(leadingBytes))
        {
            return LocalImportKind.Csl;
        }

        return LocalImportKind.Unknown;
    }

    /// <summary>Parses an explicit kind value; null/empty means "sniff".</summary>
    public static bool TryParseKind(string? value, out LocalImportKind kind)
    {
        kind = LocalImportKind.Unknown;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (string.Equals(value.Trim(), "pdf", StringComparison.OrdinalIgnoreCase))
        {
            kind = LocalImportKind.Pdf;
            return true;
        }

        if (string.Equals(value.Trim(), "csl", StringComparison.OrdinalIgnoreCase))
        {
            kind = LocalImportKind.Csl;
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when the input is a URL rather than a host-local path. MCP <c>add</c> accepts
    /// only local paths; a URL must be rejected before any file access. Windows drive
    /// paths (D:/papers/paper.pdf) are local, not URLs.
    /// </summary>
    public static bool IsUrlLike(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string trimmed = input.Trim();
        // Windows drive letter: D:/... or D:\...
        if (trimmed.Length >= 3 && char.IsLetter(trimmed[0]) && trimmed[1] == ':' &&
            (trimmed[2] == '/' || trimmed[2] == '\\'))
        {
            return false;
        }

        int schemeEnd = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd > 0)
        {
            return IsScheme(trimmed[..schemeEnd]);
        }

        int colon = trimmed.IndexOf(':');
        if (colon > 1 && IsScheme(trimmed[..colon]))
        {
            string rest = trimmed[(colon + 1)..];
            return rest.StartsWith("//", StringComparison.Ordinal) ||
                   string.Equals(trimmed[..colon], "mailto", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsScheme(string candidate)
    {
        if (candidate.Length == 0 || !char.IsLetter(candidate[0]))
        {
            return false;
        }

        foreach (char c in candidate)
        {
            if (!(char.IsLetterOrDigit(c) || c is '+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool StartsWith(ReadOnlySpan<byte> bytes, byte[] prefix)
    {
        return bytes.Length >= prefix.Length && bytes[..prefix.Length].SequenceEqual(prefix);
    }

    /// <summary>
    /// Detects a CSL style document: an XML document whose root element is &lt;style&gt;.
    /// Tolerates a UTF-8 BOM, an XML declaration and leading whitespace/comments.
    /// </summary>
    private static bool LooksLikeCslXml(ReadOnlySpan<byte> bytes)
    {
        int index = 0;
        // Skip UTF-8 BOM.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            index = 3;
        }

        index = SkipWhitespace(bytes, index);
        // Skip an XML declaration.
        if (MatchAscii(bytes, index, "<?xml"))
        {
            int end = IndexOfAscii(bytes, index, "?>");
            if (end < 0)
            {
                return false;
            }

            index = SkipWhitespace(bytes, end + 2);
        }

        // Skip XML comments.
        while (MatchAscii(bytes, index, "<!--"))
        {
            int end = IndexOfAscii(bytes, index, "-->");
            if (end < 0)
            {
                return false;
            }

            index = SkipWhitespace(bytes, end + 3);
        }

        // Root element must be <style followed by whitespace, '>' or '/'.
        if (!MatchAscii(bytes, index, "<style"))
        {
            return false;
        }

        int after = index + "<style".Length;
        return after >= bytes.Length || bytes[after] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'
            or (byte)'>' or (byte)'/';
    }

    private static int SkipWhitespace(ReadOnlySpan<byte> bytes, int index)
    {
        while (index < bytes.Length && bytes[index] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            index++;
        }

        return index;
    }

    private static bool MatchAscii(ReadOnlySpan<byte> bytes, int index, string text)
    {
        if (index < 0 || index + text.Length > bytes.Length)
        {
            return false;
        }

        for (int i = 0; i < text.Length; i++)
        {
            if (bytes[index + i] != (byte)text[i])
            {
                return false;
            }
        }

        return true;
    }

    private static int IndexOfAscii(ReadOnlySpan<byte> bytes, int start, string text)
    {
        for (int i = start; i + text.Length <= bytes.Length; i++)
        {
            if (MatchAscii(bytes, i, text))
            {
                return i;
            }
        }

        return -1;
    }
}
