using Patchouli.Core.Ids;

namespace Patchouli.Core.Bibliography;

public static class ItemCreatorRoles
{
    public const string Author = "author";
    public const string Editor = "editor";
    public const string Translator = "translator";
    public const string ContainerAuthor = "container-author";
    public const string Host = "host";
    public const string Producer = "producer";
    public const string Director = "director";
    public const string Composer = "composer";
    public const string Performer = "performer";
    public const string Interviewer = "interviewer";
    public const string Recipient = "recipient";
    public const string ScriptWriter = "script-writer";
    public const string OriginalAuthor = "original-author";
    public const string Organizer = "organizer";
    public const string ReviewedAuthor = "reviewed-author";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        Author,
        Editor,
        Translator,
        ContainerAuthor,
        Host,
        Producer,
        Director,
        Composer,
        Performer,
        Interviewer,
        Recipient,
        ScriptWriter,
        OriginalAuthor,
        Organizer,
        ReviewedAuthor
    };

    /// <summary>Chinese display labels for every supported role; feeds UI dropdowns and dialogs.</summary>
    public static readonly IReadOnlyDictionary<string, string> DisplayLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Author] = "作者",
            [Editor] = "编者",
            [Translator] = "译者",
            [ContainerAuthor] = "文集作者",
            [Host] = "主持人",
            [Producer] = "制片人",
            [Director] = "导演",
            [Composer] = "作曲",
            [Performer] = "表演者",
            [Interviewer] = "访谈者",
            [Recipient] = "收件人",
            [ScriptWriter] = "编剧",
            [OriginalAuthor] = "原作者",
            [Organizer] = "组织者",
            [ReviewedAuthor] = "被评作者"
        };

    public static string DisplayLabelFor(string role)
    {
        return DisplayLabels.TryGetValue(role, out string? label) ? label : role;
    }
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

            IEnumerable<string> pieces = new[] { Given, Particles, Family, Suffix }
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
