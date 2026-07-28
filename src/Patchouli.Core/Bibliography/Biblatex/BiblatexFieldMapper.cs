using System.Globalization;
using System.Text.Json;
using Patchouli.Core.Results;

namespace Patchouli.Core.Bibliography.Biblatex;

/// <summary>
/// Maps helper entry DTOs onto Patchouli bibliographic fields using Citation.js
/// <c>plugin-bibtex</c> BibLaTeX→CSL field rules for model-expressible fields only.
/// </summary>
public static class BiblatexFieldMapper
{
    public static Result<BiblatexMappedItem> MapVisibleEntry(BiblatexEntryDto entry)
    {
        if (entry.IsXdata)
        {
            return Result<BiblatexMappedItem>.Failure(
                AppErrorCodes.ValidationFailed,
                "@xdata entries are data containers and cannot be imported as items.");
        }

        if (!entry.VerifyOk)
        {
            string detail = FormatFirstVerifyDiagnostic(entry);
            return Result<BiblatexMappedItem>.Failure(
                AppErrorCodes.BiblatexVerifyFailed,
                $"BibLaTeX entry '{entry.Key}' failed verify(): {detail}");
        }

        string? title = NullIfEmpty(Field(entry, "title"));
        if (title is null)
        {
            return Result<BiblatexMappedItem>.Failure(
                AppErrorCodes.BiblatexMissingTitle,
                $"BibLaTeX entry '{entry.Key}' is missing title. Correct the source entry before importing.");
        }

        string itemType = BiblatexEntryTypeMap.ResolvePatchouliItemType(entry.EntryType, out string? originalType);
        IReadOnlyList<ItemCreatorInput> creators = MapCreators(entry);
        IReadOnlyList<ItemDateInput> dates = MapDates(entry);
        IReadOnlyList<ItemIdentifierInput> identifiers = MapIdentifiers(entry);
        IReadOnlyList<string> tags = entry.Keywords
            .Select(static value => value.Trim())
            .Where(static value => value.Length > 0)
            .ToArray();

        string? publicationTitle = MapPublicationTitle(entry, itemType);
        string? publisher = MapPublisher(entry, itemType);
        string? number = MapNumber(entry, itemType);
        string? issue = MapIssue(entry, itemType);
        string? note = FirstNonEmpty(Field(entry, "note"), Field(entry, "addendum"));
        string? language = FirstNonEmpty(Field(entry, "language"), Field(entry, "langid"));

        return Result<BiblatexMappedItem>.Success(new BiblatexMappedItem(
            itemType,
            originalType,
            title,
            NullIfEmpty(Field(entry, "subtitle")),
            NullIfEmpty(Field(entry, "shorttitle")),
            creators,
            dates,
            identifiers,
            publicationTitle,
            NullIfEmpty(Field(entry, "shortjournal")),
            NullIfEmpty(Field(entry, "series")),
            publisher,
            FirstNonEmpty(Field(entry, "location"), Field(entry, "address")),
            NullIfEmpty(Field(entry, "edition")),
            NullIfEmpty(Field(entry, "type")),
            number,
            NullIfEmpty(Field(entry, "chapter")),
            NullIfEmpty(Field(entry, "volume")),
            NullIfEmpty(Field(entry, "version")),
            issue,
            NullIfEmpty(Field(entry, "pages")),
            language,
            NullIfEmpty(Field(entry, "pubstate")),
            note,
            NullIfEmpty(Field(entry, "abstract")),
            tags,
            NullIfEmpty(entry.File),
            entry.Key,
            entry.EntryType));
    }

    public static string FormatFirstVerifyDiagnostic(BiblatexEntryDto entry)
    {
        if (entry.Verify.Missing.Count > 0)
        {
            return $"missing field '{entry.Verify.Missing[0]}'";
        }

        if (entry.Verify.Superfluous.Count > 0)
        {
            return $"superfluous field '{entry.Verify.Superfluous[0]}'";
        }

        if (entry.Verify.Malformed.Count > 0)
        {
            BiblatexMalformedDto malformed = entry.Verify.Malformed[0];
            return $"malformed field '{malformed.Field}': {malformed.Message}";
        }

        return "unknown verification failure";
    }

