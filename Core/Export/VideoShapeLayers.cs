using ManualVideoStudio.Core.Annotations;
using ManualVideoStudio.Core.Editing;
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

/// <summary>強調するクリック 1 つ（v1.21）。OutputTime は出力動画の秒、X / Y は書き出す映像の px（輪の中心）。</summary>
public sealed record ClickMark(double OutputTime, double X, double Y);

/// <summary>クリックの輪を出す時間 [Start, End)（出力動画の秒）と中心（映像の px）。</summary>
public sealed record ClickWindow(double Start, double End, double X, double Y);

/// <summary>
/// 書き出しで動画に重ねる図形（v1.6）。重ねる順は「モザイク（すべて）→ 画像（まとまりの順）→ クリックの強調 → 拡大 → 字幕」。
/// 図形が無ければ Empty で、書き出しの引数は図形機能の追加前と同じになる。
/// 拡大（Zooms）とクリックの強調（Clicks・ClickRingPng）は v1.21。init で足す（例: <c>layers with { Zooms = ..., Clicks = ..., ClickRingPng = ... }</c>）。
/// </summary>
public sealed record VideoShapeLayers(IReadOnlyList<ShapeOverlayImage> Images, IReadOnlyList<ShapeMosaic> Mosaics)
{
    public static readonly VideoShapeLayers Empty = new([], []);

    /// <summary>拡大（出力動画の秒・書き出す映像の px。<see cref="ZoomsOf"/> で作る）。</summary>
    public IReadOnlyList<ZoomWindow> Zooms { get; init; } = [];

    /// <summary>強調するクリック（出力動画の秒・映像の px。<see cref="ClicksOf"/> で作る）。ClickRingPng が無ければ使わない。</summary>
    public IReadOnlyList<ClickMark> Clicks { get; init; } = [];

    /// <summary>輪の半径（書き出す映像の px。<see cref="ClickRadiusFor"/>）。描画（Imaging）とプレビューで使う。</summary>
    public double ClickRadius { get; init; }

    /// <summary>
    /// クリックの輪の画像（PNG。透明な背景に輪を 1 つ。輪の中心が画像の中心になるよう、正方形・偶数の大きさで描く）。
    /// 書き出しでは画像の中心をクリックの位置に合わせて重ねる。無ければクリックの強調をしない。
    /// </summary>
    public byte[]? ClickRingPng { get; init; }

    /// <summary>クリックの強調を書き出すか（クリックがあり、輪の画像がある）。</summary>
    public bool HasClicks => Clicks.Count > 0 && ClickRingPng is { Length: > 0 };

    public bool IsEmpty => Images.Count == 0 && Mosaics.Count == 0 && Zooms.Count == 0 && !HasClicks;

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

    // ---------------- 拡大（v1.21） ----------------

    /// <summary>
    /// まとまりの拡大を、書き出す映像（videoWidth × videoHeight。元動画の大きさ）の座標の <see cref="ZoomWindow"/> へ変換する（モザイクと同じ比例変換）。
    /// 倍率は「映像の幅 / 範囲の幅」を ×1.1～×4 に収めた値、左上は範囲が映像の外へ出ないよう収める。表示時間の順。
    /// </summary>
    public static IReadOnlyList<ZoomWindow> ZoomsOf(IEnumerable<ShapeGroup> groups, int videoWidth, int videoHeight)
    {
        var list = new List<ZoomWindow>();
        if (videoWidth <= 0 || videoHeight <= 0) return list;
        foreach (var g in groups)
        {
            if (g.Zoom is not { } zoom || g.End <= g.Start) continue;
            if (AnnotationGeometry.FitToImage([zoom], g.ImageWidth, g.ImageHeight, videoWidth, videoHeight)[0] is not ZoomAnnotation z || z.Width <= 0) continue;
            var factor = Math.Clamp(videoWidth / z.Width, ZoomAnnotation.MinFactor, ZoomAnnotation.MaxFactor);
            var x = Math.Clamp(z.X, 0, videoWidth - videoWidth / factor);
            var y = Math.Clamp(z.Y, 0, Math.Max(0, videoHeight - videoHeight / factor));
            list.Add(new ZoomWindow(g.Start, g.End, factor, x, y));
        }
        list.Sort((a, b) => a.Start.CompareTo(b.Start));
        return list;
    }

