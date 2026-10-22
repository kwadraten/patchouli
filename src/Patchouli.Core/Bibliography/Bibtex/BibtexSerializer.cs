using System.Text;
using Patchouli.Core.Bibliography.Biblatex;

namespace Patchouli.Core.Bibliography.Bibtex;

/// <summary>
/// Serializes classic BibTeX entries (the output of
/// <see cref="BibtexExportMapper"/>) to .bib text.
///
/// This serializer exists because the biblatex-helper writer cannot emit
/// true classic BibTeX: its write path canonicalizes field names to their
/// BibLaTeX forms (<c>journal</c> → <c>journaltitle</c>, <c>address</c> →
/// <c>location</c>, <c>school</c> → <c>institution</c>, the last two
/// colliding when both are present), which classic BibTeX styles silently
/// ignore or reject. Verified by probing the helper binary on 2026-10-22
/// (issue #14, ADR 0051). The BibLaTeX export path keeps using the helper,
/// where those canonical names are the correct ones.
///
/// Values are emitted verbatim inside braces; the mapper has already
/// brace-escaped values whose braces are unbalanced. Field order is
/// alphabetical for determinism.
/// </summary>
public static class BibtexSerializer
{
    public static string Serialize(IEnumerable<BiblatexWriteEntryDto> entries)
    {
        StringBuilder builder = new();
        foreach (BiblatexWriteEntryDto entry in entries)
        {
            builder.Append('@').Append(entry.EntryType).Append('{').Append(entry.Key).AppendLine(",");

            List<string> lines = new();
            foreach (string role in OrderedRoles(entry.Persons))
            {
                string names = string.Join(" and ", entry.Persons[role].Select(FormatPerson));
                lines.Add($"  {role} = {{{names}}}");
            }

            foreach (KeyValuePair<string, string> field in entry.Fields.OrderBy(pair => pair.Key,
                         StringComparer.Ordinal))
            {
                lines.Add($"  {field.Key} = {{{field.Value}}}");
            }

            if (entry.Keywords.Count > 0)
            {
                lines.Add($"  keywords = {{{string.Join(", ", entry.Keywords)}}}");
            }

            builder.AppendLine(string.Join(",\n", lines));
            builder.AppendLine("}");
        }

        return builder.ToString();
    }

    private static IEnumerable<string> OrderedRoles(
        IReadOnlyDictionary<string, IReadOnlyList<BiblatexPersonDto>> persons)
    {
        if (persons.ContainsKey("author"))
        {
            yield return "author";
        }

        if (persons.ContainsKey("editor"))
        {
            yield return "editor";
        }

        foreach (string role in persons.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            if (role is not "author" and not "editor")
            {
                yield return role;
            }
        }
    }

    private static string FormatPerson(BiblatexPersonDto person)
    {
        if (!string.IsNullOrWhiteSpace(person.Literal))
        {
            return person.Literal!;
        }

        string family = string.IsNullOrWhiteSpace(person.Prefix)
            ? person.Family ?? ""
            : $"{person.Prefix} {person.Family}".Trim();
        List<string> parts = new();
        if (family.Length > 0)
        {
            parts.Add(family);
        }

        if (!string.IsNullOrWhiteSpace(person.Given))
        {
            parts.Add(person.Given!);
        }

        if (!string.IsNullOrWhiteSpace(person.Suffix))
        {
            parts.Add(person.Suffix!);
        }

        return string.Join(", ", parts);
    }
}
