using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ManualVideoStudio.Core.Annotations;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Imaging;

namespace ManualVideoStudio.Controls;

/// <summary>
/// プレビュー動画に重ねる図形（v1.6）。再生位置で表示中の図形のまとまりを、動画の px 単位の大きさ（VideoWidth x VideoHeight）で描き、
/// Viewbox で動画と同じ比率に拡大縮小する。四角形・矢印・直線・文字は書き出しと同じ描画処理（AnnotationDrawing）。
/// モザイクは再生中の映像にかけると重いため、灰色の格子の目安表示にする（実際の見た目は書き出した動画で確認する）。
/// </summary>
public sealed class ShapePreview : FrameworkElement
{
    public static readonly DependencyProperty GroupsProperty = DependencyProperty.Register(nameof(Groups), typeof(IEnumerable), typeof(ShapePreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty VideoWidthProperty = DependencyProperty.Register(nameof(VideoWidth), typeof(int), typeof(ShapePreview),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty VideoHeightProperty = DependencyProperty.Register(nameof(VideoHeight), typeof(int), typeof(ShapePreview),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush MosaicDark = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0x70, 0x70, 0x70)));
    private static readonly Brush MosaicLight = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0x9A, 0x9A, 0x9A)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
    private static readonly Typeface LabelFace = new("Meiryo");

    /// <summary>表示中の図形のまとまり（ShapeGroup の列）。</summary>
    public IEnumerable? Groups { get => (IEnumerable?)GetValue(GroupsProperty); set => SetValue(GroupsProperty, value); }
    public int VideoWidth { get => (int)GetValue(VideoWidthProperty); set => SetValue(VideoWidthProperty, value); }
    public int VideoHeight { get => (int)GetValue(VideoHeightProperty); set => SetValue(VideoHeightProperty, value); }

    public ShapePreview() => IsHitTestVisible = false;

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    protected override Size MeasureOverride(Size availableSize)
        => VideoWidth > 0 && VideoHeight > 0 ? new Size(VideoWidth, VideoHeight) : new Size(1, 1);

    protected override void OnRender(DrawingContext dc)
    {
        int w = VideoWidth, h = VideoHeight;
        if (Groups is null || w <= 0 || h <= 0) return;
        var groups = Groups.OfType<ShapeGroup>().ToList();
        if (groups.Count == 0) return;
        var scale = AnnotationStyle.Scale(w, h);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
        // 書き出しと同じ順: モザイク（すべて）→ 各まとまりの四角形・矢印・直線・文字。
        var fitted = groups.Select(g => AnnotationGeometry.FitToImage(g.Shapes, g.ImageWidth, g.ImageHeight, w, h)).ToList();
        foreach (var m in fitted.SelectMany(f => f).OfType<MosaicAnnotation>())
            DrawMosaicPlaceholder(dc, m, w, h);
        foreach (var f in fitted)
            AnnotationDrawing.DrawVectors(dc, f.Where(a => a is not MosaicAnnotation), scale);
        dc.Pop();
    }

    /// <summary>モザイクの目安表示（マスの大きさの市松模様。映像はほぼ見えない濃さ）。</summary>
    private static void DrawMosaicPlaceholder(DrawingContext dc, MosaicAnnotation m, int w, int h)
    {
        var r = AnnotationGeometry.ClipToImage(new ImageRect(m.X, m.Y, m.Width, m.Height), w, h);
        if (r.Width <= 0 || r.Height <= 0) return;
        var rect = new Rect(r.X, r.Y, r.Width, r.Height);
        dc.PushClip(new RectangleGeometry(rect));
        dc.DrawRectangle(MosaicDark, null, rect);
        double block = Math.Max(AnnotationStyle.MinMosaicBlock, m.BlockSize);
        int row = 0;
        for (double y = r.Y; y < r.Bottom; y += block, row++)
        {
            int col = 0;
            for (double x = r.X; x < r.Right; x += block, col++)
                if ((row + col) % 2 == 0) dc.DrawRectangle(MosaicLight, null, new Rect(x, y, block, block));
        }
        var size = Math.Clamp(Math.Min(r.Width, r.Height) * 0.3, 12, 40);
        var ft = new FormattedText("モザイク", CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight, LabelFace, size, LabelBrush, 1.0);
        if (ft.Width < r.Width && ft.Height < r.Height)
            dc.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y + (r.Height - ft.Height) / 2));
        dc.Pop();
    }
}
