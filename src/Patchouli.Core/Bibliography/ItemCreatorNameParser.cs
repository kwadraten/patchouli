using System.Text.RegularExpressions;

namespace Patchouli.Core.Bibliography;

public enum ItemCreatorNameMode
{
    Personal,
    Literal
}

public sealed record ItemCreatorNameParts(
    string? Family,
    string? Given,
    string? Literal,
    string? Suffix = null,
    string? Particles = null);

public static partial class ItemCreatorNameParser
{
    private static readonly string[] CommonChineseCompoundSurnames =
    [
        "欧阳", "司马", "上官", "诸葛", "夏侯", "东方", "皇甫", "尉迟", "公孙", "令狐",
        "宇文", "长孙", "慕容", "司徒", "司空", "独孤", "南宫", "万俟", "闻人", "赫连"
    ];

    public static ItemCreatorNameParts Parse(
        string? name,
        ItemCreatorNameMode mode = ItemCreatorNameMode.Personal)
    {
        var normalized = NormalizeWhitespace(name);
        if (normalized is null)
        {
            return new ItemCreatorNameParts(null, null, null);
        }

        if (mode == ItemCreatorNameMode.Literal)
        {
            return new ItemCreatorNameParts(null, null, normalized);
        }

        var commaParts = normalized.Split([',', '，'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (commaParts.Length >= 2)
        {
            return new ItemCreatorNameParts(commaParts[0], string.Join(" ", commaParts.Skip(1)), null);
        }

        if (IsHanName(normalized))
        {
            var family = CommonChineseCompoundSurnames.FirstOrDefault(
                    surname => normalized.StartsWith(surname, StringComparison.Ordinal))
                ?? normalized[..1];
            return new ItemCreatorNameParts(family, normalized[family.Length..], null);
        }

        var parts = normalized.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 1
            ? new ItemCreatorNameParts(parts[0], null, null)
            : new ItemCreatorNameParts(parts[^1], string.Join(" ", parts[..^1]), null);
    }

    private static string? NormalizeWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return WhitespaceRegex().Replace(value.Trim(), " ");
    }

    private static bool IsHanName(string value)
        => value.Length > 1 && value.All(character => character is >= '\u3400' and <= '\u9fff');

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
}
