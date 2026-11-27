using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace EnviousWispr.App.Collections;

/// <summary>
/// Observable collection supporting single-notification bulk replacement and minimal surgical
/// reconciliation for small mutations so virtualized ListView controls preserve scroll position
/// and avoid per-item layout cascades.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        CheckReentrancy();

        Items.Clear();
        for (var i = 0; i < items.Count; i++)
        {
            Items.Add(items[i]);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void Reconcile(IReadOnlyList<T> items, Func<T, T, bool> areSameRow)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(areSameRow);

        if (Count == 0 || items.Count == 0)
        {
            ReplaceAll(items);
            return;
        }

        // Prefix/suffix match to detect single add/edit/remove without resetting scroll offset.
        var prefix = 0;
        var minCount = Math.Min(Count, items.Count);
        while (prefix < minCount && areSameRow(Items[prefix], items[prefix]))
        {
            prefix++;
        }

        var oldSuffix = Count - 1;
        var newSuffix = items.Count - 1;
        while (oldSuffix >= prefix && newSuffix >= prefix && areSameRow(Items[oldSuffix], items[newSuffix]))
        {
            oldSuffix--;
            newSuffix--;
        }

        var removed = oldSuffix - prefix + 1;
        var inserted = newSuffix - prefix + 1;

        if (removed == 0 && inserted == 0)
        {
            return;
        }

        // When only a tiny window around one edit changed, mutate in place to preserve realization and scroll.
        if (removed <= 4 && inserted <= 4)
        {
            var overlap = Math.Min(removed, inserted);
            for (var i = 0; i < overlap; i++)
            {
                this[prefix + i] = items[prefix + i];
            }

            for (var i = removed - 1; i >= overlap; i--)
            {
                RemoveAt(prefix + i);
            }

            for (var i = overlap; i < inserted; i++)
            {
                Insert(prefix + i, items[prefix + i]);
            }

            return;
        }

        ReplaceAll(items);
    }
}
