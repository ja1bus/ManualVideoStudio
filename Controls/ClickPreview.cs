using System.Collections;
using System.Windows;
using System.Windows.Media;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Imaging;

namespace ManualVideoStudio.Controls;

/// <summary>
/// プレビュー動画に重ねるクリック位置の強調（v1.21。設計 7.5）。<see cref="ShapePreview"/> と同じ作りで、動画の px 単位の大きさ
/// （VideoWidth x VideoHeight）で描き、Viewbox で表示の大きさへ拡大縮小する（輪の半径も表示の大きさに合わせて縮む）。
/// 描くのは今の出力時刻で表示中の輪（<see cref="VideoShapeLayers.ClickWindowsOf"/> の時間に入るもの。主画面の ViewModel が選ぶ）で、
/// 見た目は書き出しの輪の画像と同じ描画（<see cref="ClickRingRenderer.Draw"/>）。拡大の入れ物の中に置くので、拡大と一緒に大きくなる。
/// </summary>
public sealed class ClickPreview : FrameworkElement
{
    public static readonly DependencyProperty MarksProperty = DependencyProperty.Register(nameof(Marks), typeof(IEnumerable), typeof(ClickPreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(nameof(Radius), typeof(double), typeof(ClickPreview),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty VideoWidthProperty = DependencyProperty.Register(nameof(VideoWidth), typeof(int), typeof(ClickPreview),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty VideoHeightProperty = DependencyProperty.Register(nameof(VideoHeight), typeof(int), typeof(ClickPreview),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>表示中の輪（<see cref="ClickWindow"/> の列。X / Y は動画の px）。</summary>
    public IEnumerable? Marks { get => (IEnumerable?)GetValue(MarksProperty); set => SetValue(MarksProperty, value); }
    /// <summary>輪の半径（動画の px。<see cref="VideoShapeLayers.ClickRadiusFor"/>）。</summary>
    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }
    public int VideoWidth { get => (int)GetValue(VideoWidthProperty); set => SetValue(VideoWidthProperty, value); }
    public int VideoHeight { get => (int)GetValue(VideoHeightProperty); set => SetValue(VideoHeightProperty, value); }

    public ClickPreview() => IsHitTestVisible = false;

    protected override Size MeasureOverride(Size availableSize)
        => VideoWidth > 0 && VideoHeight > 0 ? new Size(VideoWidth, VideoHeight) : new Size(1, 1);

    protected override void OnRender(DrawingContext dc)
    {
        int w = VideoWidth, h = VideoHeight;
        if (Marks is null || w <= 0 || h <= 0 || !(Radius > 0)) return;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
        foreach (var m in Marks.OfType<ClickWindow>())
            ClickRingRenderer.Draw(dc, new Point(m.X, m.Y), Radius);
        dc.Pop();
    }
}
