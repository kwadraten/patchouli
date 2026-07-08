using Patchouli.Core.Results;

namespace Patchouli.Core.Bibliography;

public interface ICslItemTypeProfileService
{
    Task<Result<IReadOnlyList<CslItemTypeProfile>>> ListProfilesAsync(CancellationToken cancellationToken = default);
    Task<Result<CslItemTypeProfile>> GetProfileAsync(string itemType, CancellationToken cancellationToken = default);
    Task<Result> ValidateItemTypeAsync(string itemType, CancellationToken cancellationToken = default);
}
