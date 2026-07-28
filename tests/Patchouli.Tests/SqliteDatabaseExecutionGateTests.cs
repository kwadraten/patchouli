using FluentAssertions;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Tests;

public sealed class SqliteDatabaseExecutionGateTests
{
    [Fact]
    public async Task EnterExclusiveAsync_serializes_separate_factories_for_the_same_database()
    {
        string path = Path.Combine(Path.GetTempPath(), $"patchouli-gate-{Guid.NewGuid():N}.sqlite");
        SqliteConnectionFactory firstFactory = new(path);
        SqliteConnectionFactory secondFactory = new(path);

        using IDisposable first = await firstFactory.EnterExclusiveAsync();
        Task<IDisposable> waiting = secondFactory.EnterExclusiveAsync();

        await Task.Delay(50);
        waiting.IsCompleted.Should().BeFalse();

        first.Dispose();
        using IDisposable second = await waiting.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
