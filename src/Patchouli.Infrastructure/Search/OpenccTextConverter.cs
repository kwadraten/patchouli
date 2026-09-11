using System.Collections.Concurrent;
using OpenccNetLib;
using Patchouli.Core.Search;

namespace Patchouli.Infrastructure.Search;

/// <summary>
/// Caches one <see cref="Opencc" /> instance per configuration and rejects unsupported configuration names
/// instead of silently falling back to a default conversion.
/// </summary>
public sealed class OpenccTextConverter : IOpenccConverter
{
    private static readonly string[] Supported = Opencc.GetSupportedConfigs().ToArray();
    private readonly ConcurrentDictionary<string, Opencc> _converters = new(StringComparer.Ordinal);

    public IReadOnlyList<string> SupportedConfigs => Supported;

    public string Convert(string config, string text)
    {
        if (string.IsNullOrWhiteSpace(config))
        {
            throw new ArgumentException("OpenCC configuration name is required.", nameof(config));
        }

        string key = config.Trim().ToLowerInvariant();
        if (!Opencc.IsValidConfig(key))
        {
            throw new ArgumentException($"OpenCC configuration '{config}' is not supported.", nameof(config));
        }

        Opencc converter = _converters.GetOrAdd(key, static name => new Opencc(name));
        return converter.Convert(text);
    }
}
