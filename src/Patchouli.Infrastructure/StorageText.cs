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

        for (int i = 0; i < value.Length; i++)
        {
            if (IsInvalidCodeUnit(value, i))
            {
                return string.Create(value.Length, (Source: value, FirstInvalid: i), static (destination, state) =>
                {
                    state.Source.AsSpan().CopyTo(destination);
                    destination[state.FirstInvalid] = '\uFFFD';
                    for (int index = state.FirstInvalid + 1; index < state.Source.Length; index++)
                    {
                        if (IsInvalidCodeUnit(state.Source, index))
                        {
                            destination[index] = '\uFFFD';
                        }
                    }
                });
            }
        }

        return value;
    }

    private static bool IsInvalidCodeUnit(string value, int index)
    {
        char codeUnit = value[index];
        return codeUnit == '\uFFFE' ||
               (char.IsHighSurrogate(codeUnit) &&
                (index == value.Length - 1 || !char.IsLowSurrogate(value[index + 1]))) ||
               (char.IsLowSurrogate(codeUnit) &&
                (index == 0 || !char.IsHighSurrogate(value[index - 1])));
    }
}
