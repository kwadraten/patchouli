using Patchouli.Core.Results;

namespace Patchouli.Core.Cli;

public sealed record CliInstallation(string? Path, string? Version, bool InPath);

public interface ICliPathService
{
    CliInstallation GetInstallation();

    Result AddToPath();

    Result RemoveFromPath();
}
