using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Recording;

/// <summary>
/// 範囲指定録画の縦横比固定の計算（v1.16）。規格サイズの選択・向きの切り替え・枠のドラッグ・設定の復元・録画開始時の確定で共通に使う。
/// 固定中の大きさは常に「単位（横 32x18 / 縦 18x32）の整数倍 k」とする。単位が偶数なので H.264 用の偶数化で比率が崩れず、
/// 幅・高さを別々に切り詰めないよう、収まらない場合は k を減らして比例縮小する。座標はすべて仮想スクリーンの物理 px（負の座標もある）。
/// </summary>
public static class RegionAspect
{
    /// <summary>単位の大きさ（横 32x18 = 16:9、縦 18x32 = 9:16）。</summary>
    public static (int Width, int Height) Unit(RegionOrientation orientation)
        => orientation == RegionOrientation.Portrait ? (18, 32) : (32, 18);

    /// <summary>規格サイズの倍率 k（1280 系 = 40、1920 系 = 60）。自由サイズは 0。</summary>
    public static int PresetScale(RegionSizePreset preset) => preset switch
    {
        RegionSizePreset.Hd720 => 40,
        RegionSizePreset.Hd1080 => 60,
        _ => 0,
    };

    /// <summary>規格サイズの実寸（向きを反映。自由サイズは 0x0）。</summary>
    public static (int Width, int Height) PresetSize(RegionSizePreset preset, RegionOrientation orientation)
    {
        var (uw, uh) = Unit(orientation);
        int k = PresetScale(preset);
        return (uw * k, uh * k);
    }

    /// <summary>最小サイズ（<see cref="RecordingSettings.MinRegionSize"/>）を満たす最小の k（横・縦とも 4 = 128x72 / 72x128）。</summary>
    public static int MinScale(RegionOrientation orientation)
    {
        var (uw, uh) = Unit(orientation);
        int min = RecordingSettings.MinRegionSize;
        return Math.Max(CeilDiv(min, uw), CeilDiv(min, uh));
    }

    /// <summary>幅 availW・高さ availH に収まる最大の k（最小の k を下回ることがある）。</summary>
    public static int MaxScale(int availW, int availH, RegionOrientation orientation)
    {
        var (uw, uh) = Unit(orientation);
        return Math.Min(Math.Max(0, availW) / uw, Math.Max(0, availH) / uh);
    }

    /// <summary>大きさが固定の条件（単位の整数倍・最小サイズ以上）を満たしているか。</summary>
    public static bool IsValidSize(int width, int height, RegionOrientation orientation)
    {
        var (uw, uh) = Unit(orientation);
        if (width <= 0 || width % uw != 0) return false;
        int k = width / uw;
        return height == k * uh && k >= MinScale(orientation);
    }

    /// <summary>k 倍の大きさで、中心 (cx, cy) をなるべく保ち、bounds の内側へ位置を寄せる（大きさは変えない）。</summary>
    public static CaptureRegion Place(int cx, int cy, int k, RegionOrientation orientation, CaptureRegion bounds)
    {
        var (uw, uh) = Unit(orientation);
        int w = uw * k, h = uh * k;
        return ClampPosition(new CaptureRegion(cx - w / 2, cy - h / 2, w, h), bounds);
    }

    /// <summary>位置だけを bounds の内側へ寄せる（大きさは変えない。bounds より大きければ左上をそろえる）。</summary>
    public static CaptureRegion ClampPosition(CaptureRegion region, CaptureRegion bounds)
    {
        int x = region.Width >= bounds.Width ? bounds.X : Math.Clamp(region.X, bounds.X, bounds.Right - region.Width);
        int y = region.Height >= bounds.Height ? bounds.Y : Math.Clamp(region.Y, bounds.Y, bounds.Bottom - region.Height);
        return region with { X = x, Y = y };
    }

    /// <summary>
    /// 倍率 k を bounds に収まる範囲・最小以上へ制限する。bounds が最小より小さい場合だけ、収まる最大（1 以上）を使う。
    /// </summary>
    private static int LimitScale(int k, int maxK, RegionOrientation orientation)
    {
        int minK = MinScale(orientation);
        if (maxK < minK) return Math.Max(1, maxK);
        return Math.Clamp(k, minK, maxK);
    }

    /// <summary>
    /// 規格サイズを選んだときの範囲。今の枠の中心を保ち、monitor（枠のあるモニター）に収まらなければ比率を保って縮める。
    /// Shrunk は規格サイズより小さくした場合 true。
    /// </summary>
    public static (CaptureRegion Region, bool Shrunk) ApplyPreset(CaptureRegion current, RegionSizePreset preset, RegionOrientation orientation, CaptureRegion monitor)
    {
        int want = PresetScale(preset);
        if (want <= 0) throw new ArgumentException("自由サイズには規格の大きさがありません。", nameof(preset));
        int k = LimitScale(want, MaxScale(monitor.Width, monitor.Height, orientation), orientation);
        var (cx, cy) = Center(current);
        return (Place(cx, cy, k, orientation, monitor), k < want);
    }

