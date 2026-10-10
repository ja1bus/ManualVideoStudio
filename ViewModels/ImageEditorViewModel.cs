using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Annotations;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Imaging;
using ManualVideoStudio.Services;
using Microsoft.Extensions.Logging;

namespace ManualVideoStudio.ViewModels;

public enum EditorTool
{
    Select,
    Rectangle,
    Arrow,
    /// <summary>矢じりの無い直線（v1.6）。</summary>
    Line,
    Text,
    Mosaic,
}

/// <summary>注釈編集画面に渡す内容。</summary>
public sealed record ImageEditorRequest(
    CapturePoint Point,
    /// <summary>注釈なしの画像（元動画から切り出したフレーム）。</summary>
    BitmapSource BaseImage,
    /// <summary>上書き保存する画像ファイル。未出力なら null（注釈はプロジェクトにだけ保存し、画像の書き出し時に反映）。</summary>
    string? OutputPath,
    ImageFormat OutputFormat,
    int JpegQuality,
    /// <summary>保存した注釈をプロジェクトへ反映する（画像サイズ付き）。</summary>
    Func<IReadOnlyList<Annotation>, int, int, EditResult> Commit);

/// <summary>
/// 動画に重ねる図形（v1.6）の編集画面に渡す内容。背景は編集後の動画の 1 コマ（注釈なし）。
/// 時刻は出力動画（カット適用後）の秒。Commit は図形・基準画像サイズ・表示時間を受け取り、プロジェクトへ反映する（主画面の Undo 対象）。
/// </summary>
public sealed record VideoShapeEditRequest(
    string Title,
    BitmapSource BaseImage,
    IReadOnlyList<Annotation> Shapes,
    int ShapesWidth,
    int ShapesHeight,
    double Start,
    double End,
    double OutputDuration,
    /// <summary>背景の 1 コマの説明（例: 「編集後 0:12.3 の画面」）。</summary>
    string BackgroundText,
    Func<IReadOnlyList<Annotation>, int, int, double, double, EditResult> Commit);

/// <summary>
/// 手順書画像の簡易注釈編集。編集中は注釈をオブジェクトとして持ち（AnnotationSession）、保存時に画像へ焼き込んで元ファイルへ上書きする。
/// 画面上の座標変換は AnnotationCanvas が行い、ここでは画像 px の座標だけを扱う。
/// v1.6 から、動画に重ねる図形の編集にも使う（IsVideoMode。表示時間を持ち、OK でプロジェクトへ反映して閉じる。画像ファイルは作らない）。
/// </summary>
public sealed partial class ImageEditorViewModel : ObservableObject
{
    private readonly ImageEditorRequest? _request;
    private readonly VideoShapeEditRequest? _video;
    private readonly IDialogService _dialogs;
    private readonly IWindowService _windows;
    private readonly ILogger _logger;
    /// <summary>動画の図形: 保存済み（開いた時点）の表示時間の文字列（未保存の変更の判定用）。</summary>
    private string _savedTimes = "";

    public ImageEditorViewModel(ImageEditorRequest request, IDialogService dialogs, IWindowService windows, ILogger logger)
        : this(request.BaseImage, request.Point.Annotations, request.Point.AnnotationImageWidth, request.Point.AnnotationImageHeight, dialogs, windows, logger)
    {
        _request = request;
        // 手順書画像の赤枠は太線で統一する（tejunsho-gui-docx の撮影規約。v1.8）。動画の図形は「中」のまま。
        _strokeLevel = StrokeWidthLevel.Thick;
        var p = request.Point;
        Title = $"画像の注釈 - No.{p.Sequence} {p.Caption}".TrimEnd();
        TargetText = request.OutputPath is null
            ? "画像ファイルは未出力です。保存した注釈はプロジェクトに記録され、［画像を書き出し］で画像に反映されます。"
            : $"保存先（上書き）: {request.OutputPath}";
        _statusText = "ツールを選んで画像上をドラッグします。選択ツールで注釈の移動・つまみでの変形ができます。";
    }

