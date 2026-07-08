using Patchouli.Core.Bibliography;
using Patchouli.Core.Results;

namespace Patchouli.Core.Csl;

public interface ICslItemMapper
{
    Task<Result<CslMappedItem>> MapAsync(ItemMetadata item, CancellationToken cancellationToken = default);
}
