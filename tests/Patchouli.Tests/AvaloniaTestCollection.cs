using Avalonia.Headless;
using Patchouli.UI;

namespace Patchouli.Tests;

[CollectionDefinition("Avalonia", DisableParallelization = true)]
public sealed class AvaloniaTestCollection;

[CollectionDefinition("RetainedTabUI", DisableParallelization = true)]
public sealed class RetainedTabUITestCollection : ICollectionFixture<AvaloniaSharedSessionFixture>;

public sealed class AvaloniaSharedSessionFixture : IDisposable
{
    public HeadlessUnitTestSession Session { get; } = HeadlessUnitTestSession.StartNew(typeof(App));

    public void Dispose()
    {
        Session.Dispose();
    }
}
