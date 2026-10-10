using ManualVideoStudio.Core.Annotations;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Export;

/// <summary>
/// 図形のまとまりの四角形・矢印・直線・文字を描いた透明な画像（PNG）。まとまり 1 つにつき 1 枚で、図形の範囲だけを切り出す。
/// X / Y は書き出す映像（元動画の大きさ）での左上。描画は WPF（Imaging）で行い、Core は ffmpeg に重ねるだけ。
/// </summary>
public sealed record ShapeOverlayImage(double Start, double End, int X, int Y, byte[] Png);

/// <summary>
/// 書き出しで先頭に付ける黒背景タイトルの画像（v1.15）。Png は書き出す映像と同じ大きさ（黒背景・白文字）で、プレビューと同じ描画処理で作る。
/// FadeSeconds は本編の冒頭フェードの長さ（0 ならフェードなし。本編が短ければ縮めた値）。
/// </summary>
public sealed record VideoTitleImage(int Seconds, double FadeSeconds, byte[] Png);

/// <summary>書き出しで毎コマかけるモザイク（書き出す映像の px。範囲は映像内に収めてある）。</summary>
public sealed record ShapeMosaic(double Start, double End, int X, int Y, int Width, int Height, int BlockSize);

/// <summary>
/// 書き出しで動画に重ねる図形（v1.6）。重ねる順は「モザイク（すべて）→ 画像（まとまりの順）→ 字幕」。
/// 図形が無ければ Empty で、書き出しの引数は図形機能の追加前と同じになる。
/// </summary>
public sealed record VideoShapeLayers(IReadOnlyList<ShapeOverlayImage> Images, IReadOnlyList<ShapeMosaic> Mosaics)
{
    public static readonly VideoShapeLayers Empty = new([], []);

    public bool IsEmpty => Images.Count == 0 && Mosaics.Count == 0;

    /// <summary>
    /// まとまりのモザイクを、書き出す映像（videoWidth × videoHeight。元動画の大きさ）の座標へ変換する。
    /// 範囲は MosaicProcessor と同じく端数を外側へ広げ、映像の外は切り取る。マスの大きさは注釈と同じ最小値を守る。
    /// </summary>
    public static IReadOnlyList<ShapeMosaic> MosaicsOf(IEnumerable<ShapeGroup> groups, int videoWidth, int videoHeight)
    {
        var list = new List<ShapeMosaic>();
        if (videoWidth <= 0 || videoHeight <= 0) return list;
        foreach (var g in groups)
        {
            var fitted = AnnotationGeometry.FitToImage(g.Shapes.OfType<MosaicAnnotation>(), g.ImageWidth, g.ImageHeight, videoWidth, videoHeight);
            foreach (var m in fitted.OfType<MosaicAnnotation>())
            {
                if (MosaicProcessor.PixelRegion(m, videoWidth, videoHeight) is not { } r) continue;
                list.Add(new ShapeMosaic(g.Start, g.End, r.X, r.Y, r.Width, r.Height, Math.Max(AnnotationStyle.MinMosaicBlock, m.BlockSize)));
            }
        }
        return list;
    }
}
