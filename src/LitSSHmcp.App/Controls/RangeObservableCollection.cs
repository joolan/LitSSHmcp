using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace LitSSHmcp.App.Controls;

/// <summary>
/// 支持一次性替换/追加的 ObservableCollection：只触发一次 Reset，
/// 避免大数据量列表逐项 Add 造成大量 CollectionChanged 与布局刷新。
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceRange(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);

        RaiseReset();
    }

    public void AddRange(IEnumerable<T> items)
    {
        var added = items as IList<T> ?? items.ToList();
        if (added.Count == 0)
            return;

        foreach (var item in added)
            Items.Add(item);

        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
