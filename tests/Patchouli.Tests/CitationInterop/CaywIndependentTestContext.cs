using System.Diagnostics;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Csl;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Host.CitationInterop;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Bibliography.Biblatex;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests.CitationInterop;

/// <summary>
/// Independent fixture for the issue-14 verification pass (not written by the
/// implementer): same real migrated SQLite library and real services as the
/// production composition, but with a settable clock so creation order — and
/// therefore the query service's created_at-desc result order — is fully
/// deterministic.
/// </summary>
internal sealed class CaywIndependentTestContext : IAsyncDisposable
{
    private CaywIndependentTestContext(
        TemporarySqliteDatabase database,
        FixedClock clock,
        ItemService items,
        LibraryItemQueryService query,
        CitationInteropService interop)
    {
        Database = database;
        Clock = clock;
        Items = items;
        Query = query;
        Interop = interop;
    }

    public TemporarySqliteDatabase Database { get; }

    public FixedClock Clock { get; }

    public ItemService Items { get; }

    public LibraryItemQueryService Query { get; }

    public CitationInteropService Interop { get; }

    public static async Task<CaywIndependentTestContext> CreateAsync()
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService library = new(database.ConnectionFactory, clock);
        await library.CreateLibraryAsync("Interop Independent Tests");
        ItemService items = new(database.ConnectionFactory, library, clock);
        LibraryItemQueryService query = new(database.ConnectionFactory);
        CslStyleStore store = new(database.ConnectionFactory, clock);
        await store.InstallStyleAsync(
            new CslCatalogStyle("probe", "Probe", "https://example.test/probe.csl", "test"),
            InteropTestContext.ProbeStyleXml());
        await store.InstallStyleAsync(
            new CslCatalogStyle("citation-only", "Citation Only", "https://example.test/citation-only.csl", "test"),
            InteropTestContext.CitationOnlyStyleXml());
        await store.SaveSettingsAsync("probe", "en-US");
        CslItemMapper mapper = new();
        CslRenderer renderer = new(items, store, mapper);
        BiblatexImportService biblatex = new(new FakeBiblatexHelperClient(), items, null!, null!);
        CitationInteropService interop = new(query, items, mapper, renderer, biblatex);
        return new CaywIndependentTestContext(database, clock, items, query, interop);
    }

    /// <summary>Creates one item at the given clock instant, so tests control
    /// the created_at ordering across items exactly.</summary>
    public async Task<ItemMetadata> CreateItemAtAsync(DateTimeOffset createdAt, CreateItemRequest request)
    {
        Clock.UtcNow = createdAt;
        Result<ItemMetadata> created = await Items.CreateItemAsync(request);
        if (created.IsFailure)
        {
            throw new InvalidOperationException($"CreateItem failed: {created.ErrorCode} {created.ErrorMessage}");
        }

        return created.Value;
    }

    public async Task<ItemMetadata> CreateItemAsync(CreateItemRequest request)
    {
        Result<ItemMetadata> created = await Items.CreateItemAsync(request);
        if (created.IsFailure)
        {
            throw new InvalidOperationException($"CreateItem failed: {created.ErrorCode} {created.ErrorMessage}");
        }

        return created.Value;
    }

    public ValueTask DisposeAsync()
    {
        return Database.DisposeAsync();
    }
}

/// <summary>
/// A desktop bridge that counts every interaction, so tests can prove a code
/// path did NOT touch the UI (probe, selected) or touched it exactly once
/// (side effects). Pick behavior is scripted per test, typically gated on
/// TaskCompletionSource barriers — never on sleeps.
/// </summary>
internal sealed class CountingCitationBridge : ICitationDesktopBridge
{
    public bool Available { get; set; } = true;

    public Func<CancellationToken, Task<CitationPickBridgeResult>>? OnPick { get; set; }

    public IReadOnlyList<ItemId> Selected { get; set; } = [];

    public int PickCalls;
    public int SnapshotCalls;
    public int ClipboardCalls;
    public int SelectCalls;
    public int MinimizeCalls;
    public int ActivateCalls;

    public List<string> ClipboardTexts { get; } = new();

    public IReadOnlyList<ItemId>? LastSelectedViaBridge { get; private set; }

    public bool IsInteractiveAvailable => Available;

