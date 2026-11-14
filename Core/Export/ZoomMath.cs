using System.Globalization;
using System.Text;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Export;

/// <summary>
/// 拡大 1 つ（v1.21）。時刻は出力動画の秒、Factor は拡大後の倍率 Z（1 より大きい）、X / Y は拡大後に映す範囲の左上（書き出す映像の px）。
/// 範囲の大きさは「映像の大きさ / Factor」（縦横比は映像と同じ）。
/// </summary>
public sealed record ZoomWindow(double Start, double End, double Factor, double X, double Y)
{
    public double Duration => End - Start;
}

/// <summary>ある時刻の拡大の状態。Zoom は倍率（1 なら等倍）、X / Y は映す範囲の左上（映像の px）。</summary>
public readonly record struct ZoomState(double Zoom, double X, double Y)
{
    public static readonly ZoomState None = new(1, 0, 0);
    public bool IsIdentity => Math.Abs(Zoom - 1) < 1e-9;
}

/// <summary>
/// 拡大の動き（v1.21。設計 7.2）。プレビューと書き出し（ffmpeg の zoompan の式）で同じ式を使う。
/// 表示時間 [S, E] の最初の RampSeconds 秒で寄り、最後の RampSeconds 秒で戻る（表示時間が 3 × RampSeconds 未満なら 3 分の 1 ずつ）。
/// 進み具合 r(t) = clip((t−S)/ramp, 0, 1) − clip((t−(E−ramp))/ramp, 0, 1)（0→1→0 の台形。表示時間の外では 0）。
/// 倍率 z(t) = 1 + (Z − 1)·r(t)。左上 x(t) = X·(1 − 1/z)/(1 − 1/Z)（y も同じ）。寄る間、注目点（範囲の中の同じ点）が動かない。
/// 拡大どうしの表示時間は重ならない（ProjectEditor で拒否）ため、複数の拡大は和で書ける。
/// </summary>
public static class ZoomMath
{
    /// <summary>寄る・戻るのにかける秒数。</summary>
    public const double RampSeconds = 0.3;

    /// <summary>これ以下の倍率は拡大として扱わない（式の 1 − 1/Z が 0 になるのを避ける）。</summary>
    private const double MinEffectiveFactor = 1.0001;

    /// <summary>寄る・戻るのにかける秒数（表示時間が短いときは 3 分の 1。ミリ秒に丸める）。</summary>
    public static double Ramp(ZoomWindow w)
    {
        var d = Math.Max(0, w.Duration);
        return d < RampSeconds * 3 ? Math.Round(d / 3, 3) : RampSeconds;
    }

    /// <summary>拡大として使える窓か（長さがあり、倍率が 1 より大きい）。</summary>
    public static bool IsEffective(ZoomWindow w)
        => w.End > w.Start && w.Factor >= MinEffectiveFactor && !double.IsNaN(w.X) && !double.IsNaN(w.Y) && Ramp(w) > 0;

    /// <summary>進み具合 r(t)（0～1）。表示時間の外では 0。</summary>
    public static double Progress(ZoomWindow w, double t)
    {
        if (!IsEffective(w) || t < w.Start || t > w.End) return 0;
        var ramp = Ramp(w);
        return Clip((t - w.Start) / ramp) - Clip((t - (w.End - ramp)) / ramp);
    }

    /// <summary>倍率 z における、左上の位置の割合（1 − 1/z）/（1 − 1/Z）。z = 1 で 0、z = Z で 1。</summary>
    public static double FocusFraction(double factor, double zoom)
        => factor < MinEffectiveFactor ? 0 : (1 - 1 / zoom) / (1 - 1 / factor);

    /// <summary>出力時刻 t の拡大の状態（プレビュー用）。どの拡大の表示時間にも入らなければ等倍。</summary>
    public static ZoomState At(IEnumerable<ZoomWindow> windows, double outputTime)
    {
        double zoom = 1, cx = 0, cy = 0;
        foreach (var w in windows)
        {
            if (!IsEffective(w)) continue;
            zoom += (w.Factor - 1) * Progress(w, outputTime);
            if (outputTime >= w.Start && outputTime <= w.End)
            {
                // 式と同じ: (Σ X·g/(1−1/Z))·(1−1/z)
                cx += w.X / (1 - 1 / w.Factor);
                cy += w.Y / (1 - 1 / w.Factor);
            }
        }
        var k = 1 - 1 / zoom;
        return new ZoomState(zoom, cx * k, cy * k);
    }

    /// <summary>1 つの拡大の状態（試験・プレビュー用）。</summary>
    public static ZoomState At(ZoomWindow window, double outputTime) => At([window], outputTime);

    // ---------------- ffmpeg の式（zoompan） ----------------

    /// <summary>
    /// zoompan フィルタ（入力時刻 it を使う。d=1 で 1 コマ → 1 コマ、s は映像の大きさ、fps は映像のフレームレート）。
    /// 有効な拡大が無ければ null（フィルタを足さない）。
    /// </summary>
    public static string? ZoomPanFilter(IEnumerable<ZoomWindow> windows, int width, int height, string fpsText)
    {
        var list = windows.Where(IsEffective).OrderBy(w => w.Start).ToList();
        if (list.Count == 0) return null;
        return $"zoompan=z='{ZoomExpression(list)}':x='{PositionExpression(list, horizontal: true)}':y='{PositionExpression(list, horizontal: false)}'"
            + string.Create(CultureInfo.InvariantCulture, $":d=1:s={width}x{height}:fps={fpsText}");
    }

    /// <summary>倍率の式 1+(Z1-1)*(r1)+(Z2-1)*(r2)…（時刻の変数は it）。</summary>
    public static string ZoomExpression(IReadOnlyList<ZoomWindow> windows, string time = "it")
    {
        var sb = new StringBuilder("1");
        foreach (var w in windows.Where(IsEffective))
        {
            var ramp = Ramp(w);
            sb.Append(CultureInfo.InvariantCulture,
                $"+{N(w.Factor - 1)}*(clip(({time}-{S(w.Start)})/{S(ramp)},0,1)-clip(({time}-{S(w.End - ramp)})/{S(ramp)},0,1))");
        }
        return sb.ToString();
    }

    /// <summary>左上の式 (X1*between(it,S1,E1)/(1-1/Z1)+…)*(1-1/zoom)（zoom は zoompan が求めた現在の倍率）。</summary>
    public static string PositionExpression(IReadOnlyList<ZoomWindow> windows, bool horizontal, string time = "it")
    {
        var terms = windows.Where(IsEffective).Select(w => string.Create(CultureInfo.InvariantCulture,
            $"{N(horizontal ? w.X : w.Y)}*between({time},{S(w.Start)},{S(w.End)})/(1-1/{N(w.Factor)})")).ToList();
        return terms.Count == 0 ? "0" : $"({string.Join('+', terms)})*(1-1/zoom)";
    }

    private static double Clip(double v) => Math.Clamp(v, 0, 1);

    private static string S(double seconds) => TimeFormat.ToFFmpegSeconds(seconds);

    /// <summary>式に書く数（小数第 4 位まで。負の数は括弧で囲む）。</summary>
    private static string N(double v)
    {
        var s = Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);
        return v < 0 ? $"({s})" : s;
    }
}
