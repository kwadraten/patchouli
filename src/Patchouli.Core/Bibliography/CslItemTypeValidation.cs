using Patchouli.Core.Results;

namespace Patchouli.Core.Bibliography;

public static class CslItemTypeValidation
{
    public static Result Validate(string? itemType)
    {
        if (string.IsNullOrWhiteSpace(itemType))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed,
                "Item type is required. Library item types match CSL-JSON type values; use 'general' only for an unclassified item.");
        }

        return CslItemTypeDisplayNames.Names.ContainsKey(itemType.Trim())
            ? Result.Success()
            : Result.Failure(AppErrorCodes.ValidationFailed,
                $"Unsupported item type '{itemType}'. Library item types match CSL-JSON type values (for example, 'article-newspaper', 'periodical', or 'webpage'), with 'general' reserved for unclassified items. Non-standard types and aliases are not accepted.");
    }
}
