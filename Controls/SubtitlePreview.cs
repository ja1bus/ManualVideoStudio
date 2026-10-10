using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Subtitles;
using ManualVideoStudio.Services;

namespace ManualVideoStudio.Controls;

/// <summary>
/// プレビュー動画に重ねる字幕（確認用の近似表示）。動画の px 単位の大きさ（VideoWidth x VideoHeight）で配置し、
/// Viewbox で動画と同じ比率に拡大縮小する。配置・文字サイズ・縁取りは焼き込み（ASS）と同じ計算（AssSubtitleBuilder.Layout）。
/// 実際の書き出しは libass で描くため、折り返し位置・字形は完全には一致しない。
/// </summary>
public sealed class SubtitlePreview : FrameworkElement
{
    public static readonly DependencyProperty SegmentProperty = DependencyProperty.Register(nameof(Segment), typeof(SubtitleSegment), typeof(SubtitlePreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SubtitleStyleProperty = DependencyProperty.Register(nameof(SubtitleStyle), typeof(SubtitleStyle), typeof(SubtitlePreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty VideoWidthProperty = DependencyProperty.Register(nameof(VideoWidth), typeof(int), typeof(SubtitlePreview),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty VideoHeightProperty = DependencyProperty.Register(nameof(VideoHeight), typeof(int), typeof(SubtitlePreview),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public SubtitleSegment? Segment { get => (SubtitleSegment?)GetValue(SegmentProperty); set => SetValue(SegmentProperty, value); }
    /// <summary>見た目（プリセット）とフォント。null なら既定。</summary>
    public SubtitleStyle? SubtitleStyle { get => (SubtitleStyle?)GetValue(SubtitleStyleProperty); set => SetValue(SubtitleStyleProperty, value); }
    public int VideoWidth { get => (int)GetValue(VideoWidthProperty); set => SetValue(VideoWidthProperty, value); }
    public int VideoHeight { get => (int)GetValue(VideoHeightProperty); set => SetValue(VideoHeightProperty, value); }

    public SubtitlePreview() => IsHitTestVisible = false;

    private static SolidColorBrush Brush(uint assColor)
    {
        var (a, r, g, b) = SubtitleColors.ToArgb(assColor);
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    protected override Size MeasureOverride(Size availableSize)
        => VideoWidth > 0 && VideoHeight > 0 ? new Size(VideoWidth, VideoHeight) : new Size(1, 1);

    protected override void OnRender(DrawingContext dc)
    {
        if (Segment is not { } s || VideoWidth <= 0 || VideoHeight <= 0 || string.IsNullOrWhiteSpace(s.Text)) return;
        var style = SubtitleStyle ?? new SubtitleStyle();
        var colors = SubtitleColors.Of(style.Preset);
        var family = Core.Models.SubtitleStyle.NormalizeFontFamily(style.FontFamily);
        var layout = AssSubtitleBuilder.Layout(s, VideoWidth, VideoHeight, style);
        // 改行位置は書き出しと同じ（アプリで決めた位置）。行は左右の余白の内側に収めてあるので、画面の幅で中央に揃える。
        var lines = AssSubtitleBuilder.Lines(s, layout, VideoWidth, family, WpfSubtitleTextMeasurer.Instance);
        var face = new Typeface(new FontFamily(family + ", Meiryo, Yu Gothic UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var textBrush = Brush(colors.TextColor);
        var edgeBrush = Brush(colors.EdgeColor);
        var ft = new FormattedText(string.Join("\n", lines), CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight, face, layout.FontSizePx, textBrush, 1.0)
        {
            MaxTextWidth = VideoWidth,
            TextAlignment = TextAlignment.Center,
        };
        double x = 0;
        double y = s.Position switch
        {
            SubtitlePosition.Top => layout.MarginVerticalPx,
            SubtitlePosition.Middle => layout.CenterY - ft.Height / 2,
            _ => VideoHeight - layout.MarginVerticalPx - ft.Height,
        };
        var geometry = ft.BuildGeometry(new Point(x, y));
        if (colors.Box)
        {
            // 帯: 字幕全体を 1 枚の箱で囲む（書き出しの BorderStyle 4 と同じ。余白は OutlinePx）。
            var b = geometry.Bounds;
            if (!b.IsEmpty)
            {
                b.Inflate(layout.OutlinePx, layout.OutlinePx);
                dc.DrawRectangle(edgeBrush, null, b);
            }
        }
        else
        {
            // 縁取り（黒）を先に太く描き、その上に文字を塗る。
            dc.DrawGeometry(null, new Pen(edgeBrush, layout.OutlinePx * 2) { LineJoin = PenLineJoin.Round }, geometry);
        }
        dc.DrawGeometry(textBrush, null, geometry);
    }
}
