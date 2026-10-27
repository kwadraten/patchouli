using System.Collections.Specialized;
using FluentAssertions;
using Patchouli.UI.ViewModels.Core;

namespace Patchouli.Tests;

public sealed class SnapshotObservableCollectionTests
{
    [Fact]
    public void Snapshot_replacement_emits_one_reset_and_retains_normal_incremental_mutations()
    {
        SnapshotObservableCollection<int> values = [1, 2];
        List<NotifyCollectionChangedAction> changes = [];
        values.CollectionChanged += (_, args) => changes.Add(args.Action);

        values.ReplaceWith([3, 4, 5]);

        values.Should().Equal(3, 4, 5);
        changes.Should().Equal(NotifyCollectionChangedAction.Reset);
        values.Add(6);
        changes.Should().Equal(NotifyCollectionChangedAction.Reset, NotifyCollectionChangedAction.Add);
        values.ReplaceWith(values);
        values.Should().Equal(3, 4, 5, 6);
        values.ReplaceWith([]);
        changes.Clear();
        values.ReplaceWith([]);
        changes.Should().BeEmpty();
    }
}