    public static IReadOnlySet<int> ExtractIssuedYears(IEnumerable<ItemDateInput> dates)
    {
        HashSet<int> years = [];
        foreach (ItemDateInput date in dates.Where(static d => d.Role == ItemDateRoles.Issued))
        {
            foreach (int year in ExtractYears(date))
            {
                years.Add(year);
            }
        }

        return years;
    }

    public static IReadOnlyList<string> AuthorMatchKeys(IEnumerable<ItemCreatorInput> creators)
    {
        return creators
            .Where(static creator => creator.Role == ItemCreatorRoles.Author)
            .Select(CreatorMatchKey)
            .Where(static key => key.Length > 0)
            .ToArray();
    }

    public static string CreatorMatchKey(ItemCreatorInput creator)
    {
        if (!string.IsNullOrEmpty(creator.Literal))
        {
            return creator.Literal.Trim();
        }

        return string.Join('\u001f', new[]
        {
            creator.Family?.Trim() ?? string.Empty,
            creator.Given?.Trim() ?? string.Empty,
            creator.Particles?.Trim() ?? string.Empty,
            creator.Suffix?.Trim() ?? string.Empty
        });
    }

    public static string CreatorMatchKey(ItemCreator creator)
    {
        return CreatorMatchKey(new ItemCreatorInput(
            creator.Role,
            creator.Family,
            creator.Given,
            creator.Literal,
            creator.Suffix,
            creator.Particles));
    }

    public static IReadOnlySet<int> ExtractYearsFromItemDates(IEnumerable<ItemDate> dates)
    {
        HashSet<int> years = [];
        foreach (ItemDate date in dates.Where(static d => d.Role == ItemDateRoles.Issued))
        {
            foreach (int year in ExtractYears(new ItemDateInput(date.Role, date.DatePartsJson, date.Circa, date.Season,
                         date.Literal)))
            {
                years.Add(year);
            }
        }

        return years;
    }

    public static bool AuthorsMatch(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return false;
        }

