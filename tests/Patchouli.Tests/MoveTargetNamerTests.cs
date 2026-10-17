using FluentAssertions;
using Patchouli.Core.Import;

namespace Patchouli.Tests;

/// <summary>
/// Issue #11: deterministic move-target naming for move-to-first-search-root.
/// Pure function; filesystem probes are injected.
/// </summary>
public sealed class MoveTargetNamerTests
{
    private static string Root => Path.Combine(Path.DirectorySeparatorChar.ToString(), "roots", "first");

    private static Task<MoveCandidateIdentity?> NoExisting(string candidate) =>
        Task.FromResult<MoveCandidateIdentity?>(null);

    [Fact]
    public async Task PlanAsync_file_already_inside_root_keeps_position()
    {
        string source = Path.Combine(Root, "paper.pdf");

        MoveTargetPlan plan = await MoveTargetNamer.PlanAsync(
            source, Root, new MoveCandidateIdentity(100, "hash"), NoExisting);

        plan.Moved.Should().BeFalse();
        plan.TargetPath.Should().Be(source);
    }

    [Fact]
    public async Task PlanAsync_free_name_moves_to_root()
    {
        string source = Path.Combine(Path.DirectorySeparatorChar.ToString(), "incoming", "paper.pdf");

        MoveTargetPlan plan = await MoveTargetNamer.PlanAsync(
            source, Root, new MoveCandidateIdentity(100, "hash"), NoExisting);

        plan.Moved.Should().BeTrue();
        plan.TargetPath.Should().Be(Path.Combine(Root, "paper.pdf"));
    }

    [Fact]
    public async Task PlanAsync_identical_content_at_name_reuses_as_is()
    {
        string source = Path.Combine(Path.DirectorySeparatorChar.ToString(), "incoming", "paper.pdf");
        var sourceIdentity = new MoveCandidateIdentity(100, "same-hash");
        Task<MoveCandidateIdentity?> Probe(string candidate) =>
            Task.FromResult<MoveCandidateIdentity?>(new MoveCandidateIdentity(100, "same-hash"));

        MoveTargetPlan plan = await MoveTargetNamer.PlanAsync(source, Root, sourceIdentity, Probe);

        // No physical move; the caller keeps the original path (already registered content).
        plan.Moved.Should().BeFalse();
        plan.TargetPath.Should().Be(source);
    }

    [Fact]
    public async Task PlanAsync_same_name_different_content_gains_numeric_suffix()
    {
        string source = Path.Combine(Path.DirectorySeparatorChar.ToString(), "incoming", "paper.pdf");
        var sourceIdentity = new MoveCandidateIdentity(100, "new-hash");
        Task<MoveCandidateIdentity?> Probe(string candidate) =>
            Task.FromResult<MoveCandidateIdentity?>(
                candidate.EndsWith("paper.pdf", StringComparison.Ordinal)
                    ? new MoveCandidateIdentity(200, "other-hash")
                    : null);

        MoveTargetPlan plan = await MoveTargetNamer.PlanAsync(source, Root, sourceIdentity, Probe);

        plan.Moved.Should().BeTrue();
        plan.TargetPath.Should().Be(Path.Combine(Root, "paper-2.pdf"));
    }

    [Fact]
    public async Task PlanAsync_skips_multiple_taken_names_deterministically()
    {
        string source = Path.Combine(Path.DirectorySeparatorChar.ToString(), "incoming", "paper.pdf");
        var sourceIdentity = new MoveCandidateIdentity(100, "new-hash");
        Task<MoveCandidateIdentity?> Probe(string candidate) =>
            Task.FromResult<MoveCandidateIdentity?>(
                candidate.EndsWith("paper-2.pdf", StringComparison.Ordinal) ||
                candidate.EndsWith("paper.pdf", StringComparison.Ordinal)
                    ? new MoveCandidateIdentity(999, "other-hash")
                    : null);

        MoveTargetPlan plan = await MoveTargetNamer.PlanAsync(source, Root, sourceIdentity, Probe);

        plan.Moved.Should().BeTrue();
        plan.TargetPath.Should().Be(Path.Combine(Root, "paper-3.pdf"));
    }

    [Fact]
    public async Task PlanAsync_same_plan_for_same_probes_is_deterministic()
    {
        string source = Path.Combine(Path.DirectorySeparatorChar.ToString(), "incoming", "paper.pdf");
        var sourceIdentity = new MoveCandidateIdentity(100, "new-hash");
        Task<MoveCandidateIdentity?> Probe(string candidate) =>
            Task.FromResult<MoveCandidateIdentity?>(new MoveCandidateIdentity(5, "taken"));

        MoveTargetPlan first = await MoveTargetNamer.PlanAsync(source, Root, sourceIdentity, Probe);
        MoveTargetPlan second = await MoveTargetNamer.PlanAsync(source, Root, sourceIdentity, Probe);

        first.Should().Be(second);
    }

    [Fact]
    public async Task PlanAsync_preserves_extension_in_suffixed_names()
    {
        string source = Path.Combine(Path.DirectorySeparatorChar.ToString(), "incoming", "my.paper.v2.pdf");
        var sourceIdentity = new MoveCandidateIdentity(100, "new-hash");
        Task<MoveCandidateIdentity?> Probe(string candidate) =>
            Task.FromResult<MoveCandidateIdentity?>(
                candidate.EndsWith("my.paper.v2.pdf", StringComparison.Ordinal)
                    ? new MoveCandidateIdentity(5, "taken")
                    : null);

        MoveTargetPlan plan = await MoveTargetNamer.PlanAsync(source, Root, sourceIdentity, Probe);

        plan.TargetPath.Should().Be(Path.Combine(Root, "my.paper.v2-2.pdf"));
    }
}