    /// <summary>動画に重ねる図形の編集（v1.6）。</summary>
    public ImageEditorViewModel(VideoShapeEditRequest request, IDialogService dialogs, IWindowService windows, ILogger logger)
        : this(request.BaseImage, request.Shapes, request.ShapesWidth, request.ShapesHeight, dialogs, windows, logger)
    {
        _video = request;
        Title = request.Title;
        TargetText = request.BackgroundText;
        _startText = TimeFormat.Format(request.Start);
        _endText = TimeFormat.Format(request.End);
        _savedTimes = TimesKey;
        OutputDurationText = TimeFormat.Format(request.OutputDuration);
        _statusText = "ツールを選んで画面上をドラッグします。表示する時間（開始・終了）を決めて［OK］を押すと動画に反映されます。";
    }

    private ImageEditorViewModel(BitmapSource baseImage, IReadOnlyList<Annotation> annotations, int annotationWidth, int annotationHeight,
        IDialogService dialogs, IWindowService windows, ILogger logger)
    {
        _dialogs = dialogs;
        _windows = windows;
        _logger = logger;
        BaseImage = baseImage;
        var w = baseImage.PixelWidth;
        var h = baseImage.PixelHeight;
        var initial = AnnotationGeometry.FitToImage(annotations, annotationWidth, annotationHeight, w, h);
        Session = new AnnotationSession(initial, w, h);
        Session.Changed += (_, _) => OnSessionChanged();
        ImageSizeText = $"{w} x {h}";
        Title = "";
        TargetText = "";
        _statusText = "";
    }

    public event Action? CloseRequested;
    /// <summary>描画の更新が必要（注釈・選択・表示倍率）。</summary>
    public event Action? RenderRequested;

    public AnnotationSession Session { get; }
    public BitmapSource BaseImage { get; }
    public string Title { get; private set; }
    public string TargetText { get; private set; }
    public bool IsOutputMissing => _request is { OutputPath: null };

    // ---------------- 動画の図形（v1.6） ----------------

    /// <summary>動画に重ねる図形の編集か（表示時間の欄を出し、保存は OK で閉じる）。</summary>
    public bool IsVideoMode => _video is not null;
    public bool IsImageMode => _video is null;
    public string OutputDurationText { get; } = "";
    public string SaveButtonText => IsVideoMode ? "OK（動画に反映）" : "保存（上書き）";
    public string SaveButtonToolTip => IsVideoMode
        ? "図形と表示時間を動画に反映して閉じます（主画面の Ctrl+Z で元に戻せます） [Ctrl+S]"
        : "画像へ焼き込んで元のファイルに上書き保存 [Ctrl+S]";
    public string CloseButtonText => IsVideoMode ? "キャンセル" : "閉じる";

    /// <summary>表示開始（編集後の動画の時刻）。</summary>
    [ObservableProperty] private string _startText = "";
    /// <summary>表示終了（編集後の動画の時刻）。</summary>
    [ObservableProperty] private string _endText = "";
    [ObservableProperty] private string _timeError = "";

    private string TimesKey => $"{StartText.Trim()}|{EndText.Trim()}";

    partial void OnStartTextChanged(string value) => OnTimesChanged();
    partial void OnEndTextChanged(string value) => OnTimesChanged();

