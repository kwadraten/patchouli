using Patchouli.Infrastructure.Bibliography.Biblatex;
using Xunit.Sdk;

namespace Patchouli.Tests;

/// <summary>
/// Shared biblatex-helper resolution for tests that validate against the
/// real Rust helper binary. A missing binary is an explicit skip with a
/// reason (<see cref="SkipException"/>), never a silent early return: a test
/// that cannot reach the helper must not report success for work it never
/// did. When a binary IS available the tests run for real.
/// </summary>
internal static class BiblatexHelperTestSupport
{
    /// <summary>
    /// The helper path for tests that construct
    /// <see cref="BiblatexHelperClient"/> with an explicit path: the staged
    /// default location, or the in-repo build output under
    /// <c>tools/biblatex-helper/target</c>. Throws a skip when no binary
    /// exists in either place.
    /// </summary>
    public static string RequireHelperPath()
    {
        string staged = BiblatexHelperClient.ResolveDefaultHelperPath();
        if (File.Exists(staged))
        {
            return staged;
        }

        string? built = FindRepositoryBuildOutput();
        if (built is not null)
        {
            return built;
        }

        throw SkipException.ForSkip(
            $"biblatex-helper binary not found (staged path '{staged}', no build output under " +
            "tools/biblatex-helper/target). Build the helper (cargo build --release in " +
            "tools/biblatex-helper) or stage the sidecar binary; this test must not pass " +
            "without exercising the real helper.");
    }

    /// <summary>
    /// Guards tests whose code under test resolves the helper itself (the
    /// Host composition always uses
    /// <see cref="BiblatexHelperClient.ResolveDefaultHelperPath"/>): only the
    /// staged default location counts as "available". Throws a skip when it
    /// is absent.
    /// </summary>
    public static void RequireStagedHelperPath()
    {
        string staged = BiblatexHelperClient.ResolveDefaultHelperPath();
        if (!File.Exists(staged))
        {
            throw SkipException.ForSkip(
                $"biblatex-helper binary not staged at '{staged}'. Stage the sidecar binary " +
                "next to the test assembly; this test must not pass without exercising the " +
                "real helper.");
        }
    }

    private static string? FindRepositoryBuildOutput()
    {
        string fileName = OperatingSystem.IsWindows() ? "biblatex-helper.exe" : "biblatex-helper";
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Patchouli.sln")))
            {
                foreach (string configuration in (string[])["release", "debug"])
                {
                    string candidate = Path.Combine(
                        current.FullName, "tools", "biblatex-helper", "target", configuration, fileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                return null;
            }

            current = current.Parent;
        }

        return null;
    }
}
