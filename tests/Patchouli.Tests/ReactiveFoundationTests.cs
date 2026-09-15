using System.Reactive.Concurrency;
using System.Reactive;
using System.Reactive.Subjects;
using FluentAssertions;
using Microsoft.Reactive.Testing;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Core;
using Patchouli.UI.Diagnostics;

namespace Patchouli.Tests;

public sealed class ReactiveFoundationTests
{
    [Fact]
    public void Latest_flow_throttles_requests_and_switches_away_from_stale_work()
    {
        TestScheduler scheduler = new();
        Subject<Unit> requests = new();
        List<CancellationToken> operationTokens = [];
        List<TaskCompletionSource> completions = [];
        List<Exception> errors = [];
        TimeSpan throttle = TimeSpan.FromMilliseconds(200);
        using IDisposable subscription = ReactiveUiFlow.SubscribeLatest(
            requests,
            throttle,
            scheduler,
            scheduler,
            cancellationToken =>
            {
                operationTokens.Add(cancellationToken);
                TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                completions.Add(completion);
                return completion.Task;
            },
            errors.Add);

        requests.OnNext(Unit.Default);
        scheduler.AdvanceBy(throttle.Ticks + 10);
        operationTokens.Should().ContainSingle();

        requests.OnNext(Unit.Default);
        scheduler.AdvanceBy(throttle.Ticks + 10);

        operationTokens.Should().HaveCount(2);
        operationTokens[0].IsCancellationRequested.Should().BeTrue();
        operationTokens[1].IsCancellationRequested.Should().BeFalse();
        errors.Should().BeEmpty();
        completions[1].TrySetResult();
    }