    private void OnTimesChanged()
    {
        TimeError = "";
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(WindowTitle));
    }

    /// <summary>［最後まで］: 終了を編集後の動画の最後にする。</summary>
    [RelayCommand]
    private void SetEndToLast()
    {
        if (_video is null) return;
        EndText = TimeFormat.Format(_video.OutputDuration);
        StatusText = "終了を動画の最後にしました。";
    }
    public string ImageSizeText { get; }
    public IReadOnlyList<double> FontSizes => AnnotationStyle.FontSizes;

    [ObservableProperty] private EditorTool _tool = EditorTool.Rectangle;
    [ObservableProperty] private AnnotationColor _color = AnnotationColor.Red;
    [ObservableProperty] private StrokeWidthLevel _strokeLevel = StrokeWidthLevel.Medium;
    [ObservableProperty] private double _fontSize = AnnotationStyle.DefaultFontSize;
    [ObservableProperty] private bool _isBold;
    [ObservableProperty] private string _statusText;
    /// <summary>表示倍率（1 = 100%）。IsFit のときは画面に合わせた実際の倍率が入る。</summary>
    [ObservableProperty] private double _zoom = 1;
    [ObservableProperty] private bool _isFit = true;

    public bool IsDirty => Session.IsDirty || (IsVideoMode && TimesKey != _savedTimes);
    public bool HasSelection => Session.Selected is not null;
    public bool HasAnnotations => Session.Items.Count > 0;
    public bool SelectedIsText => Session.Selected is TextAnnotation;
    public string ZoomText => $"{Zoom:0%}";
    public string UndoText => Session.History.UndoLabel is { } l ? $"元に戻す: {l} (Ctrl+Z)" : "元に戻す (Ctrl+Z)";
    public string RedoText => Session.History.RedoLabel is { } l ? $"やり直し: {l} (Ctrl+Y)" : "やり直し (Ctrl+Y)";
    public string CountText => Session.Items.Count == 0 ? (IsVideoMode ? "図形なし" : "注釈なし") : $"{(IsVideoMode ? "図形" : "注釈")} {Session.Items.Count} 件";
    public string SelectionText => Session.Selected is { } a ? $"選択中: {AnnotationSession.KindLabel(a.Kind)}" : "";
    public string WindowTitle => (IsDirty ? "* " : "") + Title;

    public double StrokeWidthValue => AnnotationStyle.StrokeWidth(StrokeLevel);

    // ---------------- ツール・スタイル ----------------

    [RelayCommand]
    private void SetTool(EditorTool tool)
    {
        Tool = tool;
        if (tool != EditorTool.Select) Session.Select(null);
        StatusText = tool switch
        {
            EditorTool.Rectangle => "四角形: 囲みたい範囲をドラッグします。",
            EditorTool.Arrow => "矢印: 始点から終点（矢じり側）へドラッグします。",
            EditorTool.Line => "直線: 始点から終点へドラッグします。",
            EditorTool.Text => "文字: 置きたい位置をクリックして文字を入力します。",
            EditorTool.Mosaic => "モザイク: 隠したい範囲をドラッグします。保存画像では元の文字を読めなくなります。",
            _ => "選択: 注釈をクリックで選択、ドラッグで移動、つまみで変形。ダブルクリックで文字を編集。Delete で削除。",
        };
        RenderRequested?.Invoke();
    }

    /// <summary>色の切り替え。注釈を選択中なら、その注釈の色も変える（Undo 対象）。</summary>
    [RelayCommand]
    private void SetColor(AnnotationColor color)
    {
        Color = color;
        if (Session.SelectedId is { } id) Session.SetColor(id, color);
    }

    [RelayCommand]
    private void SetStroke(StrokeWidthLevel level)
    {
        StrokeLevel = level;
        if (Session.SelectedId is { } id) Session.SetStrokeWidth(id, AnnotationStyle.StrokeWidth(level));
    }

    /// <summary>文字サイズのプルダウン。文字を選択中なら、その文字にも適用する（Undo 対象）。</summary>
    partial void OnFontSizeChanged(double value)
    {
        if (Session.SelectedId is { } id) Session.SetFontSize(id, value);
    }

    /// <summary>普通 / 太字の切り替え。文字を選択中なら、その文字にも適用する（Undo 対象）。</summary>
    [RelayCommand]
    private void SetBold(bool bold)
    {
        IsBold = bold;
        if (Session.SelectedId is { } id) Session.SetBold(id, bold);
    }

    partial void OnStrokeLevelChanged(StrokeWidthLevel value) => OnPropertyChanged(nameof(StrokeWidthValue));
    partial void OnZoomChanged(double value)
    {
        OnPropertyChanged(nameof(ZoomText));
        if (!IsFit) RenderRequested?.Invoke();
    }

    // ---------------- 注釈の作成（Canvas から呼ぶ。座標は画像 px） ----------------

    /// <summary>ドラッグで作る注釈の見本（作成中のプレビュー表示にも使う）。小さすぎる場合は null。</summary>
    public Annotation? CreateShape(ImagePoint start, ImagePoint end)
    {
        start = AnnotationGeometry.Clamp(start, Session.ImageWidth, Session.ImageHeight);
        end = AnnotationGeometry.Clamp(end, Session.ImageWidth, Session.ImageHeight);
        var r = ImageRect.FromPoints(start, end);
        switch (Tool)
        {
            case EditorTool.Rectangle:
                return r.Width < AnnotationStyle.MinBoxSize || r.Height < AnnotationStyle.MinBoxSize ? null
                    : new RectangleAnnotation { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height, Color = Color, StrokeWidth = StrokeWidthValue };
            case EditorTool.Mosaic:
                return r.Width < AnnotationStyle.MinBoxSize || r.Height < AnnotationStyle.MinBoxSize ? null
                    : new MosaicAnnotation
                    {
                        X = r.X, Y = r.Y, Width = r.Width, Height = r.Height,
                        BlockSize = AnnotationStyle.DefaultMosaicBlockSize(Session.ImageWidth, Session.ImageHeight),
                    };
            case EditorTool.Arrow:
                return AnnotationGeometry.DistanceToPoint(start, end) < AnnotationStyle.MinArrowLength ? null
                    : new ArrowAnnotation { X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y, Color = Color, StrokeWidth = StrokeWidthValue };
            case EditorTool.Line:
                return AnnotationGeometry.DistanceToPoint(start, end) < AnnotationStyle.MinArrowLength ? null
                    : new LineAnnotation { X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y, Color = Color, StrokeWidth = StrokeWidthValue };
            default:
                return null;
        }
    }

    public void CommitShape(ImagePoint start, ImagePoint end)
    {
        var shape = CreateShape(start, end);
        if (shape is null)
        {
            StatusText = "範囲が小さすぎるため作成しませんでした。ドラッグして範囲を指定してください。";
            return;
        }
        Session.Add(shape);
        // 続けて描けるよう選択は外す（選択したままだと色・線幅の切り替えが作ったばかりの注釈に適用されるため）。
        Session.Select(null);
        StatusText = $"{AnnotationSession.KindLabel(shape.Kind)}を追加しました（Ctrl+Z で取り消し）。移動・変形は［選択］ツールで行います。";
    }

    /// <summary>文字ツールでクリックした位置に文字を追加する。</summary>
    public void AddTextAt(ImagePoint p)
    {
        var text = _windows.PromptText("文字の追加", "画像に表示する文字を入力してください（Enter で改行、Ctrl+Enter で決定）。", "");
        text = AnnotationSession.NormalizeText(text);
        if (text.Length == 0) return;
        p = AnnotationGeometry.Clamp(p, Session.ImageWidth, Session.ImageHeight);
        var t = new TextAnnotation { X = p.X, Y = p.Y, Text = text, FontSize = FontSize, Bold = IsBold, Color = Color };
        // 右端・下端からはみ出す場合は内側へ寄せる。
        var (w, h) = AnnotationDrawing.MeasureText(t, Session.StyleScale);
        t.X = Math.Max(0, Math.Min(t.X, Session.ImageWidth - w));
        t.Y = Math.Max(0, Math.Min(t.Y, Session.ImageHeight - h));
        Session.Add(t);
        Session.Select(null);
        StatusText = "文字を追加しました。選択ツールでダブルクリックすると編集できます。";
    }

    /// <summary>選択中の注釈の色・線幅・文字サイズを、ツールバーの表示に反映する。</summary>
    public void SyncStyleFromSelection()
    {
        switch (Session.Selected)
        {
            case RectangleAnnotation r:
                Color = r.Color;
                StrokeLevel = AnnotationStyle.LevelOf(r.StrokeWidth);
                break;
            case SegmentAnnotation a:
                Color = a.Color;
                StrokeLevel = AnnotationStyle.LevelOf(a.StrokeWidth);
                break;
            case TextAnnotation t:
                Color = t.Color;
                FontSize = t.FontSize;
                IsBold = t.Bold;
                break;
        }
    }

    public (double Width, double Height) MeasureText(TextAnnotation t, double styleScale) => AnnotationDrawing.MeasureText(t, styleScale);

    // ---------------- 編集操作 ----------------

    [RelayCommand]
    private void EditText()
    {
        if (Session.Selected is not TextAnnotation t) return;
        var text = _windows.PromptText("文字の編集", "文字を編集してください（空にすると削除します）。", t.Text);
        if (text is null) return;
        if (Session.SetText(t.Id, text))
            StatusText = AnnotationSession.NormalizeText(text).Length == 0 ? "文字を削除しました。" : "文字を変更しました。";
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (Session.Selected is not { } a) return;
        Session.RemoveSelected();
        StatusText = $"{AnnotationSession.KindLabel(a.Kind)}を削除しました（Ctrl+Z で元に戻す）。";
    }

    [RelayCommand]
    private void ClearAll()
    {
        if (Session.Items.Count == 0) return;
        var what = IsVideoMode ? "図形" : "この画像の注釈";
        if (Session.Items.Count > 1 && !_dialogs.Confirm($"{what} {Session.Items.Count} 件をすべて削除しますか？（Ctrl+Z で元に戻せます）", "全削除"))
            return;
        Session.Clear();
        StatusText = $"{(IsVideoMode ? "図形" : "注釈")}をすべて削除しました（Ctrl+Z で元に戻す）。";
    }

    private bool CanUndo() => Session.History.CanUndo;
    private bool CanRedo() => Session.History.CanRedo;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        var label = Session.History.UndoLabel;
        if (!Session.Undo()) return;
        SyncStyleFromSelection();
        StatusText = $"元に戻しました: {label}";
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        var label = Session.History.RedoLabel;
        if (!Session.Redo()) return;
        SyncStyleFromSelection();
        StatusText = $"やり直しました: {label}";
    }

    /// <summary>Esc: 作成・ドラッグ中は Canvas 側で取り消す。それ以外は選択を解除する。</summary>
    public void Escape()
    {
        if (Session.IsInteractive) Session.CancelInteractive();
        else Session.Select(null);
    }

    public void NudgeSelected(double dx, double dy)
    {
        if (Session.SelectedId is { } id) Session.Move(id, dx, dy, MeasureText);
    }

    // ---------------- 表示倍率 ----------------

    [RelayCommand] private void ZoomFit() => IsFit = true;

    [RelayCommand]
    private void ZoomActual()
    {
        IsFit = false;
        Zoom = 1;
    }

    [RelayCommand] private void ZoomIn() => SetZoom(Zoom * 1.25);
    [RelayCommand] private void ZoomOut() => SetZoom(Zoom / 1.25);

    public void SetZoom(double zoom)
    {
        IsFit = false;
        Zoom = Math.Clamp(zoom, 0.1, 8);
    }

    partial void OnIsFitChanged(bool value) => RenderRequested?.Invoke();

    // ---------------- 保存・閉じる ----------------

    /// <summary>
    /// 保存。変更が無ければ何もしない。画像ファイルがあれば注釈を焼き込んで同じ名前・同じ形式で上書きし（一時ファイル経由）、
    /// プロジェクトの注釈も更新する。成功（または変更なし）で true。
    /// </summary>
    [RelayCommand]
    private void Save()
    {
        // 動画の図形は OK で反映して閉じる（字幕の追加・編集と同じ）。
        if (TrySave() && IsVideoMode) CloseRequested?.Invoke();
    }

    public bool TrySave()
    {
        if (Session.IsInteractive) Session.EndInteractive();
        if (_video is not null) return TrySaveVideo(_video);
        var request = _request!;
        if (!Session.IsDirty)
        {
            StatusText = "変更はありません。";
            return true;
        }
        var annotations = Session.Snapshot();
        // プロジェクトに記録する値（小数第 1 位）で描画し、後の再書き出しと画素をそろえる。
        foreach (var a in annotations) a.RoundCoordinates();
        try
        {
            if (request.OutputPath is { } path)
            {
                var image = AnnotationRenderer.Render(BaseImage, annotations, Session.ImageWidth, Session.ImageHeight);
                ImageFiles.SaveAtomic(image, path, request.OutputFormat, AnnotatedImageFiles.JpegQualityLevel(request.JpegQuality));
                _logger.LogInformation("Annotated image saved: {Path} ({Count} annotations)", path, annotations.Count);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            _logger.LogError(ex, "Annotated image save failed: {Path}", request.OutputPath);
            _dialogs.ShowError("画像を保存できませんでした。元の画像は変更していません。", ex.Message);
            return false;
        }

        var r = request.Commit(annotations, Session.ImageWidth, Session.ImageHeight);
        if (!r.Success)
        {
            _dialogs.ShowError(r.Message);
            return false;
        }
        Session.MarkSaved();
        OnSessionChanged();
        StatusText = request.OutputPath is null
            ? "注釈をプロジェクトに記録しました（画像ファイルは未出力。［画像を書き出し］で反映されます）。"
            : $"上書き保存しました: {Path.GetFileName(request.OutputPath)}";
        return true;
    }

    /// <summary>動画の図形: 表示時間を確かめてプロジェクトへ反映する（画像ファイルは作らない）。</summary>
    private bool TrySaveVideo(VideoShapeEditRequest video)
    {
        if (!TimeFormat.TryParse(StartText, out var start))
        {
            TimeError = "開始を HH:MM:SS.mmm 形式で入力してください。";
            StatusText = TimeError;
            return false;
        }
        if (!TimeFormat.TryParse(EndText, out var end))
        {
            TimeError = "終了を HH:MM:SS.mmm 形式で入力してください。";
            StatusText = TimeError;
            return false;
        }
        if (!IsDirty)
        {
            StatusText = "変更はありません。";
            return true;
        }
        var shapes = Session.Snapshot();
        foreach (var a in shapes) a.RoundCoordinates();
        var r = video.Commit(shapes, Session.ImageWidth, Session.ImageHeight, start, end);
        if (!r.Success)
        {
            TimeError = r.Message;
            StatusText = r.Message;
            return false;
        }
        Session.MarkSaved();
        _savedTimes = TimesKey;
        OnSessionChanged();
        StatusText = "動画に反映しました。";
        return true;
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    /// <summary>閉じてよいか。未保存の変更があれば保存するか確認する。</summary>
    public bool ConfirmClose()
    {
        if (Session.IsInteractive) Session.CancelInteractive();
        if (!Session.IsDirty) return true;
        var r = IsVideoMode
            ? _dialogs.ConfirmYesNoCancel("変更内容を動画に反映しますか？" + Environment.NewLine + Environment.NewLine
                + "［はい］図形と表示時間を反映　［いいえ］反映しないで閉じる", "図形の反映")
            : _dialogs.ConfirmYesNoCancel("変更内容を保存しますか？" + Environment.NewLine + Environment.NewLine
                + (_request!.OutputPath is null ? "［はい］注釈をプロジェクトに記録" : "［はい］画像へ上書き保存") + "　［いいえ］保存しないで閉じる", "注釈の保存");
        return r switch
        {
            true => TrySave(),
            false => true,
            null => false,
        };
    }

    private void OnSessionChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasAnnotations));
        OnPropertyChanged(nameof(SelectedIsText));
        OnPropertyChanged(nameof(UndoText));
        OnPropertyChanged(nameof(RedoText));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(WindowTitle));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        RenderRequested?.Invoke();
    }
}
