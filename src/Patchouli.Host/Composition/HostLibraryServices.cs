using Patchouli.Core.Bibliography;
using Patchouli.Core.Library;
using Patchouli.Core.Time;
using Patchouli.Host.Caching;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Search;

namespace Patchouli.Host.Composition;

/// <summary>Library catalog composition over one connection factory and revision stream.</summary>
internal sealed class HostLibraryServices
{
    public HostLibraryServices(SqliteConnectionFactory connectionFactory, IClock clock,
        Action<Exception, string, string?> reportUnexpectedException)
    {
        Identity = new LibraryIdentityService(connectionFactory, clock);
        Revisions = new LibraryRevisionService(connectionFactory);
        SearchProfiles = new SearchProfileService(connectionFactory, Identity, clock, new OpenccTextConverter());
        Queries = new LibraryItemQueryService(connectionFactory, SearchProfiles);
        Items = new ItemService(connectionFactory, Identity, clock, Revisions);
        Tags = new ItemTagService(connectionFactory, Revisions);
        Collections = new CollectionService(connectionFactory, Identity, clock, Revisions);
        Cache = new LibraryItemCache(Queries, Tags);
        RevisionMonitor = new LibraryRevisionMonitor(Revisions, Cache,
            reportUnexpectedException: reportUnexpectedException);
        Merge = new ItemMergeService(connectionFactory, clock, Identity, Revisions);
        Duplicates = new DuplicateItemDetectionService(connectionFactory, Identity);
    }

    public ILibraryIdentityService Identity { get; }
    public ILibraryRevisionService Revisions { get; }
    public ILibraryItemQueryService Queries { get; }
    public SearchProfileService SearchProfiles { get; }
    public IItemService Items { get; }
    public IItemTagService Tags { get; }
    public ICollectionService Collections { get; }
    public LibraryItemCache Cache { get; }
    public LibraryRevisionMonitor RevisionMonitor { get; }
    public IItemMergeService Merge { get; }
    public IDuplicateItemDetectionService Duplicates { get; }
}
