using Patchouli.Core.Ids;

namespace Patchouli.Core.Bibliography;

public static class ItemCreatorRoles
{
    public const string Author = "author";
    public const string Editor = "editor";
    public const string Translator = "translator";
    public const string ContainerAuthor = "container-author";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        Author,
        Editor,
        Translator,
        ContainerAuthor
    };
}

public sealed record ItemCreator(
    string CreatorId,
    ItemId ItemId,
    string Role,
    string? Family,
    string? Given,
    string? Literal,
    string? Suffix,
    string? Particles,
    int SequenceIndex,
    DateTimeOffset CreatedAt)
{
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Literal))
            {
                return Literal.Trim();
            }

            var pieces = new[] { Given, Particles, Family, Suffix }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim());
            return string.Join(" ", pieces);
        }
    }
}

public sealed record ItemCreatorInput(
    string Role,
    string? Family = null,
    string? Given = null,
    string? Literal = null,
    string? Suffix = null,
    string? Particles = null);
