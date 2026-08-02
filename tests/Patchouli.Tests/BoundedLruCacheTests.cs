using FluentAssertions;
using Patchouli.Infrastructure.Caching;

namespace Patchouli.Tests;

public sealed class BoundedLruCacheTests
{
    [Fact]
    public void Set_and_TryGet_roundtrip_and_touch_lru_order()
    {
        BoundedLruCache<string, string> cache = new(1024);
        cache.Set("a", "1", 10);
        cache.Set("b", "2", 10);

        cache.TryGet("a", out string? a).Should().BeTrue();
        a.Should().Be("1");
        cache.TryGet("b", out string? b).Should().BeTrue();
        b.Should().Be("2");
        cache.Count.Should().Be(2);
        cache.CachedBytes.Should().Be(20);
    }

    [Fact]
    public void Byte_budget_evicts_least_recently_used()
    {
        BoundedLruCache<string, string> cache = new(29);
        cache.Set("a", "1", 10);
        cache.Set("b", "2", 10);
        cache.Set("c", "3", 10);

        cache.Count.Should().Be(2);
        cache.Evictions.Should().Be(1);
        cache.TryGet("a", out _).Should().BeFalse();
        cache.TryGet("b", out _).Should().BeTrue();
        cache.TryGet("c", out _).Should().BeTrue();
        cache.CachedBytes.Should().BeLessThanOrEqualTo(30);
    }

    [Fact]
    public void Pinned_entries_survive_eviction_and_are_evicted_after_unpin()
    {
        BoundedLruCache<string, string> cache = new(45);
        cache.Set("a", "1", 20, true);
        cache.Set("b", "2", 10);
        cache.Set("c", "3", 10);

        cache.IsPinned("a").Should().BeTrue();

        // Adding d pushes the cache over budget; the pinned a must survive and the oldest
        // evictable entry (b) is dropped instead.
        cache.Set("d", "4", 10);
        cache.TryGet("a", out _).Should().BeTrue();
        cache.TryGet("b", out _).Should().BeFalse();
        cache.TryGet("c", out _).Should().BeTrue();
        cache.TryGet("d", out _).Should().BeTrue();

        cache.Unpin("a");
        cache.Set("e", "5", 10);
        cache.TryGet("a", out _).Should().BeFalse("the unpinned entry is now the oldest evictable value");
        cache.Count.Should().Be(3);
    }

    [Fact]
    public void Oversized_entry_is_not_cached_at_all()
    {
        BoundedLruCache<string, string> cache = new(10);
        cache.Set("big", "x", 100);

        cache.Count.Should().Be(0);
        cache.CachedBytes.Should().Be(0);
        cache.TryGet("big", out _).Should().BeFalse();
    }

    [Fact]
    public void Metrics_track_hits_misses_and_evictions()
    {
        BoundedLruCache<string, string> cache = new(20);
        cache.TryGet("missing", out _).Should().BeFalse();

        cache.Misses.Should().Be(1);
        cache.Set("a", "1", 10);
        cache.Set("b", "2", 10);
        cache.TryGet("a", out _).Should().BeTrue();
        cache.Hits.Should().Be(1);

        cache.Set("c", "3", 10);
        cache.Evictions.Should().Be(1);
    }

    [Fact]
    public void Set_replaces_existing_entry_and_resizes_bytes()
    {
        BoundedLruCache<string, string> cache = new(100);
        cache.Set("a", "1", 10);
        cache.Set("a", "longer", 30);

        cache.Count.Should().Be(1);
        cache.CachedBytes.Should().Be(30);
        cache.TryGet("a", out string? value).Should().BeTrue();
        value.Should().Be("longer");
    }

    [Fact]
    public void EvictWhere_removes_matching_entries_without_clearing_others()
    {
        BoundedLruCache<string, string> cache = new(1024);
        cache.Set("doc-1|page-0", "a", 10);
        cache.Set("doc-1|page-1", "b", 10);
        cache.Set("doc-2|page-0", "c", 10);

        cache.EvictWhere(key => key.StartsWith("doc-1", StringComparison.Ordinal)).Should().Be(2);

        cache.Count.Should().Be(1);
        cache.TryGet("doc-2|page-0", out _).Should().BeTrue();
        cache.CachedBytes.Should().Be(10);
    }

    [Fact]
    public void Clear_resets_count_bytes_and_pins()
    {
        BoundedLruCache<string, string> cache = new(1024);
        cache.Set("a", "1", 10, true);
        cache.Set("b", "2", 20);

        cache.Clear();

        cache.Count.Should().Be(0);
        cache.CachedBytes.Should().Be(0);
        cache.TryGet("a", out _).Should().BeFalse();
    }

    [Fact]
    public void Pin_after_set_protects_and_Get_touches_recently()
    {
        BoundedLruCache<string, string> cache = new(29);
        cache.Set("a", "1", 10);
        cache.Set("b", "2", 10);
        cache.Pin("a");

        // Touching b keeps it most-recent; a is pinned and must not be evicted.
        cache.TryGet("b", out _).Should().BeTrue();
        cache.Set("c", "3", 10);
        cache.TryGet("a", out _).Should().BeTrue();
        cache.TryGet("b", out _).Should().BeFalse();
    }
}
