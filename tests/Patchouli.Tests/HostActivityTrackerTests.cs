using FluentAssertions;
using Patchouli.Core.Diagnostics;

namespace Patchouli.Tests;

public sealed class HostActivityTrackerTests
{
    [Fact]
    public void BeginScope_publishes_busy_synchronously()
    {
        using HostActivityTracker tracker = new();
        bool observedBusy = false;
        tracker.Changed += (_, snapshot) => observedBusy |= snapshot.IsBusy;

        using IActivityScope scope = tracker.BeginScope("OCR", HostActivityKind.Ocr);

        observedBusy.Should().BeTrue();
        tracker.Current.IsBusy.Should().BeTrue();
    }

    [Fact]
    public void Correlated_scopes_share_one_item_until_the_last_scope_ends()
    {
        using HostActivityTracker tracker = new();
        IActivityScope first = tracker.BeginScope("Search", HostActivityKind.UiCommand, correlationId: "search");
        IActivityScope second = tracker.BeginScope("Search", HostActivityKind.UiCommand, correlationId: "search");

        tracker.Current.Items.Should().ContainSingle();
        first.Dispose();
        tracker.Current.IsBusy.Should().BeTrue();
        tracker.Current.Items.Should().ContainSingle();

        second.Dispose();
        tracker.Current.IsBusy.Should().BeFalse();
        tracker.Current.Items.Should().BeEmpty();
    }

    [Fact]
    public void Pause_and_retry_reasons_do_not_replace_activity_detail()
    {
        using HostActivityTracker tracker = new();
        using IActivityScope scope = tracker.BeginScope("OCR", HostActivityKind.Ocr, "page 4/10");

        scope.SetPaused(true, "等待用户");
        tracker.Current.IsSleeping.Should().BeTrue();
        tracker.Current.SleepReason.Should().Contain("等待用户");
        tracker.Current.Items.Single().Detail.Should().Be("page 4/10");

        scope.SetPaused(false);
        scope.SetWaitingRetry(true, "30 秒后重试");
        tracker.Current.IsSleeping.Should().BeTrue();
        tracker.Current.SleepReason.Should().Contain("30 秒后重试");
        tracker.Current.Items.Single().Detail.Should().Be("page 4/10");
    }

    [Fact]
    public async Task Detail_bursts_are_coalesced()
    {
        using HostActivityTracker tracker = new(TimeSpan.FromMilliseconds(40));
        List<HostActivitySnapshot> snapshots = [];
        tracker.Changed += (_, snapshot) => snapshots.Add(snapshot);
        using IActivityScope scope = tracker.BeginScope("Index", HostActivityKind.Indexing);

        for (int i = 0; i < 20; i++)
        {
            scope.UpdateDetail($"{i}");
        }

        await Task.Delay(80);
        await tracker.FlushAsync();

        snapshots.Should().HaveCount(2);
        snapshots[^1].Items.Single().Detail.Should().Be("19");
    }

    [Fact]
    public void Throwing_observer_does_not_block_other_observers()
    {
        using HostActivityTracker tracker = new();
        ThrowingObserver throwing = new();
        RecordingObserver recording = new();
        using IDisposable first = tracker.SnapshotStream.Subscribe(throwing);
        using IDisposable second = tracker.SnapshotStream.Subscribe(recording);

        using IActivityScope scope = tracker.BeginScope("Import", HostActivityKind.Import);

        recording.Values.Should().Contain(snapshot => snapshot.IsBusy);
    }

    [Fact]
    public void Subscribing_after_dispose_completes_immediately()
    {
        HostActivityTracker tracker = new();
        tracker.Dispose();
        RecordingObserver observer = new();

        using IDisposable subscription = tracker.SnapshotStream.Subscribe(observer);

        observer.Completed.Should().BeTrue();
    }

    private sealed class ThrowingObserver : IObserver<HostActivitySnapshot>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(HostActivitySnapshot value)
        {
            throw new InvalidOperationException("observer failure");
        }
    }

    private sealed class RecordingObserver : IObserver<HostActivitySnapshot>
    {
        public List<HostActivitySnapshot> Values { get; } = [];
        public bool Completed { get; private set; }

        public void OnCompleted()
        {
            Completed = true;
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(HostActivitySnapshot value)
        {
            Values.Add(value);
        }
    }
}
