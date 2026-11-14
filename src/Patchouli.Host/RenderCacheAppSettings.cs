using Patchouli.Infrastructure.Rendering;

namespace Patchouli.UI;

/// <summary>
/// Disk-side page render cache settings (issue #57), persisted in the JSON "RenderCache"
/// section. The limit bounds cache/page-renders by LRU eviction; the PNG quality feeds the
/// SkiaSharp PNG encoder for newly written renders (existing cached files are not rewritten —
/// the quality is part of the cache token so a change renders fresh files).
/// </summary>
public sealed record RenderCacheAppSettings(long DiskLimitBytes, int PngQuality)
{
    public static RenderCacheAppSettings Default()
    {
        return new RenderCacheAppSettings(PageRenderService.DefaultDiskCacheByteLimit,
            PageRenderService.DefaultPngQuality);
    }

    public RenderCacheAppSettings Normalize()
    {
        return this with
        {
            DiskLimitBytes = Math.Clamp(DiskLimitBytes, PageRenderService.MinDiskCacheByteLimit,
                PageRenderService.MaxDiskCacheByteLimit),
            PngQuality = Math.Clamp(PngQuality, 1, 100)
        };
    }
}
