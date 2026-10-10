using System.Text.Json.Serialization;

namespace ManualVideoStudio.Core.Models;

/// <summary>
/// 動画に重ねる図形のまとまり（v1.6）。1 回の編集で描いた図形（四角形・矢印・直線・文字・モザイク）を、同じ表示時間でまとめて持つ。
/// 時刻は字幕と同じく出力動画（カット適用後）の秒。図形の座標は注釈と同じく基準画像（編集時に切り出した動画の 1 コマ）の px で、
/// 書き出す映像の大きさに合わせて比例変換する。図形は画面の動きに追従しない（その位置に固定して表示する）。
/// </summary>
public sealed class ShapeGroup
{
    public string Id { get; set; } = CutSegment.NewId();
    /// <summary>表示開始（出力動画の秒）。</summary>
    public double Start { get; set; }
    /// <summary>表示終了（出力動画の秒。この時刻は含まない）。</summary>
    public double End { get; set; }
    /// <summary>図形（リストの後ろほど手前）。モザイクは他の図形より下に描く。</summary>
    public List<Annotation> Shapes { get; set; } = [];
    /// <summary>図形の座標の基準画像の幅（px）。</summary>
    public int ImageWidth { get; set; }
    /// <summary>図形の座標の基準画像の高さ（px）。</summary>
    public int ImageHeight { get; set; }

    [JsonIgnore]
    public double Duration => End - Start;

    [JsonIgnore]
    public bool HasMosaic => Shapes.Any(s => s is MosaicAnnotation);

    /// <summary>一覧・タイムラインに出す内容（例: 「四角形・矢印 2」）。</summary>
    [JsonIgnore]
    public string Summary => SummaryOf(Shapes);

    public static string SummaryOf(IEnumerable<Annotation> shapes)
    {
        var parts = shapes.GroupBy(s => s.Kind).OrderBy(g => Order(g.Key))
            .Select(g => g.Count() > 1 ? $"{Label(g.Key)} {g.Count()}" : Label(g.Key)).ToList();
        return parts.Count == 0 ? "（図形なし）" : string.Join("・", parts);
    }

    private static int Order(AnnotationKind k) => k switch
    {
        AnnotationKind.Rectangle => 0,
        AnnotationKind.Arrow => 1,
        AnnotationKind.Line => 2,
        AnnotationKind.Text => 3,
        _ => 4,
    };

    private static string Label(AnnotationKind k) => k switch
    {
        AnnotationKind.Rectangle => "四角形",
        AnnotationKind.Arrow => "矢印",
        AnnotationKind.Line => "直線",
        AnnotationKind.Text => "文字",
        _ => "モザイク",
    };
}
