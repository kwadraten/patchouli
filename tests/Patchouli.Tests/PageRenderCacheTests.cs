using System.Diagnostics;
using System.Text.RegularExpressions;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Host.Settings;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Rendering;
using Patchouli.Infrastructure.Search;
using Patchouli.Ocr;
using Patchouli.UI;

namespace Patchouli.Tests;

/// <summary>
/// Issue #57: the page-render disk cache is a bounded LRU under cache/page-renders with a
/// user-configurable limit and PNG quality. These tests pin the filename layout, cache-hit
/// access stamps, stale source-token sibling cleanup, the sweep's orphan/legacy/temp
/// collection, LRU eviction, document-delete clearing, OCR run work-artifact cleanup, the
/// maintenance service and the persisted settings section.
/// </summary>
public sealed class PageRenderCacheTests
{
    [Fact]
    public async Task Render_writes_readable_tokenized_file_name()
    {
        await using Context c = await Context.CreateAsync();

        string path = await RenderAndPathAsync(c);

        Path.GetFileName(path).Should().MatchRegex(
            $"^{Regex.Escape(c.PageId.ToString())}_t[0-9a-f]{{12}}_s[0-9a-f]{{12}}\\.png$");
        Path.GetFileName(Path.GetDirectoryName(path)!).Should().Be(c.DocumentInstanceId.ToString());
    }

    [Fact]
    public async Task Cache_hit_refreshes_last_access_for_the_whole_family()
    {
        await using Context c = await Context.CreateAsync();
        string path = await RenderAndPathAsync(c);
        string derivative = path[..^4] + ".rot90.png";
        await File.WriteAllBytesAsync(derivative, [137, 80, 78, 71]);
        DateTime oldAccess = DateTime.UtcNow - TimeSpan.FromDays(3);
        File.SetLastAccessTimeUtc(path, oldAccess);
        File.SetLastAccessTimeUtc(derivative, oldAccess);

        Result<PageRenderResult> result = await c.RenderAsync();

        result.Value.Status.Should().Be(PageRenderStatus.FromCache);
        c.Renderer.CallCount.Should().Be(1);
        DateTime cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
        File.GetLastAccessTimeUtc(path).Should().BeAfter(cutoff);
        File.GetLastAccessTimeUtc(derivative).Should().BeAfter(cutoff,
            "a cache hit keeps the rotated/cropped derivatives alive together with the base render");
    }

