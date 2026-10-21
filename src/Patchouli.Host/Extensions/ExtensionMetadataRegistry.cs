using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Infrastructure.Bibliography.MetadataLookup;

namespace Patchouli.Host.Extensions;

/// <summary>Reads the catalog's published metadata registrations at each lookup boundary.</summary>
public sealed class ExtensionMetadataRegistry(
    IMetadataSourceRegistry baseline,
    Func<IReadOnlyList<IMetadataSource>> extensions) : IMetadataSourceRegistry
{
    public IReadOnlyList<MetadataSourceDefinition> Sources => Current().Sources;

    public IReadOnlyList<IMetadataSource> Resolve(string identifierScheme,
        IReadOnlyList<MetadataSourcePreference>? preferences = null)
    {
        return Current().Resolve(identifierScheme, preferences);
    }

    private IMetadataSourceRegistry Current()
    {
        return MetadataSourceRegistry.Combine(baseline, extensions());
    }
}
