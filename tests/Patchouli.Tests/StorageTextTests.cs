using FluentAssertions;
using Patchouli.Infrastructure;

namespace Patchouli.Tests;

public sealed class StorageTextTests
{
    [Fact]
    public void Normalize_returns_original_long_text_with_valid_surrogates()
    {
        string text = new string('a', 1024 * 1024) + "\U0001F600";

        StorageText.NormalizeInvalidCodePoints(text).Should().BeSameAs(text);
    }

    [Fact]
    public void Normalize_replaces_only_invalid_code_units_in_long_text()
    {
        string prefix = new('a', 1024 * 1024);
        string text = "\uDC00" + prefix + "\uFFFE\uD800\U0001F600\uD800";

        StorageText.NormalizeInvalidCodePoints(text).Should().Be("\uFFFD" + prefix + "\uFFFD\uFFFD\U0001F600\uFFFD");
    }
}