    [Fact]
    public async Task Force_rerender_deletes_stale_source_token_siblings()
    {
        await using Context c = await Context.CreateAsync();
        string path = await RenderAndPathAsync(c);
        string stem = Path.GetFileNameWithoutExtension(path);
        string directory = Path.GetDirectoryName(path)!;
        string staleBase = Path.Combine(directory, stem[..52] + "aaaaaaaaaaaa.png");
        string staleDerivative = Path.Combine(directory, stem[..52] + "aaaaaaaaaaaa.rot90.png");
        await File.WriteAllBytesAsync(staleBase, [137, 80, 78, 71]);
        await File.WriteAllBytesAsync(staleDerivative, [137, 80, 78, 71]);
        string ownDerivative = path[..^4] + ".rot90.png";
        await File.WriteAllBytesAsync(ownDerivative, [137, 80, 78, 71]);

        string reRendered = await RenderAndPathAsync(c, true);

        reRendered.Should().Be(path);
        File.Exists(staleBase).Should().BeFalse(
            "the old source-hash family is removed once a fresh render exists");
        File.Exists(staleDerivative).Should().BeFalse();
        File.Exists(ownDerivative).Should().BeTrue(
            "derivatives of the current source token are kept");
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task Quality_change_writes_a_new_family_and_updates_the_encoder()
    {
        await using Context c = await Context.CreateAsync(pngQuality: 60);
        string first = await RenderAndPathAsync(c);
        c.Renderer.LastPngQuality.Should().Be(60);

        c.Service.SetPngQuality(80);
        c.Service.PngQuality.Should().Be(80);
        string second = await RenderAndPathAsync(c, true);

        c.Renderer.LastPngQuality.Should().Be(80);
        second.Should().NotBe(first);
        File.Exists(first).Should().BeTrue(
            "a quality change starts a new source-token family; the old files retire through LRU");
    }

    [Fact]
    public async Task SetPngQuality_clamps_to_the_supported_range()
    {
        await using Context c = await Context.CreateAsync();

        c.Service.SetPngQuality(0);
        c.Service.PngQuality.Should().Be(1);
        c.Service.SetPngQuality(250);
        c.Service.PngQuality.Should().Be(100);
    }

    [Fact]
    public async Task Sweep_removes_tmp_orphan_and_legacy_files_with_grace_periods()
    {
        await using Context c = await Context.CreateAsync();
        string path = await RenderAndPathAsync(c);
        string directory = Path.GetDirectoryName(path)!;
        DateTime oldWrite = DateTime.UtcNow - TimeSpan.FromDays(2);
        DateTime recent = DateTime.UtcNow - TimeSpan.FromMinutes(1);

        string staleTmp = path[..^4] + ".aaaa.tmp";
        string freshTmp = path[..^4] + ".bbbb.tmp";
        string legacyFile = Path.Combine(directory, "legacy-opaque-token.png");
        string foreignFile = Path.Combine(directory, "foreign.txt");
        string orphanDerivative = Path.Combine(directory,
            $"{c.PageId}_t0123456789ab_scdef01234567.rot90.png");
        string freshOrphan = Path.Combine(directory,
            $"{c.PageId}_t0123456789ab_scdef01234567.region-001122334455.png");
        File.WriteAllText(staleTmp, "tmp");
        File.WriteAllText(freshTmp, "tmp");
        File.WriteAllText(legacyFile, "legacy");
        File.WriteAllText(foreignFile, "foreign");
        File.WriteAllText(orphanDerivative, "orphan");
        File.WriteAllText(freshOrphan, "orphan");
        File.SetLastWriteTimeUtc(staleTmp, oldWrite);
        File.SetLastWriteTimeUtc(legacyFile, oldWrite);
        File.SetLastWriteTimeUtc(foreignFile, oldWrite);
        File.SetLastWriteTimeUtc(orphanDerivative, oldWrite);
        File.SetLastWriteTimeUtc(freshTmp, recent);
        File.SetLastWriteTimeUtc(freshOrphan, recent);

        await c.Service.SweepDiskCacheAsync();

        File.Exists(staleTmp).Should().BeFalse("leftover temp files older than 5 minutes go first");
        File.Exists(legacyFile).Should().BeFalse(
            "unparseable legacy names older than a day are collected");
        File.Exists(foreignFile).Should().BeFalse();
        File.Exists(orphanDerivative).Should().BeFalse(
            "a derivative whose base render is gone is collected past the fresh grace");
        File.Exists(freshTmp).Should().BeTrue("files younger than the grace are left for the next sweep");
        File.Exists(freshOrphan).Should().BeTrue();
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task Lru_evicts_oldest_accessed_families_until_under_limit()
    {
        await using Context c = await Context.CreateAsync(1024);
        await c.AddSecondPageAsync();
        await c.AddThirdPageAsync();
        string first = await RenderAndPathAsync(c);
        string second = await RenderAndPathAsync(c, page: c.SecondPageId!.Value);
        string third = await RenderAndPathAsync(c, page: c.ThirdPageId!.Value);
        DateTime now = DateTime.UtcNow;
        foreach ((string file, TimeSpan age) in new[]
                 {
                     (first, TimeSpan.FromDays(3)),
                     (second, TimeSpan.FromDays(2)),
                     (third, TimeSpan.FromDays(1))
                 })
        {
            File.SetLastWriteTimeUtc(file, now - age);
            File.SetLastAccessTimeUtc(file, now - age);
        }

        await c.Service.SweepDiskCacheAsync();

        File.Exists(first).Should().BeFalse("the least recently accessed family evicts first");
        File.Exists(second).Should().BeFalse();
        File.Exists(third).Should().BeTrue("the most recently used render survives");
        (await c.Service.GetCacheUsageAsync()).TotalBytes.Should().BeLessThanOrEqualTo(1024);
    }

    [Fact]
    public async Task SetDiskCacheLimit_clamps_and_runs_a_sweep_that_converges()
    {
        await using Context c = await Context.CreateAsync();
        string path = await RenderAndPathAsync(c);
        string staleTmp = path[..^4] + ".aaaa.tmp";
        File.WriteAllText(staleTmp, "tmp");
        File.SetLastWriteTimeUtc(staleTmp, DateTime.UtcNow - TimeSpan.FromDays(2));

        c.Service.SetDiskCacheLimit(1);
        c.Service.DiskCacheLimitBytes.Should().Be(PageRenderService.MinDiskCacheByteLimit);
        c.Service.SetDiskCacheLimit(PageRenderService.MaxDiskCacheByteLimit * 4);
        c.Service.DiskCacheLimitBytes.Should().Be(PageRenderService.MaxDiskCacheByteLimit);

        bool converged = await WaitForConditionAsync(() => !File.Exists(staleTmp), TimeSpan.FromSeconds(15));
        converged.Should().BeTrue("the limit setter schedules a sweep instead of waiting for maintenance");
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task Usage_and_clear_cover_every_document_cache_directory()
    {
        await using Context c = await Context.CreateAsync();
        string path = await RenderAndPathAsync(c);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "notes.txt"), "x");

        PageRenderCacheUsage usage = await c.Service.GetCacheUsageAsync();
        usage.CacheRoot.Should().Be(Path.GetFullPath(c.CacheRoot));
        usage.FileCount.Should().Be(2);
        usage.TotalBytes.Should().BeGreaterThan(0);

        await c.Service.ClearDiskCacheAsync();

        Directory.Exists(c.CacheRoot).Should().BeFalse();
        (await c.Service.GetCacheUsageAsync()).FileCount.Should().Be(0);
    }

    [Fact]
    public async Task Removing_a_document_instance_clears_its_render_cache()
    {
        await using Context c = await Context.CreateAsync();
        await RenderAndPathAsync(c);
        string documentDirectory = Path.Combine(c.CacheRoot, c.DocumentInstanceId.ToString());
        Directory.Exists(documentDirectory).Should().BeTrue();

        Result<DocumentInstanceRemoval> removal =
            await c.Documents.RemoveDocumentInstanceAsync(c.ItemId, c.DocumentInstanceId);

        removal.IsSuccess.Should().BeTrue(removal.ErrorMessage);
        Directory.Exists(documentDirectory).Should().BeFalse(
            "the page-render cache directory is cleared right after the committed delete");
    }

    [Fact]
    public async Task Purging_an_item_clears_render_caches_of_its_documents()
    {
        await using Context c = await Context.CreateAsync();
        await RenderAndPathAsync(c);
        string documentDirectory = Path.Combine(c.CacheRoot, c.DocumentInstanceId.ToString());
        (await c.Items.DeleteItemsAsync([c.ItemId])).IsSuccess.Should().BeTrue();

        Result purged = await c.Purge.PurgeItemsAsync([c.ItemId]);

        purged.IsSuccess.Should().BeTrue(purged.ErrorMessage);
        Directory.Exists(documentDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task Inventory_describes_and_clears_the_render_cache()
    {
        await using Context c = await Context.CreateAsync();
        await RenderAndPathAsync(c);
        ManagedFileInventoryService inventory = new(null!, null!, UnusedStorage(), null!, null!, c.Service);

        Result<PageRenderCacheDescription> described = await inventory.DescribePageRenderCacheAsync();

        described.IsSuccess.Should().BeTrue(described.ErrorMessage);
        described.Value.FileCount.Should().Be(1);
        described.Value.TotalBytes.Should().BeGreaterThan(0);
        described.Value.DiskLimitBytes.Should().Be(PageRenderService.DefaultDiskCacheByteLimit);
        described.Value.PngQuality.Should().Be(PageRenderService.DefaultPngQuality);

        (await inventory.ClearPageRenderCacheAsync()).IsSuccess.Should().BeTrue();
        Directory.Exists(c.CacheRoot).Should().BeFalse();
    }

    [Fact]
    public async Task Maintenance_sweeps_the_render_cache_and_ocr_work_roots()
    {
        await using Context c = await Context.CreateAsync();
        string path = await RenderAndPathAsync(c);
        string staleTmp = path[..^4] + ".aaaa.tmp";
        File.WriteAllText(staleTmp, "tmp");
        File.SetLastWriteTimeUtc(staleTmp, DateTime.UtcNow - TimeSpan.FromDays(2));

        string root = Path.Combine(c.RootDirectory, "storage");
        OcrStorageLocations storage = StorageAt(root);
        string staleArtifact = Path.Combine(storage.MinerUWorkDirectory, "run-1", "p0", "artifact.txt");
        string freshArtifact = Path.Combine(storage.NdlLiteWorkDirectory, "run-2", "artifact.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(staleArtifact)!);
        Directory.CreateDirectory(Path.GetDirectoryName(freshArtifact)!);
        File.WriteAllText(staleArtifact, "old");
        File.WriteAllText(freshArtifact, "new");
        File.SetLastWriteTimeUtc(staleArtifact,
            DateTime.UtcNow - CacheMaintenanceService.OcrWorkFileGrace - TimeSpan.FromHours(1));

        await using CacheMaintenanceService maintenance = new(c.Service, storage);
        await maintenance.RunOnceAsync();

        File.Exists(staleTmp).Should().BeFalse("the render-cache sweep runs in the same pass");
        File.Exists(path).Should().BeTrue();
        File.Exists(staleArtifact).Should().BeFalse("ocr-work files older than a day are pruned");
        Directory.Exists(Path.Combine(storage.MinerUWorkDirectory, "run-1")).Should().BeFalse(
            "emptied run directories are pruned deepest-first");
        File.Exists(freshArtifact).Should().BeTrue();
        Directory.Exists(storage.MinerUWorkDirectory).Should().BeTrue("the work root itself stays");
    }

    [Fact]
    public async Task Completed_ocr_run_removes_its_work_artifacts()
    {
        await using OcrContext c = await OcrContext.CreateAsync(_ => false);
        OcrPreset preset = await c.CreatePresetAsync();

        Result<OcrRun> run =
            await c.Coordinator.RunPresetOnDocumentAsync(c.Document.DocumentInstanceId, preset.PresetId);

        run.IsSuccess.Should().BeTrue(run.ErrorMessage);
        run.Value.State.Should().Be(OcrRunState.Completed);
        string workDirectory = Path.Combine(c.WorkRoot, run.Value.OcrRunId.ToString());
        c.Engine.LastWorkDirectory.Should().Be(workDirectory);
        Directory.Exists(workDirectory).Should().BeFalse(
            "a fully successful run no longer needs its run-scoped work directory");
    }

    [Fact]
    public async Task Partially_failed_ocr_run_keeps_its_work_artifacts()
    {
        await using OcrContext c = await OcrContext.CreateAsync(page => page.PageIndex == 0);
        OcrPreset preset = await c.CreatePresetAsync();

        Result<OcrRun> run =
            await c.Coordinator.RunPresetOnDocumentAsync(c.Document.DocumentInstanceId, preset.PresetId);

        run.IsSuccess.Should().BeTrue(run.ErrorMessage);
        run.Value.State.Should().Be(OcrRunState.CompletedWithErrors);
        string workDirectory = Path.Combine(c.WorkRoot, run.Value.OcrRunId.ToString());
        Directory.Exists(workDirectory).Should().BeTrue(
            "failed and partially-failed runs keep their artifacts for diagnostics and retry");
    }

    [Fact]
    public void Render_cache_settings_round_trip_and_clamp()
    {
        using TemporaryAppSettingsFile file = new();
        PatchouliAppSettings settings = PatchouliAppSettings.Load(file.Path);
        settings.RenderCache.DiskLimitBytes.Should().Be(PageRenderService.DefaultDiskCacheByteLimit);
        settings.RenderCache.PngQuality.Should().Be(PageRenderService.DefaultPngQuality);

        PatchouliAppSettings changed = settings with
        {
            RenderCache = new RenderCacheAppSettings(123L * 1024 * 1024, 60)
        };
        changed.Save(file.Path).IsSuccess.Should().BeTrue();

        PatchouliAppSettings reloaded = PatchouliAppSettings.Load(file.Path);
        reloaded.RenderCache.DiskLimitBytes.Should().Be(123L * 1024 * 1024);
        reloaded.RenderCache.PngQuality.Should().Be(60);

        PatchouliAppSettings outOfRange = reloaded with
        {
            RenderCache = new RenderCacheAppSettings(1, 300)
        };
        outOfRange.Save(file.Path).IsSuccess.Should().BeTrue();

        PatchouliAppSettings clamped = PatchouliAppSettings.Load(file.Path);
        clamped.RenderCache.DiskLimitBytes.Should().Be(PageRenderService.MinDiskCacheByteLimit);
        clamped.RenderCache.PngQuality.Should().Be(100);
    }

    [Fact]
    public void Missing_render_cache_section_falls_back_to_defaults()
    {
        string root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"patchouli-rendercache-settings-{Guid.NewGuid():N}")).FullName;
        string path = Path.Combine(root, "settings.json");
        try
        {
            File.WriteAllText(path, """
                                    {
                                      "Ui": {
                                        "PaletteId": "legacy-palette"
                                      }
                                    }
                                    """);

            PatchouliAppSettings settings = PatchouliAppSettings.Load(path);

            settings.RenderCache.Should().Be(RenderCacheAppSettings.Default());
            settings.Ui.PaletteId.Should().Be("legacy-palette");
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryWithRetry(root);
        }
    }

    [Fact]
    public async Task Store_field_level_update_persists_render_cache()
    {
        using TemporaryAppSettingsFile file = new();
        PatchouliAppSettings initial = PatchouliAppSettings.Load(file.Path);
        await using AppSettingsStore store = new(file.Path, initial, TimeSpan.FromMilliseconds(30));

        store.Update(settings => settings with
        {
            RenderCache = new RenderCacheAppSettings(64L * 1024 * 1024, 70)
        }, "RenderCache");

        await Task.Delay(100);
        (await store.FlushAsync()).IsSuccess.Should().BeTrue();

        PatchouliAppSettings reloaded = PatchouliAppSettings.Load(file.Path);
        reloaded.RenderCache.DiskLimitBytes.Should().Be(64L * 1024 * 1024);
        reloaded.RenderCache.PngQuality.Should().Be(70);
    }

    [Fact]
    public async Task Sweep_never_follows_or_deletes_linked_cache_directories()
    {
        await using Context c = await Context.CreateAsync();
        string outside = Path.Combine(c.RootDirectory, "outside-target");
        Directory.CreateDirectory(outside);
        string outsideSentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(outsideSentinel, "must survive");
        File.SetLastWriteTimeUtc(outsideSentinel, DateTime.UtcNow - TimeSpan.FromDays(2));
        string linkedDocDirectory = Path.Combine(c.CacheRoot, DocumentInstanceId.New().ToString());
        if (!TryCreateDirectoryLink(linkedDocDirectory, outside))
        {
            return; // the environment cannot create directory links; no unsafe path was left
        }

        await c.Service.SweepDiskCacheAsync();

        File.Exists(outsideSentinel).Should().BeTrue(
            "the sweep must not delete external files through a linked cache directory");
        Directory.Exists(linkedDocDirectory).Should().BeTrue("the link entry itself stays in place");
    }

    [Fact]
    public async Task Document_cache_clear_skips_a_linked_cache_directory()
    {
        await using Context c = await Context.CreateAsync();
        string outside = Path.Combine(c.RootDirectory, "outside-target");
        Directory.CreateDirectory(outside);
        string outsideSentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(outsideSentinel, "must survive");
        string linkedDocDirectory = Path.Combine(c.CacheRoot, c.DocumentInstanceId.ToString());
        if (!TryCreateDirectoryLink(linkedDocDirectory, outside))
        {
            return;
        }

        Result cleared = await c.Service.ClearRenderCacheForDocumentAsync(c.DocumentInstanceId);

        cleared.IsSuccess.Should().BeTrue();
        File.Exists(outsideSentinel).Should().BeTrue();
        Directory.Exists(linkedDocDirectory).Should().BeTrue("the link entry is not removed either");
    }

    [Fact]
    public async Task Clearing_the_whole_cache_never_deletes_linked_content()
    {
        await using Context c = await Context.CreateAsync();
        string real = await RenderAndPathAsync(c);
        string outside = Path.Combine(c.RootDirectory, "outside-target");
        Directory.CreateDirectory(outside);
        string outsideSentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(outsideSentinel, "must survive");
        string linkedDocDirectory = Path.Combine(c.CacheRoot, DocumentInstanceId.New().ToString());
        if (!TryCreateDirectoryLink(linkedDocDirectory, outside))
        {
            return;
        }

        await c.Service.ClearDiskCacheAsync();

        File.Exists(real).Should().BeFalse("app-created cache files are still cleared");
        File.Exists(outsideSentinel).Should().BeTrue();
        Directory.Exists(linkedDocDirectory).Should().BeTrue();
    }

    [Fact]
    public async Task Maintenance_ocr_work_sweep_never_follows_linked_directories()
    {
        await using Context c = await Context.CreateAsync();
        string root = Path.Combine(c.RootDirectory, "storage");
        OcrStorageLocations storage = StorageAt(root);
        string outside = Path.Combine(c.RootDirectory, "outside-target");
        Directory.CreateDirectory(outside);
        string outsideSentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(outsideSentinel, "must survive");
        File.SetLastWriteTimeUtc(outsideSentinel,
            DateTime.UtcNow - CacheMaintenanceService.OcrWorkFileGrace - TimeSpan.FromHours(1));
        string linkedRunDirectory = Path.Combine(storage.MinerUWorkDirectory, "linked-run");
        Directory.CreateDirectory(storage.MinerUWorkDirectory);
        if (!TryCreateDirectoryLink(linkedRunDirectory, outside))
        {
            return;
        }

        string staleWorkFile = Path.Combine(storage.MinerUWorkDirectory, "stale.txt");
        File.WriteAllText(staleWorkFile, "old");
        File.SetLastWriteTimeUtc(staleWorkFile,
            DateTime.UtcNow - CacheMaintenanceService.OcrWorkFileGrace - TimeSpan.FromHours(1));

        await using CacheMaintenanceService maintenance = new(c.Service, storage);
        await maintenance.RunOnceAsync();

        File.Exists(outsideSentinel).Should().BeTrue(
            "the ocr-work sweep must not reach external files through a link");
        Directory.Exists(linkedRunDirectory).Should().BeTrue("the link entry itself stays in place");
        File.Exists(staleWorkFile).Should().BeFalse("real stale work files are still collected");
    }

    /// <summary>
    /// Creates a directory junction (mklink /J, needs no privilege) or falls back to a
    /// directory symlink when available. Returns false when the platform/user cannot create
    /// either; the caller then returns early instead of failing the test.
    /// </summary>
    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            ProcessStartInfo start = new("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using Process? process = Process.Start(start);
            if (process is not null)
            {
                process.WaitForExit(10000);
                if (process.ExitCode == 0 && Directory.Exists(linkPath) &&
                    (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or
                                              UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Fall through to the symlink attempt.
        }

        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task<string> RenderAndPathAsync(Context c, bool force = false, PageId? page = null,
        int dpi = 200)
    {
        Result<PageRenderResult> result = await c.RenderAsync(force, page, dpi);
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.Status.Should().Be(PageRenderStatus.Rendered);
        string path = result.Value.CacheImagePath!;
        File.Exists(path).Should().BeTrue();
        return path;
    }

    private static async Task<bool> WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        using CancellationTokenSource deadline = new(timeout);
        while (!deadline.IsCancellationRequested)
        {
            if (condition())
            {
                return true;
            }

            try
            {
                await Task.Delay(50, deadline.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return condition();
    }

    private static OcrStorageLocations UnusedStorage()
    {
        string root = Path.Combine(Path.GetTempPath(), "unused");
        return StorageAt(root);
    }

    private static OcrStorageLocations StorageAt(string root)
    {
        return new OcrStorageLocations(
            Path.Combine(root, "models"),
            Path.Combine(root, "models", "ndl-koten"),
            Path.Combine(root, "models", "ndlocr-lite"),
            Path.Combine(root, "models", "rapidocr"),
            Path.Combine(root, "ocr-work", "mineru"),
            Path.Combine(root, "ocr-work", "ndl-koten"),
            Path.Combine(root, "ocr-work", "ndlocr-lite"),
            Path.Combine(root, "ocr-work", "rapidocr"));
    }

    private sealed class Context : IAsyncDisposable
    {
        private Context(TemporarySqliteDatabase db, FixedClock clock, string root, string cache, string pdf,
            ItemId itemId, DocumentInstanceId doc, PageId page, FileAssetId asset, SizedRenderer renderer,
            PageRenderService service, DocumentInstanceService documents, ItemService items,
            ItemPurgeService purge, PageService pages)
        {
            Database = db;
            Clock = clock;
            RootDirectory = root;
            CacheRoot = cache;
            PdfPath = pdf;
            ItemId = itemId;
            DocumentInstanceId = doc;
            PageId = page;
            AssetId = asset;
            Renderer = renderer;
            Service = service;
            Documents = documents;
            Items = items;
            Purge = purge;
            Pages = pages;
        }

        public TemporarySqliteDatabase Database { get; }
        public FixedClock Clock { get; }
        public string RootDirectory { get; }
        public string CacheRoot { get; }
        public string PdfPath { get; }
        public ItemId ItemId { get; }
        public DocumentInstanceId DocumentInstanceId { get; }
        public PageId PageId { get; }
        public PageId? SecondPageId { get; private set; }
        public PageId? ThirdPageId { get; private set; }
        public FileAssetId AssetId { get; }
        public SizedRenderer Renderer { get; }
        public PageRenderService Service { get; }
        public DocumentInstanceService Documents { get; }
        public ItemService Items { get; }
        public ItemPurgeService Purge { get; }
        public PageService Pages { get; }

        public static async Task<Context> CreateAsync(long? diskCacheLimit = null, int? pngQuality = null)
        {
            TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-06-20T00:00:00Z"));
            string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
                $"render-cache-{Guid.NewGuid():N}")).FullName;
            string pdf = Path.Combine(dir, "source.pdf");
            File.Copy(TestFixtures.RealThreePagePdf, pdf);
            string cache = Path.Combine(dir, "cache");
            await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService library = new(db.ConnectionFactory, clock);
            await library.CreateLibraryAsync("Render cache");
            FileAssetService files = new(db.ConnectionFactory, library, clock);
            Result<FileAsset> asset = await files.RegisterFileAsync(pdf);
            ItemService items = new(db.ConnectionFactory, library, clock);
            Result<ItemMetadata> item = await items.CreateItemAsync("book", "Render cache item");
            FileResolutionService resolution = new(db.ConnectionFactory, library, clock);
            SizedRenderer renderer = new();
            PageRenderService service = new(db.ConnectionFactory, library, resolution, renderer, clock, cache,
                new TrackingMaterializer(), diskCacheByteLimit: diskCacheLimit, pngQuality: pngQuality);
            DocumentInstanceService documents = new(db.ConnectionFactory, clock, pageRenders: service);
            Result<DocumentInstance> doc = await documents.AttachDocumentInstanceAsync(
                item.Value.ItemId, asset.Value.FileAssetId, DocumentInstanceType.PrimaryScan);
            PageService pages = new(db.ConnectionFactory, clock);
            Result<Page> page = await pages.CreatePageAsync(doc.Value.DocumentInstanceId, 0, "1", null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "initial", null);
            ItemPurgeService purge = new(db.ConnectionFactory, clock, library, pageRenders: service);
            return new Context(db, clock, dir, cache, pdf, item.Value.ItemId, doc.Value.DocumentInstanceId,
                page.Value.PageId, asset.Value.FileAssetId, renderer, service, documents, items, purge, pages);
        }

        public Task<Result<PageRenderResult>> RenderAsync(bool force = false, PageId? page = null, int dpi = 200)
        {
            return Service.RenderPageAsync(new PageRenderRequest(DocumentInstanceId, page ?? PageId, Dpi: dpi,
                Purpose: PageRenderPurpose.Ocr, ForceRerender: force));
        }

        public async Task AddSecondPageAsync()
        {
            SecondPageId ??= (await Pages.CreatePageAsync(DocumentInstanceId, 1, "2", null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "initial", null)).Value.PageId;
        }

        public async Task AddThirdPageAsync()
        {
            ThirdPageId ??= (await Pages.CreatePageAsync(DocumentInstanceId, 2, "3", null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "initial", null)).Value.PageId;
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            await Database.DisposeAsync();
            DeleteDirectoryTree(RootDirectory);
        }

        /// <summary>
        /// Recursive delete that first unlinks reparse-point directories: removing a junction
        /// through a parent's recursive walk fails on some Windows/.NET combinations, while a
        /// direct non-recursive delete removes the link itself.
        /// </summary>
        private static void DeleteDirectoryTree(string root)
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            Queue<string> pending = new();
            pending.Enqueue(root);
            while (pending.Count > 0)
            {
                string[] children;
                try
                {
                    children = Directory.EnumerateDirectories(pending.Dequeue()).ToArray();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (string child in children)
                {
                    try
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        {
                            Directory.Delete(child);
                        }
                        else
                        {
                            pending.Enqueue(child);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // Cleanup is best-effort.
                    }
                }
            }

            try
            {
                Directory.Delete(root, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Cleanup is best-effort.
            }
        }
    }

    /// <summary>Renderer whose PNG payloads are large enough for byte-limit accounting tests.</summary>
    private sealed class SizedRenderer : IPdfPageRenderer, IPdfPageRendererIdentity
    {
        public int CallCount { get; private set; }
        public int LastPngQuality { get; private set; } = 100;

        public string GetRendererBasisVersion(int dpi)
        {
            return "sized-cache-renderer-v1";
        }

        public async Task<PdfPageRenderOutput> RenderPageToPngAsync(string pdfPath, int pageIndex,
            string outputPath, int dpi, CancellationToken cancellationToken = default, int pngQuality = 100)
        {
            LastPngQuality = pngQuality;
            CallCount++;
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllBytesAsync(outputPath, new byte[1024], cancellationToken);
            return new PdfPageRenderOutput(1000, 1400, 0, CoordinateBasis.NormalizedPage, 1000, 1400,
                GetRendererBasisVersion(dpi));
        }
    }

    private sealed class TrackingMaterializer : IFileMaterializationService
    {
        public Task<Result> EnsureAvailableAsync(string path, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class OcrContext : IAsyncDisposable
    {
        private OcrContext(TemporarySqliteDatabase database, WorkArtifactEngine engine, string workRoot,
            DocumentInstance document, OcrPresetService presets, OcrRunEngine coordinator)
        {
            Database = database;
            Engine = engine;
            WorkRoot = workRoot;
            Document = document;
            Presets = presets;
            Coordinator = coordinator;
        }

        public TemporarySqliteDatabase Database { get; }
        public WorkArtifactEngine Engine { get; }
        public string WorkRoot { get; }
        public DocumentInstance Document { get; }
        public OcrPresetService Presets { get; }
        public OcrRunEngine Coordinator { get; }

        public static async Task<OcrContext> CreateAsync(Func<Page, bool> shouldFail)
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
            string workRoot = Path.Combine(Path.GetTempPath(), $"ocr-work-{Guid.NewGuid():N}");
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
            await libraries.CreateLibraryAsync("OCR work artifacts");
            ItemMetadata item = (await new ItemService(database.ConnectionFactory, libraries, clock)
                .CreateItemAsync("document", "OCR work")).Value;
            DocumentInstance document = (await new DocumentInstanceService(database.ConnectionFactory, clock)
                .AttachDocumentInstanceAsync(item.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
            PageService pages = new(database.ConnectionFactory, clock);
            await pages.CreatePageAsync(document.DocumentInstanceId, 0, "1", null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "test", null);
            await pages.CreatePageAsync(document.DocumentInstanceId, 1, "2", null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "test", null);
            DocumentTreeService trees = BoxTreeTestData.CreateService(database.ConnectionFactory, clock);
            SearchUnitBuilder search = new(database.ConnectionFactory, clock, new MarkdigMarkdownEngine());
            WorkArtifactEngine engine = new(workRoot, async () =>
            {
                await using SqliteConnection connection = database.ConnectionFactory.CreateConnection();
                await connection.OpenAsync();
                return await connection.ExecuteScalarAsync<string?>(
                    "select ocr_run_id from ocr_runs where state='running' limit 1;");
            }, shouldFail);
            OcrRunEngine coordinator = new(
                database.ConnectionFactory,
                clock,
                engine,
                search,
                new OcrDocumentTreeImporter(trees),
                minerUCacheRoot: workRoot);
            return new OcrContext(database, engine, workRoot, document,
                new OcrPresetService(database.ConnectionFactory, libraries, clock), coordinator);
        }

        public async Task<OcrPreset> CreatePresetAsync()
        {
            return (await Presets.CreatePresetAsync("Work artifacts", null, OcrEngineIds.Mock,
                OcrModelIds.MockBasic, null, "{}", false)).Value;
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            TestTempFileCleanup.DeleteDirectoryWithRetry(WorkRoot);
        }
    }

    /// <summary>
    /// Mock engine that drops run-scoped artifacts under the MinerU work root while the run is
    /// still 'running' — the shape the run-completion cleanup must collect.
    /// </summary>
    private sealed class WorkArtifactEngine(
        string workRoot,
        Func<Task<string?>> runningRunId,
        Func<Page, bool> shouldFail) : IOcrEngine
    {
        public string EngineId => OcrEngineIds.Mock;
        public string? LastWorkDirectory { get; private set; }

        public async Task<OcrEnginePageResult> RunPageAsync(Page page, OcrPresetVersion presetVersion,
            CancellationToken cancellationToken)
        {
            string? runId = await runningRunId();
            runId.Should().NotBeNullOrWhiteSpace("the engine runs while the run row is 'running'");
            LastWorkDirectory = Path.Combine(workRoot, runId);
            Directory.CreateDirectory(LastWorkDirectory);
            await File.WriteAllTextAsync(Path.Combine(LastWorkDirectory, $"p{page.PageIndex}.txt"), "work",
                cancellationToken);
            return shouldFail(page)
                ? new OcrEnginePageResult(page.PageId, false, null, null, OcrFailureCode.LocalOcrProcessFailed,
                    "injected failure")
                : new OcrEnginePageResult(page.PageId, true, "text", null, null, null);
        }
    }
}
