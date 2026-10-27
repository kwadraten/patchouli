using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Patchouli.UI.ViewModels.Core;

/// <summary>Publishes an already-built UI snapshot once, rather than realizing partial lists per row.</summary>
internal sealed class SnapshotObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceWith(IEnumerable<T> replacement)
    {
        T[] values = replacement.ToArray();
        if (Count == 0 && values.Length == 0)
        {
            return;
        }

        CheckReentrancy();
        Items.Clear();
        foreach (T value in values)
        {
            Items.Add(value);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
