namespace Patchouli.Core.Diagnostics;

public interface IAppLogger
{
    Task LogAsync(string operation, string message);
}