    public Task<CitationPickBridgeResult> PickCitationsAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref PickCalls);
        return OnPick?.Invoke(cancellationToken)
               ?? Task.FromResult(CitationPickBridgeResult.CancelledResult);
    }

    public Task<IReadOnlyList<ItemId>> GetSelectedItemIdsAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref SnapshotCalls);
        return Task.FromResult(Selected);
    }

    public Task SetClipboardTextAsync(string text, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref ClipboardCalls);
        ClipboardTexts.Add(text);
        return Task.CompletedTask;
    }

    public Task SelectItemsAsync(IReadOnlyList<ItemId> itemIds, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref SelectCalls);
        LastSelectedViaBridge = itemIds;
        return Task.CompletedTask;
    }

    public Task MinimizeMainWindowAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref MinimizeCalls);
        return Task.CompletedTask;
    }

    public Task ActivateMainWindowAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref ActivateCalls);
        return Task.CompletedTask;
    }

    public void AssertNoBridgeCalls()
    {
        (PickCalls + SnapshotCalls + ClipboardCalls + SelectCalls + MinimizeCalls + ActivateCalls)
            .Should().Be(0, "this code path must not touch the desktop bridge at all");
    }
}

internal static class CaywIndependentQueries
{
    public static Dictionary<string, string> Query(params (string Key, string Value)[] pairs)
    {
        return pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed record LoopbackHttpResponse(int StatusCode, string Body, string? ContentType, int ExitCode);

/// <summary>
/// Loopback HTTP client for the transport tests, driven through curl.
/// Rationale: this sandbox denies direct IPv4 TCP egress from .NET processes
/// (verified empirically — an in-process HttpClient receives the sandbox
/// policy text instead of the server response, while curl and IPv6 loopback
/// connect normally), so an HttpClient-based test cannot reach the IPv4-only
/// listener here at all. curl is a real local client of exactly the kind the
/// service exists for, and it behaves identically on a normal dev machine.
/// </summary>
internal static class LoopbackHttp
{
    public static async Task<LoopbackHttpResponse> SendAsync(
        string method,
        string url,
        string? body = null,
        params (string Name, string Value)[] headers)
    {
        using Process process = StartCurl(method, url, body, headers, out string bodyFile);
        if (body is not null)
        {
            try
            {
                await process.StandardInput.WriteAsync(body);
            }
            catch (IOException)
            {
                // The server may reject an oversized body and close the
                // connection while curl is still sending; the response file
                // still captures the verdict.
            }
        }

        process.StandardInput.Close();
        string meta = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return Parse(meta, bodyFile, process.ExitCode);
    }

    /// <summary>Starts a request without waiting for it (client-disconnect tests).</summary>
    public static Process StartRequest(string method, string url, out string bodyFile)
    {
        return StartCurl(method, url, null, [], out bodyFile);
    }

    public static LoopbackHttpResponse ReadResult(Process process, string bodyFile)
    {
        string meta = process.StandardOutput.ReadToEnd();
        return Parse(meta, bodyFile, process.ExitCode);
    }

    private static Process StartCurl(
        string method,
        string url,
        string? body,
        (string Name, string Value)[] headers,
        out string bodyFile)
    {
        bodyFile = Path.GetTempFileName();
        ProcessStartInfo startInfo = new("curl")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-sS");
        startInfo.ArgumentList.Add("-X");
        startInfo.ArgumentList.Add(method);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(bodyFile);
        startInfo.ArgumentList.Add("-w");
        startInfo.ArgumentList.Add("%{http_code} %{content_type}");
        if (body is not null)
        {
            startInfo.ArgumentList.Add("--data-binary");
            startInfo.ArgumentList.Add("@-");
        }

        foreach ((string name, string value) in headers)
        {
            startInfo.ArgumentList.Add("-H");
            startInfo.ArgumentList.Add($"{name}: {value}");
        }

        startInfo.ArgumentList.Add(url);
        return Process.Start(startInfo)!;
    }

    private static LoopbackHttpResponse Parse(string meta, string bodyFile, int exitCode)
    {
        string body = File.Exists(bodyFile) ? File.ReadAllText(bodyFile) : "";
        string[] parts = meta.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        int status = parts.Length > 0 && int.TryParse(parts[0], out int parsed) ? parsed : 0;
        string? contentType = parts.Length > 1 ? parts[1] : null;
        return new LoopbackHttpResponse(status, body, contentType, exitCode);
    }
}
