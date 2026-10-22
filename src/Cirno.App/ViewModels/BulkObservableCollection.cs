using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Cirno.App.ViewModels;

/// <summary>
/// 支持批量追加的可观察集合。
/// <para>
/// 一次会话动辄上万条历史消息。若逐条 <see cref="ObservableCollection{T}.Add"/>，
/// 绑定列表会收到上万次 CollectionChanged，每次都可能触发一次布局/重排，
/// 还原耗时从毫秒级劣化到秒级。
/// </para>
/// <para>
/// 这里把批量追加压成「一次 Add 通知 + 一次 Reset 通知」：
/// 先把新元素挂进底层集合，再发一次 Reset，绑定方只需整体重建一次。
/// </para>
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>批量追加：先落地元素，再发一次 Reset，避免逐条通知。</summary>
    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var added = false;
        foreach (var item in items)
        {
            Items.Add(item);
            added = true;
        }

        if (added)
        {
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