    /// <summary>
    /// 向きを切り替えたときの範囲。今の枠の幅と高さを入れ替え（固定中は同じ k のまま単位だけ入れ替わる）、中心を保つ。
    /// bounds に収まらなければ比率を保って縮め、位置を寄せる。Shrunk は縮めた場合 true。
    /// </summary>
    public static (CaptureRegion Region, bool Shrunk) Rotate(CaptureRegion current, RegionOrientation to, CaptureRegion bounds)
    {
        var from = to == RegionOrientation.Portrait ? RegionOrientation.Landscape : RegionOrientation.Portrait;
        int k0 = ScaleOf(current, from);
        int k = LimitScale(k0, MaxScale(bounds.Width, bounds.Height, to), to);
        var (cx, cy) = Center(current);
        return (Place(cx, cy, k, to, bounds), k < k0);
    }

    /// <summary>
    /// 任意の範囲を固定の条件に合わせる（設定の復元・録画開始時）。条件を満たし bounds に収まっていればそのまま返す。
    /// そうでなければ中心を保って最も近い k にし、bounds に収まるよう比例縮小して位置を寄せる。
    /// </summary>
    public static CaptureRegion Normalize(CaptureRegion region, RegionOrientation orientation, CaptureRegion bounds)
    {
        if (IsValidSize(region.Width, region.Height, orientation) && region.Width <= bounds.Width && region.Height <= bounds.Height)
            return ClampPosition(region, bounds);
        int k = LimitScale(ScaleOf(region, orientation), MaxScale(bounds.Width, bounds.Height, orientation), orientation);
        var (cx, cy) = Center(region);
        return Place(cx, cy, k, orientation, bounds);
    }

    /// <summary>範囲に最も近い k（幅・高さの比の平均を四捨五入。最小は 1）。</summary>
    public static int ScaleOf(CaptureRegion region, RegionOrientation orientation)
    {
        var (uw, uh) = Unit(orientation);
        double k = (region.Width / (double)uw + region.Height / (double)uh) / 2;
        return Math.Max(1, (int)Math.Round(k, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// 固定中のつまみのドラッグ。start はドラッグ開始時の範囲（固定の条件を満たす）、(dx, dy) はつまみの方向
    /// （-1 = 左 / 上、1 = 右 / 下、0 = その軸は動かさない）、(mx, my) はマウスの移動量（物理 px）。
    /// 四隅は反対側の角を、左右の辺は反対側の辺と上下の中心を、上下の辺は反対側の辺と左右の中心を固定して比率を保つ。
    /// bounds（仮想スクリーン）の端や最小サイズでは、比率を崩さず拡縮できる限界で止める。
    /// </summary>
    public static CaptureRegion Resize(CaptureRegion start, int dx, int dy, int mx, int my, RegionOrientation orientation, CaptureRegion bounds)
    {
        if (dx == 0 && dy == 0) return start;
        var (uw, uh) = Unit(orientation);
        // 望む k: 動かした軸の変化から求める。四隅は単位あたりの変化が大きい方の軸に合わせる。
        double kw = (start.Width + dx * mx) / (double)uw;
        double kh = (start.Height + dy * my) / (double)uh;
        double k0 = start.Width / (double)uw;
        double want = dx == 0 ? kh : dy == 0 ? kw : Math.Abs(kw - k0) >= Math.Abs(kh - k0) ? kw : kh;
        int k = (int)Math.Round(want, MidpointRounding.AwayFromZero);

        // 固定する辺・中心から、広げられる幅・高さの上限を決める。
        var (cx, cy) = Center(start);
        int availW = dx > 0 ? bounds.Right - start.X
            : dx < 0 ? start.Right - bounds.X
            : 2 * Math.Min(cx - bounds.X, bounds.Right - cx);
        int availH = dy > 0 ? bounds.Bottom - start.Y
            : dy < 0 ? start.Bottom - bounds.Y
            : 2 * Math.Min(cy - bounds.Y, bounds.Bottom - cy);
        int maxK = MaxScale(availW, availH, orientation);
        int minK = MinScale(orientation);
        // 開始時の枠が既に上限を超えている（画面構成の変化など）場合は、大きくしないだけにする。
        k = maxK < minK ? Math.Min(k, ScaleOf(start, orientation)) : Math.Clamp(k, minK, maxK);
        k = Math.Max(1, k);

        int w = uw * k, h = uh * k;
        int x = dx > 0 ? start.X : dx < 0 ? start.Right - w : cx - w / 2;
        int y = dy > 0 ? start.Y : dy < 0 ? start.Bottom - h : cy - h / 2;
        return new CaptureRegion(x, y, w, h);
    }

    /// <summary>自由サイズのつまみのドラッグ（v1.15 までと同じ。幅・高さを別々に変え、最小サイズで止める）。画面内への補正は呼び出し側で行う。</summary>
    public static CaptureRegion ResizeFree(CaptureRegion start, int dx, int dy, int mx, int my)
    {
        int x = start.X, y = start.Y, w = start.Width, h = start.Height;
        int min = RecordingSettings.MinRegionSize;
        if (dx < 0) { var nw = Math.Max(min, w - mx); x = start.Right - nw; w = nw; }
        else if (dx > 0) w = Math.Max(min, w + mx);
        if (dy < 0) { var nh = Math.Max(min, h - my); y = start.Bottom - nh; h = nh; }
        else if (dy > 0) h = Math.Max(min, h + my);
        return new CaptureRegion(x, y, w, h);
    }

    private static (int X, int Y) Center(CaptureRegion r) => (r.X + r.Width / 2, r.Y + r.Height / 2);

    private static int CeilDiv(int a, int b) => (a + b - 1) / b;
}
