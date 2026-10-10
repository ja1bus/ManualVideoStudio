using System.Text.Json;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;

namespace ManualVideoStudio.Core.Annotations;

/// <summary>
/// 1 枚の画像に対する注釈編集の状態（UI 非依存）。Undo / Redo は既存の UndoRedoService（スナップショット方式）を使う。
/// 注釈は非破壊のオブジェクトとして保持し、保存時に描画側（Imaging）が画像へ焼き込む。
/// 保存済み状態との比較で未保存変更（IsDirty）を判定する。
/// </summary>
public sealed class AnnotationSession
{
    private List<Annotation> _items;
    private string _savedJson;
    // ドラッグ中の移動・変形は開始時点の状態を持ち、終了時に変化があれば 1 回だけ記録する。
    private (string Label, List<Annotation> Before)? _interactive;

    public AnnotationSession(IEnumerable<Annotation> initial, int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0) throw new ArgumentOutOfRangeException(nameof(imageWidth), "画像サイズが不正です。");
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
        _items = initial.Select(a => a.Clone()).ToList();
        _savedJson = Serialize(_items);
    }

    public int ImageWidth { get; }
    public int ImageHeight { get; }
    public double StyleScale => AnnotationStyle.Scale(ImageWidth, ImageHeight);
    public IReadOnlyList<Annotation> Items => _items;
    public UndoRedoService<List<Annotation>> History { get; } = new();
    public string? SelectedId { get; private set; }
    public Annotation? Selected => SelectedId is null ? null : _items.FirstOrDefault(a => a.Id == SelectedId);
    public bool IsDirty => Serialize(_items) != _savedJson;
    public bool IsInteractive => _interactive is not null;

    /// <summary>注釈・選択の変更後に発生する（描画の更新用）。</summary>
    public event EventHandler? Changed;

    /// <summary>保存用の複製。</summary>
    public List<Annotation> Snapshot() => _items.Select(a => a.Clone()).ToList();

    public void MarkSaved() => _savedJson = Serialize(_items);

    public void Select(string? id)
    {
        if (id is not null && _items.All(a => a.Id != id)) id = null;
        if (SelectedId == id) return;
        SelectedId = id;
        Raise();
    }

    // ---------------- 追加・削除 ----------------

    public void Add(Annotation annotation)
    {
        var a = annotation.Clone();
        Record(AddLabel(a.Kind));
        _items.Add(a);
        SelectedId = a.Id;
        Raise();
    }

    public bool Remove(string id)
    {
        var a = _items.FirstOrDefault(x => x.Id == id);
        if (a is null) return false;
        Record($"{KindLabel(a.Kind)}の削除");
        _items.Remove(a);
        if (SelectedId == id) SelectedId = null;
        Raise();
        return true;
    }

    public bool RemoveSelected() => SelectedId is { } id && Remove(id);

    public bool Clear()
    {
        if (_items.Count == 0) return false;
        Record("注釈の全削除");
        _items.Clear();
        SelectedId = null;
        Raise();
        return true;
    }

    // ---------------- プロパティ変更（1 操作 = 1 Undo） ----------------

    public bool SetColor(string id, AnnotationColor color) => Change(id, "色の変更", a =>
    {
        switch (a)
        {
            case RectangleAnnotation r when r.Color != color: r.Color = color; return true;
            case SegmentAnnotation ar when ar.Color != color: ar.Color = color; return true;
            case TextAnnotation t when t.Color != color: t.Color = color; return true;
            default: return false;
        }
    });

    public bool SetStrokeWidth(string id, double width) => Change(id, "線幅の変更", a =>
    {
        switch (a)
        {
            case RectangleAnnotation r when r.StrokeWidth != width: r.StrokeWidth = width; return true;
            case SegmentAnnotation ar when ar.StrokeWidth != width: ar.StrokeWidth = width; return true;
            default: return false;
        }
    });

    public bool SetFontSize(string id, double size) => Change(id, "文字サイズの変更", a =>
    {
        if (a is not TextAnnotation t || t.FontSize == size) return false;
        t.FontSize = size;
        return true;
    });

    public bool SetBold(string id, bool bold) => Change(id, "太さの変更", a =>
    {
        if (a is not TextAnnotation t || t.Bold == bold) return false;
        t.Bold = bold;
        return true;
    });

    /// <summary>文字を変更する。空文字にした場合は注釈を削除する。</summary>
    public bool SetText(string id, string text)
    {
        text = NormalizeText(text);
        if (_items.FirstOrDefault(x => x.Id == id) is not TextAnnotation t) return false;
        if (text.Length == 0) return Remove(id);
        return Change(id, "文字の編集", _ =>
        {
            if (t.Text == text) return false;
            t.Text = text;
            return true;
        });
    }

    /// <summary>移動（キー操作など 1 回で完結するもの）。画像からはみ出さない範囲に詰める。</summary>
    public bool Move(string id, double dx, double dy, AnnotationGeometry.TextMeasurer measure)
    {
        var a = _items.FirstOrDefault(x => x.Id == id);
        if (a is null) return false;
        var (cx, cy) = AnnotationGeometry.ClampOffset(AnnotationGeometry.ShapeBounds(a, StyleScale, measure), dx, dy, ImageWidth, ImageHeight);
        if (Math.Abs(cx) < 1e-9 && Math.Abs(cy) < 1e-9) return false;
        Record("注釈の移動");
        a.Offset(cx, cy);
        Raise();
        return true;
    }

    // ---------------- ドラッグ操作（移動・つまみでの変形） ----------------

    /// <summary>ドラッグ開始。以降 UpdateInteractive で形を変え、EndInteractive で確定（変化があれば 1 回だけ Undo 記録）。</summary>
    public void BeginInteractive(string label)
    {
        if (_interactive is not null) EndInteractive();
        _interactive = (label, Snapshot());
    }

    /// <summary>ドラッグ開始時の注釈（変形の基準）。</summary>
    public Annotation? InteractiveOriginal(string id) => _interactive?.Before.FirstOrDefault(a => a.Id == id);

    public void UpdateInteractive(string id, Action<Annotation> update)
    {
        if (_interactive is null) return;
        var a = _items.FirstOrDefault(x => x.Id == id);
        if (a is null) return;
        update(a);
        Raise();
    }

    public bool EndInteractive()
    {
        if (_interactive is not { } state) return false;
        _interactive = null;
        if (Serialize(state.Before) == Serialize(_items)) return false;
        History.Record(state.Label, state.Before);
        Raise();
        return true;
    }

    /// <summary>ドラッグを取り消して開始時の状態へ戻す（Esc）。</summary>
    public void CancelInteractive()
    {
        if (_interactive is not { } state) return;
        _interactive = null;
        _items = state.Before;
        Raise();
    }

    // ---------------- Undo / Redo ----------------

    public bool Undo()
    {
        if (_interactive is not null) CancelInteractive();
        if (!History.CanUndo) return false;
        _items = History.Undo(Snapshot());
        FixSelection();
        Raise();
        return true;
    }

    public bool Redo()
    {
        if (_interactive is not null) CancelInteractive();
        if (!History.CanRedo) return false;
        _items = History.Redo(Snapshot());
        FixSelection();
        Raise();
        return true;
    }

    // ---------------- 補助 ----------------

    /// <summary>文字注釈の入力を整える（改行を LF に統一し、前後の空白行を除く）。</summary>
    public static string NormalizeText(string? text)
        => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    public static string KindLabel(AnnotationKind kind) => kind switch
    {
        AnnotationKind.Rectangle => "四角形",
        AnnotationKind.Arrow => "矢印",
        AnnotationKind.Line => "直線",
        AnnotationKind.Text => "文字",
        AnnotationKind.Mosaic => "モザイク",
        _ => "注釈",
    };

    private static string AddLabel(AnnotationKind kind) => $"{KindLabel(kind)}の追加";

    private bool Change(string id, string label, Func<Annotation, bool> mutate)
    {
        var a = _items.FirstOrDefault(x => x.Id == id);
        if (a is null) return false;
        var before = Snapshot();
        if (!mutate(a)) return false;
        History.Record(label, before);
        Raise();
        return true;
    }

    private void Record(string label) => History.Record(label, Snapshot());

    private void FixSelection()
    {
        if (SelectedId is not null && _items.All(a => a.Id != SelectedId)) SelectedId = null;
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    private static string Serialize(List<Annotation> items) => JsonSerializer.Serialize(items, JsonDefaults.Options);
}
