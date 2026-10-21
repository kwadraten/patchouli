using FluentAssertions;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Tests;

public sealed class ExtensionPackageSnapshotTests
{
    [Fact]
    public void Fingerprint_is_independent_of_source_insertion_order_and_path_separators()
    {
        ExtensionPackageSnapshot first = new("sample.extension", new Dictionary<string, string>
        {
            ["main.fsx"] = "let translate = id",
            ["lib\\helpers.fsx"] = "let helper = 1"
        });
        ExtensionPackageSnapshot second = new("sample.extension", new Dictionary<string, string>
        {
            ["lib/helpers.fsx"] = "let helper = 1",
            ["main.fsx"] = "let translate = id"
        });

        first.Fingerprint.Should().Be(second.Fingerprint);
        first.Sources.Keys.Should().Contain("lib/helpers.fsx");
    }

    [Fact]
    public void Fingerprint_changes_when_any_source_content_changes()
    {
        ExtensionPackageSnapshot original = new("sample.extension", new Dictionary<string, string>
        {
            ["main.fsx"] = "let translate = id",
            ["README.md"] = "Example"
        });
        ExtensionPackageSnapshot changed = new("sample.extension", new Dictionary<string, string>
        {
            ["main.fsx"] = "let translate value = value",
            ["README.md"] = "Example"
        });

        changed.Fingerprint.Should().NotBe(original.Fingerprint);
    }

    [Fact]
    public void Snapshot_copies_source_map_and_exposes_it_as_read_only()
    {
        Dictionary<string, string> sources = new() { ["main.fsx"] = "let translate = id" };
        ExtensionPackageSnapshot snapshot = new("sample.extension", sources);
        string fingerprint = snapshot.Fingerprint;

        sources["main.fsx"] = "changed after snapshot";

        snapshot.Sources["main.fsx"].Should().Be("let translate = id");
        snapshot.Fingerprint.Should().Be(fingerprint);
        Action mutate = () => ((IDictionary<string, string>)snapshot.Sources)["main.fsx"] = "mutated";
        mutate.Should().Throw<NotSupportedException>();
    }
}
