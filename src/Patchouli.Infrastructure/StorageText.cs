namespace Patchouli.Infrastructure;

/// <summary>
/// Extracted text (PDFium glyph maps, OCR engines) can carry invalid UTF-16 code units —
/// unpaired surrogates and the U+FFFE noncharacter — that break SQLite binding and ICU
/// normalization. Replace them with U+FFFD at the persistence/processing boundary.
/// </summary>
internal static class StorageText
{
    public static string? NormalizeInvalidCodePoints(string? value)
    {
        if (value is null)
        {
            return null;
        }

        bool dirty = false;
        Span<char> chars = stackalloc char[value.Length];
        value.AsSpan().CopyTo(chars);
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] == '\uFFFE')
            {
                chars[i] = '�';
                dirty = true;
            }
            else if (char.IsHighSurrogate(chars[i]) &&
                     (i + 1 >= chars.Length || !char.IsLowSurrogate(chars[i + 1])))
            {
                chars[i] = '�';
                dirty = true;
            }
            else if (char.IsLowSurrogate(chars[i]) &&
                     (i == 0 || !char.IsHighSurrogate(chars[i - 1])))
            {
                chars[i] = '�';
                dirty = true;
            }
        }

        return dirty ? new string(chars) : value;
    }
}