    [Fact]
    public void Buffered_flow_coalesces_each_window_without_dropping_values()
    {
        TestScheduler scheduler = new();
        Subject<int> changes = new();
        List<int[]> batches = [];
        List<Exception> errors = [];
        TimeSpan window = TimeSpan.FromMilliseconds(20);
        using IDisposable subscription = ReactiveUiFlow.SubscribeBufferedSequential(
            changes,
            window,
            scheduler,
            scheduler,
            (batch, _) =>
            {
                batches.Add(batch.ToArray());
                return Task.CompletedTask;
            },
            errors.Add);

        changes.OnNext(1);
        changes.OnNext(2);
        scheduler.AdvanceBy(window.Ticks + 10);
        changes.OnNext(3);
        scheduler.AdvanceBy(window.Ticks + 10);

        batches.Should().HaveCount(2);
        batches[0].Should().Equal(1, 2);
        batches[1].Should().Equal(3);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Change_set_batch_unions_every_resource_and_keeps_the_latest_revision()
    {
        ItemId itemA = ItemId.New();
        ItemId itemB = ItemId.New();
        DocumentInstanceId document = DocumentInstanceId.New();
        CollectionId collection = CollectionId.New();
        LibraryChangeSet first = new(4, [itemA], [document], ["apa"], [], [], []);
        LibraryChangeSet second = new(7, [itemA, itemB], [], ["apa", "mla"], [], [], [collection]);

        LibraryChangeSet combined = LibraryShellViewModel.MergeChangeSets([first, second]);

        combined.NewRevision.Should().Be(7);
        combined.ItemIds.Should().BeEquivalentTo(new[] { itemA, itemB });
        combined.DocumentInstanceIds.Should().Equal(document);
        combined.StyleIds.Should().Equal("apa", "mla");
        combined.CollectionIds.Should().Equal(collection);
    }

    [Fact]
    public void Workspace_layout_automatically_invalidates_properties_derived_from_active_tab()
    {
        WorkspaceLayoutViewModel layout = new(ImmediateScheduler.Instance);
        List<string?> changes = [];
        layout.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        layout.ActiveTab = CreateTab(WorkspaceTabKind.Library);

        changes.Should().Contain(
        [
            nameof(WorkspaceLayoutViewModel.ActiveTab),
            nameof(WorkspaceLayoutViewModel.ShowSidebar),
            nameof(WorkspaceLayoutViewModel.IsInspectorVisible),
            nameof(WorkspaceLayoutViewModel.IsLibraryActive),
            nameof(WorkspaceLayoutViewModel.IsReaderActive),
            nameof(WorkspaceLayoutViewModel.IsSettingsActive),
            nameof(WorkspaceLayoutViewModel.IsItemEditorActive)
        ]);
    }

    [Fact]
    public void Workspace_layout_automatically_invalidates_inspector_visibility()
    {
        WorkspaceLayoutViewModel layout = new(ImmediateScheduler.Instance)
            { ActiveTab = CreateTab(WorkspaceTabKind.Library) };
        List<string?> changes = [];
        layout.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        layout.ShowInspectorPane = false;

        changes.Should().Contain(nameof(WorkspaceLayoutViewModel.ShowInspectorPane));
        changes.Should().Contain(nameof(WorkspaceLayoutViewModel.IsInspectorVisible));
    }

    [Fact]
    public void Workspace_layout_keeps_collection_driven_dependencies_explicit()
    {
        WorkspaceLayoutViewModel layout = new(ImmediateScheduler.Instance);
        List<string?> changes = [];
        layout.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        layout.Tabs.Add(CreateTab(WorkspaceTabKind.PdfWorkspace));

        changes.Should().ContainSingle(c => c == nameof(WorkspaceLayoutViewModel.HasPdfWorkspaceTab));
    }

    [Fact]
    public void Toolkit_observable_property_notifies_only_when_the_value_changes()
    {
        WorkspaceTabViewModel tab = CreateTab(WorkspaceTabKind.Library);
        List<string?> changes = [];
        tab.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        tab.Title = tab.Title;
        tab.Title = "Renamed";

        changes.Should().Equal(nameof(WorkspaceTabViewModel.Title));
    }

    [Fact]
    public void BindOutput_starts_with_initial_value_and_distincts_until_changed()
    {
        Subject<string> subject = new();
        EmptyViewModel owner = new();
        List<string> outputs = [];
        List<Exception> errors = [];
        TestScheduler scheduler = new();

        subject.BindOutput(owner, outputs.Add, scheduler, errors.Add, true, "init");
        scheduler.AdvanceBy(1); // observe on

        outputs.Should().Equal("init");

        subject.OnNext("init");
        scheduler.AdvanceBy(1);
        outputs.Should().Equal("init"); // distinct

        subject.OnNext("second");
        scheduler.AdvanceBy(1);
        outputs.Should().Equal("init", "second");
    }

    [Fact]
    public void BindOutput_ties_to_owner_disposal()
    {
        Subject<string> subject = new();
        EmptyViewModel owner = new();
        List<string> outputs = [];
        List<Exception> errors = [];
        TestScheduler scheduler = new();

        subject.BindOutput(owner, outputs.Add, scheduler, errors.Add);

        subject.OnNext("first");
        scheduler.AdvanceBy(1);
        outputs.Should().Equal("first");

        owner.Dispose();

        subject.OnNext("second");
        scheduler.AdvanceBy(1);
        outputs.Should().Equal("first"); // no new emissions
    }

    [Fact]
    public void BindOutput_routes_errors_to_sink_when_reporter_is_null()
    {
        Subject<string> subject = new();
        EmptyViewModel owner = new();
        List<Exception> logged = [];
        IUnexpectedExceptionSink originalSink = UnexpectedExceptions.Sink;
        UnexpectedExceptions.Sink = new RecordingUnexpectedExceptionSink((ex, b, op) => logged.Add(ex));
        try
        {
            TestScheduler scheduler = new();

            subject.BindOutput(owner, _ => throw new InvalidOperationException("rx fail"), scheduler, null);

            subject.OnNext("test");
            scheduler.AdvanceBy(1);

            logged.Should().ContainSingle().Which.Message.Should().Be("rx fail");
        }
        finally
        {
            UnexpectedExceptions.Sink = originalSink;
        }
    }

    private static WorkspaceTabViewModel CreateTab(WorkspaceTabKind kind)
    {
        return new WorkspaceTabViewModel(kind, kind.ToString(), kind.ToString(), "File", true, null,
            new EmptyViewModel());
    }

    private sealed class EmptyViewModel : ViewModelBase;
}