        HashSet<string> leftSet = new(left, StringComparer.Ordinal);
        HashSet<string> rightSet = new(right, StringComparer.Ordinal);
        return leftSet.IsSubsetOf(rightSet) || rightSet.IsSubsetOf(leftSet);
    }

    public static bool YearsMatch(IReadOnlySet<int> left, IReadOnlySet<int> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return false;
        }

        return left.Overlaps(right);
    }

    public static string? ExactTrim(string? value)
    {
        return value is null ? null : value.Trim();
    }

    public static bool ExactTrimEquals(string? left, string? right)
    {
        string? a = ExactTrim(left);
        string? b = ExactTrim(right);
        if (a is null || b is null)
        {
            return false;
        }

        return string.Equals(a, b, StringComparison.Ordinal);
    }

    private static IReadOnlyList<ItemCreatorInput> MapCreators(BiblatexEntryDto entry)
    {
        List<ItemCreatorInput> creators = [];
        AppendPersons(creators, entry, "author", ItemCreatorRoles.Author);
        AppendPersons(creators, entry, "editor", ItemCreatorRoles.Editor);
        AppendPersons(creators, entry, "translator", ItemCreatorRoles.Translator);
        AppendPersons(creators, entry, "bookauthor", ItemCreatorRoles.ContainerAuthor);
        return creators;
    }

    private static void AppendPersons(
        List<ItemCreatorInput> creators,
        BiblatexEntryDto entry,
        string roleKey,
        string itemRole)
    {
        if (!entry.Persons.TryGetValue(roleKey, out IReadOnlyList<BiblatexPersonDto>? people))
        {
            return;
        }

        foreach (BiblatexPersonDto person in people)
        {
            creators.Add(new ItemCreatorInput(
                itemRole,
                NullIfEmpty(person.Family),
                NullIfEmpty(person.Given),
                NullIfEmpty(person.Literal),
                NullIfEmpty(person.Suffix),
                NullIfEmpty(person.Prefix)));
        }
    }

    private static IReadOnlyList<ItemDateInput> MapDates(BiblatexEntryDto entry)
    {
        List<ItemDateInput> dates = [];
        if (TryMapDate(entry, "date", ItemDateRoles.Issued, out ItemDateInput? issued) && issued is not null)
        {
            dates.Add(issued);
        }

        if (TryMapDate(entry, "urldate", ItemDateRoles.Accessed, out ItemDateInput? accessed) && accessed is not null)
        {
            dates.Add(accessed);
        }

        if (TryMapDate(entry, "origdate", ItemDateRoles.OriginalDate, out ItemDateInput? original) &&
            original is not null)
        {
            dates.Add(original);
        }

        return dates;
    }

    private static bool TryMapDate(
        BiblatexEntryDto entry,
        string key,
        string role,
        out ItemDateInput? date)
    {
        date = null;
        if (!entry.Dates.TryGetValue(key, out BiblatexDateDto? dto))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(dto.Literal) && dto.Parts.Count == 0)
        {
            date = new ItemDateInput(role, "[]", dto.Circa, Literal: dto.Literal.Trim());
            return true;
        }

        if (dto.Parts.Count == 0)
        {
            return false;
        }

        date = new ItemDateInput(role, JsonSerializer.Serialize(dto.Parts), dto.Circa);
        return true;
    }

    private static IReadOnlyList<ItemIdentifierInput> MapIdentifiers(BiblatexEntryDto entry)
    {
        List<ItemIdentifierInput> identifiers = [];
        AddIdentifier(identifiers, BuiltInIdentifierSchemes.DOI, Field(entry, "doi"));
        AddIdentifier(identifiers, BuiltInIdentifierSchemes.ISBN, Field(entry, "isbn"));
        AddIdentifier(identifiers, BuiltInIdentifierSchemes.ISSN, Field(entry, "issn"));
        AddIdentifier(identifiers, BuiltInIdentifierSchemes.URL, Field(entry, "url"));
        return identifiers;
    }

    private static void AddIdentifier(List<ItemIdentifierInput> identifiers, string scheme, string? value)
    {
        string? normalized = NullIfEmpty(value);
        if (normalized is null)
        {
            return;
        }

        identifiers.Add(new ItemIdentifierInput(scheme, normalized));
    }

    private static string? MapPublicationTitle(BiblatexEntryDto entry, string itemType)
    {
        if (itemType is "article-journal")
        {
            return FirstNonEmpty(
                Field(entry, "journaltitle"),
                Field(entry, "journal"),
                Field(entry, "maintitle"));
        }

        return FirstNonEmpty(
            Field(entry, "maintitle"),
            Field(entry, "booktitle"),
            Field(entry, "journaltitle"),
            Field(entry, "journal"));
    }

    private static string? MapPublisher(BiblatexEntryDto entry, string itemType)
    {
        string? publisher = NullIfEmpty(Field(entry, "publisher"));
        if (publisher is not null)
        {
            return publisher;
        }

        if (itemType is "paper-conference" or "webpage")
        {
            return NullIfEmpty(Field(entry, "organization"));
        }

        if (itemType is "report" or "thesis")
        {
            return FirstNonEmpty(Field(entry, "institution"), Field(entry, "school"), Field(entry, "organization"));
        }

        return FirstNonEmpty(Field(entry, "organization"), Field(entry, "institution"), Field(entry, "school"));
    }

    private static string? MapNumber(BiblatexEntryDto entry, string itemType)
    {
        if (itemType is "patent" or "report")
        {
            return NullIfEmpty(Field(entry, "number"));
        }

        if (itemType is "article-journal")
        {
            return NullIfEmpty(Field(entry, "eid"));
        }

        return NullIfEmpty(Field(entry, "number"));
    }

    private static string? MapIssue(BiblatexEntryDto entry, string itemType)
    {
        if (itemType is "article-journal" or "paper-conference")
        {
            return FirstNonEmpty(Field(entry, "issue"), Field(entry, "number"));
        }

        return NullIfEmpty(Field(entry, "issue"));
    }

    private static IEnumerable<int> ExtractYears(ItemDateInput date)
    {
        if (!string.IsNullOrWhiteSpace(date.Literal))
        {
            if (int.TryParse(date.Literal.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int year))
            {
                yield return year;
            }

            yield break;
        }

        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(date.DatePartsJson) ? "[]" : date.DatePartsJson);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (JsonElement part in document.RootElement.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Array || part.GetArrayLength() == 0)
                {
                    continue;
                }

                if (part[0].TryGetInt32(out int year))
                {
                    yield return year;
                }
            }
        }
    }

    private static string? Field(BiblatexEntryDto entry, string key)
    {
        return entry.Fields.TryGetValue(key, out string? value) ? value : null;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            string? normalized = NullIfEmpty(value);
            if (normalized is not null)
            {
                return normalized;
            }
        }

        return null;
    }

    private static string? NullIfEmpty(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }
}
