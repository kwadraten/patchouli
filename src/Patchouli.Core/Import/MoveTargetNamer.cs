namespace Patchouli.Core.Import;

/// <summary>Identity probe used to compare an existing move-target candidate with the source.</summary>
public sealed record MoveCandidateIdentity(long SizeBytes, string QuickHash);

/// <summary>Planned move target: the path to move to, and whether a move is needed at all.</summary>
public sealed record MoveTargetPlan(string TargetPath, bool Moved);

/// <summary>
/// Issue #11: deterministic move-target naming for the move-to-first-search-root behavior.
/// Rules: a file already inside the target root keeps its position (no nested re-move);
/// an existing same-name file with identical content is reused as-is; same-name files
/// with different content never overwrite — the target name gains a numeric suffix
/// (<c>name-2.pdf</c>, <c>name-3.pdf</c>, ...). Pure and fully testable: all filesystem
/// probes are injected.
/// </summary>
public static class MoveTargetNamer
{
    /// <summary>
    /// Plans the move target. Returns the source path unchanged with <c>Moved=false</c>
    /// when no physical move is needed (already inside the root, or an identical file
    /// already occupies the name).
    /// </summary>
    /// <param name="sourceFullPath">Normalized absolute source path.</param>
    /// <param name="rootFullPath">Normalized absolute target root path.</param>
    /// <param name="sourceIdentity">Identity of the source file (size + quick hash).</param>
    /// <param name="probeTarget">Returns the identity of an existing target candidate, or null.</param>
    public static async Task<MoveTargetPlan> PlanAsync(
        string sourceFullPath,
        string rootFullPath,
        MoveCandidateIdentity sourceIdentity,
        Func<string, Task<MoveCandidateIdentity?>> probeTarget,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFullPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootFullPath);
        ArgumentNullException.ThrowIfNull(probeTarget);

        string sourceDirectory = Path.GetDirectoryName(sourceFullPath) ?? string.Empty;
        if (PathEquals(sourceDirectory, rootFullPath))
        {
            // Already inside the target root: keep position, never re-move.
            return new MoveTargetPlan(sourceFullPath, false);
        }

        string fileName = Path.GetFileName(sourceFullPath);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);

        for (int suffix = 1; ; suffix++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string candidateName = suffix == 1 ? fileName : $"{stem}-{suffix}{extension}";
            string candidate = Path.Combine(rootFullPath, candidateName);
            MoveCandidateIdentity? existing = await probeTarget(candidate).ConfigureAwait(false);
            if (existing is null)
            {
                return new MoveTargetPlan(candidate, true);
            }

            if (existing.SizeBytes == sourceIdentity.SizeBytes &&
                string.Equals(existing.QuickHash, sourceIdentity.QuickHash, StringComparison.Ordinal))
            {
                // Identical content already occupies this name; nothing to move.
                return new MoveTargetPlan(sourceFullPath, false);
            }
            // Same name, different content: keep looking for a free deterministic name.
        }
    }

    private static bool PathEquals(string left, string right)
    {
        string normalizedLeft = Normalize(left);
        string normalizedRight = Normalize(right);
        return OperatingSystem.IsWindows()
            ? string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase)
            : string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);
    }

    private static string Normalize(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }
}