    // ---------------- クリックの強調（v1.21） ----------------

    /// <summary>輪を出し始める時刻（クリックの何秒前か）。</summary>
    public const double ClickLeadSeconds = 0.05;
    /// <summary>輪を出し終える時刻（クリックの何秒後か）。表示は合わせて 0.5 秒。</summary>
    public const double ClickTailSeconds = 0.45;

    /// <summary>輪の半径（px）。動画の高さ 1080px のときの半径（小 28・中 40・大 56）を、映像の高さに比例させる。</summary>
    public static double ClickRadiusFor(ClickHighlightSize size, int videoHeight)
        => videoHeight <= 0 ? 0 : ClickHighlightSettings.RadiusAt1080(size) * videoHeight / 1080.0;

    /// <summary>
    /// 元動画の時刻のクリック（録画の操作記録。位置は動画の px）を、出力動画の時刻へ写す。削除される位置のクリックと、
    /// 映像（videoWidth × videoHeight）の外のクリックは除く。出力時刻の順。
    /// </summary>
    public static IReadOnlyList<ClickMark> ClicksOf(IEnumerable<(double SourceTime, double X, double Y)> clicks, IReadOnlyList<TimeRange> keepRanges,
        int videoWidth, int videoHeight)
    {
        var list = new List<ClickMark>();
        foreach (var (t, x, y) in clicks)
        {
            if (double.IsNaN(t) || double.IsNaN(x) || double.IsNaN(y)) continue;
            if (x < 0 || y < 0 || (videoWidth > 0 && x >= videoWidth) || (videoHeight > 0 && y >= videoHeight)) continue;
            if (EditTimelineCalculator.MapToOutput(t, keepRanges) is not { } ot) continue;
            list.Add(new ClickMark(ot, x, y));
        }
        list.Sort((a, b) => a.OutputTime.CompareTo(b.OutputTime));
        return list;
    }

    /// <summary>
    /// 輪を出す時間。クリックの 0.05 秒前から 0.5 秒間で、次のクリックの輪と重なる場合は次の輪が出る手前で切る（同時に 2 つは出さない）。
    /// 長さが 0 になったもの（同じ時刻のクリック）は除く。clicks は出力時刻の順であること（<see cref="ClicksOf"/>）。
    /// </summary>
    public static IReadOnlyList<ClickWindow> ClickWindowsOf(IReadOnlyList<ClickMark> clicks)
    {
        var list = new List<ClickWindow>();
        for (int i = 0; i < clicks.Count; i++)
        {
            var c = clicks[i];
            double start = Math.Round(Math.Max(0, c.OutputTime - ClickLeadSeconds), 3);
            double end = Math.Round(c.OutputTime + ClickTailSeconds, 3);
            if (i + 1 < clicks.Count) end = Math.Min(end, Math.Round(Math.Max(0, clicks[i + 1].OutputTime - ClickLeadSeconds), 3));
            if (end - start >= 0.001) list.Add(new ClickWindow(start, end, c.X, c.Y));
        }
        return list;
    }

    /// <summary>PNG の幅・高さ（IHDR から読む。PNG でなければ null）。</summary>
    public static (int Width, int Height)? PngSize(byte[]? png)
    {
        if (png is null || png.Length < 24) return null;
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (!png.AsSpan(0, 8).SequenceEqual(signature)) return null;
        int w = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        int h = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        return w > 0 && h > 0 ? (w, h) : null;
    }
}
