namespace Patchouli.Infrastructure.Files;

/// <summary>
/// Directory-walk primitives that never follow reparse points (junctions, symlinks,
/// mount points). The render-cache LRU sweep, the ocr-work cleanup and the run/disk
/// artifact deletes all operate inside app-owned cache roots where links are foreign
/// artifacts: a cached scan or delete must never cross a link into external content,
/// and the link entry itself is left in place rather than removed. Every entry point
/// re-checks attributes at the moment it acts so a directory swapped for a link after
/// it was enumerated is still skipped; remaining races only defer work to the next pass.
/// </summary>
public static class ReparsePointGuard
{
    /// <summary>
    /// True when the entry is a reparse point, or when its attributes cannot be read —
    /// an unreadable entry is treated as suspect and skipped rather than walked or deleted.
    /// A missing entry is not a link and cannot be walked or deleted anyway, so it reports
    /// false and lets the caller handle the absent path.
    /// </summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or ArgumentException or NotSupportedException)
        {
            // Missing entries (FileNotFoundException/DirectoryNotFoundException) are not
            // links: reporting false keeps first-render paths — where the cache directory
            // does not exist yet — working. Anything else that failed to report attributes
            // is treated as suspect: skipped, never walked or deleted.
            return exception is not (FileNotFoundException or DirectoryNotFoundException);
        }
    }

    /// <summary>Non-reparse child directories of <paramref name="root"/>.</summary>
    public static IEnumerable<string> EnumerateChildDirectories(string root)
    {
        string[] children;
        try
        {
            children = Directory.EnumerateDirectories(root).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or ArgumentException or NotSupportedException)
        {
            yield break;
        }

        foreach (string child in children)
        {
            // Re-check now, not once at scan start: a link planted between the listing and
            // the check is still skipped, and links are never returned to deleters.
            if (!IsReparsePoint(child))
            {
                yield return child;
            }
        }
    }

    /// <summary>
    /// Every directory inside <paramref name="root"/> reachable without crossing a reparse
    /// point. Optionally yields <paramref name="root"/> itself first (a reparse-point root
    /// yields nothing at all). Attributes are re-verified when each directory is entered.
    /// </summary>
    public static IEnumerable<string> EnumerateDirectoriesRecursive(string root, bool includeRoot = false)
    {
        if (IsReparsePoint(root))
        {
            yield break;
        }

        if (includeRoot)
        {
            yield return root;
        }

        Queue<string> pending = new();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            string current = pending.Dequeue();
            if (current != root && IsReparsePoint(current))
            {
                continue; // became a link after it was enqueued
            }

            foreach (string child in EnumerateChildDirectories(current))
            {
                yield return child;
                pending.Enqueue(child);
            }
        }
    }

    /// <summary>
    /// Every file inside <paramref name="root"/> reachable without crossing a reparse
    /// point; reparse-point files are excluded too, so callers can delete the results.
    /// </summary>
    public static IEnumerable<string> EnumerateFilesRecursive(string root)
    {
        foreach (string directory in EnumerateDirectoriesRecursive(root, true))
        {
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(directory).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or ArgumentException or NotSupportedException)
            {
                continue;
            }

            foreach (string file in files)
            {
                if (!IsReparsePoint(file))
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>
    /// Deletes <paramref name="directory"/> and its contents without ever crossing or
    /// removing a reparse point. When links are present inside, the directories that
    /// still hold them remain; when the root itself is a link nothing happens.
    /// </summary>
    public static void DeleteTree(string directory)
    {
        if (IsReparsePoint(directory))
        {
            return;
        }

        DeleteTreeContents(directory);
        try
        {
            Directory.Delete(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or ArgumentException or NotSupportedException)
        {
            // Leftover reparse points keep the directory non-empty; that is the intended
            // outcome — the entry stays for the owning subsystem or the next pass.
        }
    }

    private static void DeleteTreeContents(string directory)
    {
        string[] files;
        try
        {
            files = Directory.EnumerateFiles(directory).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or ArgumentException or NotSupportedException)
        {
            return;
        }

        foreach (string file in files)
        {
            if (IsReparsePoint(file))
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or ArgumentException or NotSupportedException)
            {
                // Locked or vanished files are retried by the next pass.
            }
        }

        foreach (string child in EnumerateChildDirectories(directory))
        {
            DeleteTree(child);
        }
    }
}
