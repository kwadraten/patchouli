using Avalonia.Headless;

namespace Patchouli.Tests;

[CollectionDefinition("Avalonia", DisableParallelization = true)]
public sealed class AvaloniaTestCollection;

[CollectionDefinition("RetainedTabUI", DisableParallelization = true)]
public sealed class RetainedTabUITestCollection : ICollectionFixture<AvaloniaSharedSessionFixture>;

public sealed class AvaloniaSharedSessionFixture : IDisposable
{
    // Markdown text decorations are static AvaloniaObjects: all Markdown tests must use this thread.
    // Per-test application isolation is retained by each Dispatch call.
    public HeadlessUnitTestSession Session { get; } = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));

    public void Dispose()
    {
        Session.Dispose();
    }
}
