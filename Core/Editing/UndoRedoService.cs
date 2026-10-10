namespace ManualVideoStudio.Core.Editing;

/// <summary>
/// スナップショット（Memento）方式の Undo / Redo。操作の直前状態を Record し、Undo で現在状態と入れ替える。
/// 状態 T は不変として扱う（呼び出し側で複製済みのものを渡す）。
/// </summary>
public sealed class UndoRedoService<T>
{
    private readonly LinkedList<(string Label, T State)> _undo = new();
    private readonly Stack<(string Label, T State)> _redo = new();

    public UndoRedoService(int capacity = 200)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
    }

    public int Capacity { get; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _undo.Last?.Value.Label;
    public string? RedoLabel => _redo.Count > 0 ? _redo.Peek().Label : null;
    public int UndoCount => _undo.Count;

    public event EventHandler? Changed;

    /// <summary>変更直前の状態を記録する。Redo 履歴は破棄される。</summary>
    public void Record(string label, T stateBeforeChange)
    {
        _undo.AddLast((label, stateBeforeChange));
        while (_undo.Count > Capacity) _undo.RemoveFirst();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>直前の状態を返す。current は Redo 用に保存される。</summary>
    public T Undo(T current)
    {
        if (_undo.Last is null) throw new InvalidOperationException("元に戻す操作がありません。");
        var (label, state) = _undo.Last.Value;
        _undo.RemoveLast();
        _redo.Push((label, current));
        Changed?.Invoke(this, EventArgs.Empty);
        return state;
    }

    public T Redo(T current)
    {
        if (_redo.Count == 0) throw new InvalidOperationException("やり直す操作がありません。");
        var (label, state) = _redo.Pop();
        _undo.AddLast((label, current));
        Changed?.Invoke(this, EventArgs.Empty);
        return state;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
