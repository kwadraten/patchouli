using Patchouli.Core.Agents;

namespace Patchouli.Host.Agents;

/// <summary>
/// Per-library Agent workspace. Patchouli stores one Library per runtime database, so the library data
/// directory is the runtime database directory and the workspace is
/// <c>&lt;runtime database directory&gt;/agent-workspace/</c>. The directory is created on first launch;
/// on Unix it is owner-only, matching the existing agent scratch-workspace convention.
/// </summary>
public sealed class AgentWorkspaceLocator : IAgentWorkspaceLocator
{
    private readonly string _libraryDataDirectory;

    public AgentWorkspaceLocator(string libraryDataDirectory)
    {
        if (string.IsNullOrWhiteSpace(libraryDataDirectory))
        {
            throw new ArgumentException("A library data directory is required.", nameof(libraryDataDirectory));
        }

        _libraryDataDirectory = Path.GetFullPath(libraryDataDirectory);
    }

    public static AgentWorkspaceLocator FromRuntimeDatabase(string runtimeDatabasePath)
    {
        string fullPath = Path.GetFullPath(runtimeDatabasePath);
        string? directory = Path.GetDirectoryName(fullPath);
        return new AgentWorkspaceLocator(string.IsNullOrWhiteSpace(directory) ? AppContext.BaseDirectory : directory);
    }

    public string GetWorkspaceDirectory()
    {
        return AgentWorkspaceLayout.GetDirectory(_libraryDataDirectory);
    }

    public string EnsureWorkspaceDirectory()
    {
        string directory = GetWorkspaceDirectory();
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  PlatformNotSupportedException)
            {
                System.Diagnostics.Debug.WriteLine($"Agent workspace permission update failed: {exception}");
            }
        }

        return directory;
    }
}
