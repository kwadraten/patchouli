using System.Security.Cryptography;
using System.Text;

namespace Patchouli.Host.Agent;

/// <summary>Device-local scratch files, partitioned by library and session beneath the OS temp directory.</summary>
public sealed class AgentWorkspaceStore
{
    private readonly AgentSessionStore _sessions;
    private readonly string _temporaryRoot;
    private readonly string _namespaceRoot;
    private readonly Lock _gate = new();

    public AgentWorkspaceStore(AgentSessionStore sessions)
        : this(sessions, Path.GetTempPath())
    {
    }

    internal AgentWorkspaceStore(AgentSessionStore sessions, string temporaryRoot)
    {
        _sessions = sessions;
        _temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(temporaryRoot));
        string userKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)))[..16]
            .ToLowerInvariant();
        _namespaceRoot = Path.Combine(_temporaryRoot, "patchouli-agent-workspaces-" + userKey);
        string identity = OperatingSystem.IsWindows()
            ? sessions.SessionsRoot.ToUpperInvariant()
            : sessions.SessionsRoot;
        string libraryKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        Root = Path.Combine(_namespaceRoot, libraryKey);
    }

    public string Root { get; }

    public string Resolve(string sessionId)
    {
        // Reuse the journal's path-segment validation; no supplied path becomes a cleanup target.
        _sessions.ResolveSessionDirectory(sessionId);
        return Path.Combine(Root, sessionId);
    }

    public string Ensure(string sessionId)
    {
        string directory = Resolve(sessionId);
        lock (_gate)
        {
            ValidateParents();
            EnsureNamespace();
            RejectLink(directory);
            Directory.CreateDirectory(directory);
        }

        return directory;
    }

    public string EnsureRoot()
    {
        lock (_gate)
        {
            ValidateParents();
            EnsureNamespace();
            Directory.CreateDirectory(Root);
            return Root;
        }
    }

    public bool Delete(string sessionId)
    {
        string directory = Resolve(sessionId);
        lock (_gate)
        {
            ValidateParents();
            return DeleteEntry(directory);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            ValidateParents();
            if (!Directory.Exists(Root))
            {
                return;
            }

            foreach (string entry in Directory.EnumerateFileSystemEntries(Root))
            {
                DeleteEntry(entry);
            }
        }
    }

    private void ValidateParents()
    {
        RejectLink(_namespaceRoot);
        RejectLink(Root);
    }

    private void EnsureNamespace()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_namespaceRoot);
        }
        else
        {
            Directory.CreateDirectory(_namespaceRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void RejectLink(string path)
    {
        if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("An agent workspace directory cannot be a symbolic link.");
        }
    }

    private bool DeleteEntry(string path)
    {
        string absolute = Path.GetFullPath(path);
        StringComparison comparison =
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!absolute.StartsWith(Root + Path.DirectorySeparatorChar, comparison))
        {
            throw new IOException("Agent workspace cleanup cannot leave its managed root.");
        }

        if (!Path.Exists(absolute))
        {
            return false;
        }

        FileAttributes attributes = File.GetAttributes(absolute);
        if ((attributes & FileAttributes.Directory) == 0)
        {
            File.Delete(absolute);
        }
        else if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(absolute);
        }
        else
        {
            foreach (string child in Directory.EnumerateFileSystemEntries(absolute))
            {
                DeleteEntry(child);
            }

            Directory.Delete(absolute);
        }

        return true;
    }
}
