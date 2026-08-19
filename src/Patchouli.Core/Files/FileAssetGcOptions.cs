namespace Patchouli.Core.Files;

public sealed record FileAssetGcOptions(TimeSpan? Delay = null, int MaxRetries = 3);
