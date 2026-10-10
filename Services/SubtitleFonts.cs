using System.Text;
using System.Windows;
using System.Windows.Media;
using ManualVideoStudio.Core.Subtitles;

namespace ManualVideoStudio.Services;

/// <summary>
/// インストール済みフォントの一覧（字幕のフォント選択と幅の測定に使う。v1.5）。
/// 書体を 1 つずつ開くため数秒かかるので、起動時に裏で読み始める（<see cref="LoadAsync"/>）。
/// </summary>
public static class SubtitleFonts
{
    private static readonly object Gate = new();
    private static Task<IReadOnlyList<FontChoice>>? _load;

    public static Task<IReadOnlyList<FontChoice>> LoadAsync()
    {
        lock (Gate)
        {
            return _load ??= Task.Run<IReadOnlyList<FontChoice>>(() =>
            {
                try { return FontCatalog.LoadInstalledFonts(); }
                catch (Exception) { return []; }
            });
        }
    }

    /// <summary>読み終わっていれば一覧、まだなら null。</summary>
    public static IReadOnlyList<FontChoice>? Loaded
    {
        get
        {
            lock (Gate) return _load is { IsCompletedSuccessfully: true } t ? t.Result : null;
        }
    }
}

/// <summary>
/// 字幕の幅を、実際のフォントの文字送り幅で測る（折り返し位置を決めるため。v1.5）。プレビューと書き出しで同じものを使う。
/// フォントはインストール済みフォントの一覧（GDI 名）から書体のファイルを引き、無ければ WPF のファミリー名で探す。
/// フォントに無い文字はメイリオ → 見積もり（全角 = 文字サイズ、半角 = × 0.6）で測る。カーニングは見ない。
/// </summary>
public sealed class WpfSubtitleTextMeasurer : ISubtitleTextMeasurer
{
    public static readonly WpfSubtitleTextMeasurer Instance = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, GlyphTypeface?> _faces = new(StringComparer.OrdinalIgnoreCase);

    public double Measure(string text, string fontFamily, double fontSizePx)
    {
        lock (_gate)
        {
            var face = Resolve(fontFamily);
            var fallback = Resolve("Meiryo");
            double w = 0;
            foreach (var r in text.EnumerateRunes())
            {
                if (Advance(face, r) is { } a) w += a;
                else if (Advance(fallback, r) is { } b) w += b;
                else w += EstimatedTextMeasurer.Instance.Measure(r.ToString(), fontFamily, 1);
            }
            return w * fontSizePx;
        }
    }

    private static double? Advance(GlyphTypeface? face, Rune r)
    {
        if (face is null) return null;
        try
        {
            return face.CharacterToGlyphMap.TryGetValue(r.Value, out var g) ? face.AdvanceWidths[g] : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private GlyphTypeface? Resolve(string family)
    {
        if (_faces.TryGetValue(family, out var cached)) return cached;
        GlyphTypeface? face = null;
        var fonts = SubtitleFonts.Loaded;
        try
        {
            if (fonts is not null && FontCatalog.Find(fonts, family)?.RegularFace is { } uri)
                face = new GlyphTypeface(uri);
            else if (new Typeface(new FontFamily(family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out var g))
                face = g;
        }
        catch (Exception)
        {
            face = null;
        }
        // 一覧を読み終わる前に見つからなかった名前は、読み終わった後にもう一度探す。
        if (face is not null || fonts is not null) _faces[family] = face;
        return face;
    }
}
