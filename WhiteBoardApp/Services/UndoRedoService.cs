using System.Collections.ObjectModel;
using WhiteBoard.Models;

namespace WhiteBoard.Services;

/// <summary>一次可撤销的操作。</summary>
public interface IUndoableAction
{
    /// <summary>显示在撤销提示里的名称，例如“书写”“擦除”。</summary>
    string Name { get; }

    void Undo();

    void Redo();
}

/// <summary>新增对象。</summary>
public sealed class AddItemsAction : IUndoableAction
{
    private readonly ObservableCollection<WhiteboardItem> _target;
    private readonly IReadOnlyList<WhiteboardItem> _items;

    public AddItemsAction(ObservableCollection<WhiteboardItem> target, IEnumerable<WhiteboardItem> items, string name = "书写")
    {
        _target = target;
        _items = items.ToList();
        Name = name;
    }

    public string Name { get; }

    public void Undo()
    {
        foreach (var item in _items)
            _target.Remove(item);
    }

    public void Redo()
    {
        foreach (var item in _items)
        {
            if (!_target.Contains(item))
                _target.Add(item);
        }
    }
}

/// <summary>删除对象（撤销时按原顺序插回）。</summary>
public sealed class RemoveItemsAction : IUndoableAction
{
    private readonly ObservableCollection<WhiteboardItem> _target;
    private readonly List<(WhiteboardItem Item, int Index)> _entries = new();

    public RemoveItemsAction(ObservableCollection<WhiteboardItem> target, IEnumerable<WhiteboardItem> items, string name = "擦除")
    {
        _target = target;
        Name = name;

        foreach (var item in items)
        {
            var index = target.IndexOf(item);
            _entries.Add((item, index < 0 ? int.MaxValue : index));
        }

        _entries.Sort((a, b) => a.Index.CompareTo(b.Index));
    }

    public string Name { get; }

    public void Undo()
    {
        foreach (var (item, index) in _entries)
        {
            if (_target.Contains(item))
                continue;

            var at = index == int.MaxValue || index > _target.Count ? _target.Count : index;
            _target.Insert(at, item);
        }
    }

    public void Redo()
    {
        foreach (var (item, _) in _entries)
            _target.Remove(item);
    }
}

/// <summary>把一组删除 + 一组新增合成一步（像素橡皮用）。</summary>
public sealed class ReplaceItemsAction : IUndoableAction
{
    private readonly RemoveItemsAction _remove;
    private readonly AddItemsAction _add;

    public ReplaceItemsAction(
        ObservableCollection<WhiteboardItem> target,
        IEnumerable<WhiteboardItem> removed,
        IEnumerable<WhiteboardItem> added,
        string name = "擦除")
    {
        Name = name;
        _remove = new RemoveItemsAction(target, removed, name);
        _add = new AddItemsAction(target, added, name);
    }

    public string Name { get; }

    public void Undo()
    {
        _add.Undo();
        _remove.Undo();
    }

    public void Redo()
    {
        _remove.Redo();
        _add.Redo();
    }
}

/// <summary>移动对象：记录每个对象移动前后的状态。</summary>
public sealed class MoveItemsAction : IUndoableAction
{
    private readonly List<(WhiteboardItem Item, WhiteboardItem Before, WhiteboardItem After)> _entries = new();

    public MoveItemsAction(IEnumerable<(WhiteboardItem Item, WhiteboardItem Before, WhiteboardItem After)> entries)
    {
        _entries.AddRange(entries);
        Name = "移动对象";
    }

    public string Name { get; }

    public void Undo()
    {
        foreach (var (item, before, _) in _entries)
            CopyState(before, item);
    }

    public void Redo()
    {
        foreach (var (item, _, after) in _entries)
            CopyState(after, item);
    }

    /// <summary>把 <paramref name="from"/> 的几何信息写回 <paramref name="to"/>。</summary>
    private static void CopyState(WhiteboardItem from, WhiteboardItem to)
    {
        switch (from, to)
        {
            case (StrokeItem src, StrokeItem dst):
                dst.Points.Clear();
                dst.Points.AddRange(src.Points.Select(p => p.Clone()));
                break;

            case (ShapeItem src, ShapeItem dst):
                dst.Start = src.Start;
                dst.End = src.End;
                break;

            case (TextItem src, TextItem dst):
                dst.Position = src.Position;
                break;
        }
    }
}

/// <summary>
/// 撤销 / 重做栈。按“页”记录操作，切换页面时各自独立。
/// </summary>
public sealed class UndoRedoService
{
    private readonly Stack<IUndoableAction> _undo = new();
    private readonly Stack<IUndoableAction> _redo = new();

    /// <summary>栈内容发生变化（用于刷新按钮可用状态）。</summary>
    public event EventHandler? Changed;

    /// <summary>历史记录条数上限，防止内存无限增长。</summary>
    public int Capacity { get; set; } = 200;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public int UndoCount => _undo.Count;

    public int Count => _undo.Count;

    public string? NextUndoName => _undo.Count > 0 ? _undo.Peek().Name : null;

    public string? NextRedoName => _redo.Count > 0 ? _redo.Peek().Name : null;

    /// <summary>执行一个已经生效的操作并压栈（调用方负责先改数据）。</summary>
    public void Push(IUndoableAction action)
    {
        _undo.Push(action);
        _redo.Clear();
        Trim();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo()
    {
        if (_undo.Count == 0)
            return false;

        var action = _undo.Pop();
        action.Undo();
        _redo.Push(action);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
            return false;

        var action = _redo.Pop();
        action.Redo();
        _undo.Push(action);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Trim()
    {
        if (_undo.Count <= Capacity)
            return;

        // Stack 不支持从底部弹出，超出容量时整体重建
        var items = _undo.ToArray().Take(Capacity).Reverse().ToArray();
        _undo.Clear();
        foreach (var item in items)
            _undo.Push(item);
    }
}
