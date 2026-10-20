using System.Globalization;
using System.Text;
using ManualVideoStudio.Core.Analysis;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Recording;

namespace ManualVideoStudio.Core.FFmpeg;

/// <summary>書き出しコマンド。フィルタが長い場合は FilterScript をファイルに書いて渡す。</summary>
public sealed record ExportCommand(IReadOnlyList<string> Arguments, string? FilterScript, string? FilterScriptPlaceholder)
{
    /// <summary>FilterScript を書き出したパスで置き換えた引数を返す。</summary>
    public IReadOnlyList<string> WithScriptPath(string path)
        => FilterScriptPlaceholder is null ? Arguments : Arguments.Select(a => a == FilterScriptPlaceholder ? path : a).ToList();
}

/// <summary>図形の画像 1 枚の重ね合わせ（v1.6）。File は ffmpeg の作業フォルダーからの相対名（拡張子なしの PNG）。</summary>
public sealed record ShapeOverlayInput(string File, double Start, double End, int X, int Y);

/// <summary>書き出しで重ねる図形（v1.6）。画像は入力 1 番から順に -f png_pipe で読む。</summary>
public sealed record ShapeFilterPlan(IReadOnlyList<ShapeOverlayInput> Images, IReadOnlyList<ShapeMosaic> Mosaics)
{
    public bool IsEmpty => Images.Count == 0 && Mosaics.Count == 0;
}

/// <summary>
/// 書き出しで先頭に付ける黒背景タイトル（v1.15）。File は ffmpeg の作業フォルダーからの相対名（拡張子なしの PNG。完成動画と同じ大きさ）。
/// Seconds は表示秒数、FadeSeconds は本編の冒頭フェード（0 ならフェードなし）、Fps は完成動画のフレームレート。
/// </summary>
public sealed record TitleFilterPlan(string File, int Seconds, double FadeSeconds, double Fps);

public sealed record VideoEncodeOptions
{
    public int Crf { get; init; } = 22;
    public string Preset { get; init; } = "medium";
    public int AudioBitrateKbps { get; init; } = 192;
}

/// <summary>
/// ffmpeg 引数の生成（純粋ロジック）。UI・プロセス実行から独立させ Unit Test で検証する。
/// すべて 1 要素 = 1 引数で返し、パスの空白・日本語・記号をエスケープせずそのまま渡す。
/// </summary>
public static class FFmpegArguments
{
    /// <summary>コマンドライン上限（32767）に余裕を持たせた、インライン filter_complex の最大長。</summary>
    public const int MaxInlineFilterLength = 20_000;
    public const string FilterScriptPlaceholder = "{{FILTER_SCRIPT}}";
    /// <summary>
    /// 録画の区間の一覧ファイル名（作業フォルダー内）。.txt は作成と同時に暗号化される環境があるため拡張子を付けない（v1.4.1）。
    /// concat demuxer は -f concat で明示するので拡張子は不要。
    /// </summary>
    public const string ConcatListFileName = "segments";

    /// <summary>長いフィルタを書き出す一時ファイルのパス。一覧ファイルと同じ理由で拡張子を付けない（v1.4.1）。</summary>
    public static string NewFilterScriptPath()
        => Path.Combine(AppTemp.Root, $"manualvideostudio-filter-{Guid.NewGuid():N}");

    private static string S(double seconds) => TimeFormat.ToFFmpegSeconds(seconds);
    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 指定時刻の 1 フレームを画像にする。入力側 -ss（ffmpeg 2.1 以降は再エンコード時に正確なシーク）。
    /// maxWidth &gt; 0 のとき縮小（サムネイル用）。
    /// </summary>
    public static IReadOnlyList<string> FrameExtraction(string input, double time, string output, ImageFormat format,
        int jpegQuality = 2, int maxWidth = 0)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-ss", S(time), "-i", input,
            "-map", "0:v:0", "-frames:v", "1", "-an", "-sn",
        };
        var filters = new List<string>();
        if (maxWidth > 0) filters.Add($"scale='min({I(maxWidth)},iw)':-2:flags=lanczos");
        if (format == ImageFormat.Jpeg)
        {
            // 4:4:4 で色の付いた文字のにじみを避ける。
            filters.Add("format=yuvj444p");
            args.AddRange(filters.Count > 0 ? ["-vf", string.Join(',', filters)] : []);
            args.AddRange(["-c:v", "mjpeg", "-q:v", I(Math.Clamp(jpegQuality, 2, 31))]);
        }
        else
        {
            if (filters.Count > 0) args.AddRange(["-vf", string.Join(',', filters)]);
            args.AddRange(["-c:v", "png"]);
        }
        // 出力先は拡張子なし（キャッシュ・一時ファイル）の場合があるため、形式を明示する（v1.4.2）。
        args.AddRange(["-f", "image2", "-update", "1", output]);
        return args;
    }

    /// <summary>
    /// 編集済み動画の書き出し。残す区間を trim/atrim して concat する（正確なカット・再エンコード）。
    /// subtitleFile を指定すると、カット後の映像へ字幕（ASS）を焼き込む（ffmpeg の作業フォルダーからの相対名で渡すこと）。
    /// 字幕が無い場合の引数は字幕機能の追加前と同じ。進捗は -progress pipe:1 で stdout に出す。
    /// transitionSeconds &gt; 0 なら、つなぎ目にクロスフェードを入れる（<see cref="BuildTransitionFilter"/>。出力の長さは変わらない）。
    /// </summary>
    public static ExportCommand Export(string input, IReadOnlyList<TimeRange> keepRanges, VideoMetadata meta,
        string output, VideoEncodeOptions options, int ffmpegMajorVersion, string? subtitleFile = null, double transitionSeconds = 0,
        ShapeFilterPlan? shapes = null, TitleFilterPlan? title = null)
    {
        if (keepRanges.Count == 0) throw new ArgumentException("出力する区間がありません。", nameof(keepRanges));
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", input };
        AddShapeInputs(args, shapes);
        AddTitleInput(args, title);
        string? script = null;
        string? placeholder = null;

        bool oddSize = meta.Width % 2 != 0 || meta.Height % 2 != 0;
        // yuv420p は幅・高さが偶数である必要がある。縮小ではなく右端・下端 1px を切り落として文字をぼかさない。
        // 図形は切り落とした後の映像に重ね、その上に字幕を焼き込む。
        var post = ComposeVideoChain(oddSize ? EvenCrop : null, shapes, meta.Width & ~1, meta.Height & ~1,
            subtitleFile is null ? null : SubtitlesFilter(subtitleFile));
        bool hasShapes = shapes is { IsEmpty: false };

        if (title is not null)
        {
            // タイトルあり: 本編（カット・図形・字幕）を [outv] / [outa] まで作り、冒頭フェード → タイトルとの連結を続ける。
            // 字幕・図形の時刻は本編基準のまま（タイトルは連結で先頭に足すだけ）。
            string main;
            if (EditTimelineCalculator.IsIdentity(meta.Duration, keepRanges))
                main = $"[0:v:0]{post ?? "null"}[outv]" + (meta.HasAudio ? ";[0:a:0]anull[outa]" : "");
            else
                main = BuildJoinFilter(keepRanges, meta.HasAudio, post, transitionSeconds);
            var full = main + ";" + TitleStage(title, 1 + (shapes?.Images.Count ?? 0), meta.HasAudio, EditTimelineCalculator.OutputDuration(keepRanges),
                scale: null, outV: "[fv]", outA: "[fa]");
            (script, placeholder) = AddFilterComplex(args, full, ffmpegMajorVersion);
            args.AddRange(["-map", "[fv]"]);
            if (meta.HasAudio) args.AddRange(["-map", "[fa]"]);
        }
        else if (EditTimelineCalculator.IsIdentity(meta.Duration, keepRanges) && hasShapes)
        {
            // 図形の画像は別の入力のため、編集なしでも filter_complex で重ねる。
            (script, placeholder) = AddFilterComplex(args, $"[0:v:0]{post}[outv]", ffmpegMajorVersion);
            args.AddRange(["-map", "[outv]"]);
            if (meta.HasAudio) args.AddRange(["-map", "0:a:0"]);
        }
        else if (EditTimelineCalculator.IsIdentity(meta.Duration, keepRanges))
        {
            args.AddRange(["-map", "0:v:0"]);
            if (meta.HasAudio) args.AddRange(["-map", "0:a:0"]);
            if (post is not null) args.AddRange(["-vf", post]);
        }
        else
        {
            var filter = BuildJoinFilter(keepRanges, meta.HasAudio, post, transitionSeconds);
            if (filter.Length <= MaxInlineFilterLength)
            {
                args.AddRange(["-filter_complex", filter]);
            }
            else
            {
                script = filter;
                placeholder = FilterScriptPlaceholder;
                // FFmpeg 7 以降は "-/filter_complex <file>"、それ以前は -filter_complex_script。
                args.AddRange([ffmpegMajorVersion >= 7 ? "-/filter_complex" : "-filter_complex_script", placeholder]);
            }
            args.AddRange(["-map", "[outv]"]);
            if (meta.HasAudio) args.AddRange(["-map", "[outa]"]);
        }

        args.AddRange([
            "-c:v", "libx264", "-preset", options.Preset, "-crf", I(options.Crf),
            // 画面録画のスクロールに追随させる（開発メモ: me=umh 以上でないと merange は無視される）。
            "-x264-params", "me=umh:merange=64",
            "-pix_fmt", "yuv420p",
        ]);
        if (meta.HasAudio) args.AddRange(["-c:a", "aac", "-b:a", I(options.AudioBitrateKbps) + "k"]);
        else args.Add("-an");
        args.AddRange(["-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1"]);

        var ext = Path.GetExtension(output).ToLowerInvariant();
        if (ext is ".mp4" or ".m4v" or ".mov") args.AddRange(["-movflags", "+faststart"]);
        args.AddRange(["-progress", "pipe:1", "-nostats", "-stats_period", "0.5"]);
        args.Add(output);
        return new ExportCommand(args, script, placeholder);
    }

    /// <summary>幅・高さを偶数にする（右端・下端の 1px を切り落とす）。</summary>
    public const string EvenCrop = "crop=trunc(iw/2)*2:trunc(ih/2)*2:0:0";

    /// <summary>字幕（ASS）を焼き込むフィルタ。file は作業フォルダーからの相対名（英数字・記号 -_. のみ）。</summary>
    public static string SubtitlesFilter(string file)
    {
        if (file.Length == 0 || file.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException("字幕ファイル名は英数字と -_. だけにしてください（フィルタ記法のエスケープを避けるため）。", nameof(file));
        return $"subtitles=filename={file}";
    }

    /// <summary>
    /// 字幕の確認画像（v1.5）: 動画の 1 コマ（input が null なら灰色の画面）に字幕を焼き込んで PNG 1 枚にする。
    /// 実際に使われたフォントを stderr の fontselect で確かめるため loglevel は verbose。
    /// 出力は拡張子なしの一時ファイル（暗号化される拡張子を避ける）ため形式を明示する。
    /// </summary>
    public static IReadOnlyList<string> SubtitleSample(string? input, double time, int width, int height, string subtitleFile, string output)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "verbose", "-y" };
        if (input is null)
            args.AddRange(["-f", "lavfi", "-i", $"color=c=0x606060:s={I(width)}x{I(height)}:d=1"]);
        else
            args.AddRange(["-ss", S(time), "-i", input, "-map", "0:v:0"]);
        args.AddRange(["-vf", SubtitlesFilter(subtitleFile), "-frames:v", "1", "-an", "-sn", "-c:v", "png", "-f", "image2", "-update", "1", output]);
        return args;
    }

    private static string? JoinFilters(params string?[] filters)
    {
        var list = filters.Where(f => !string.IsNullOrEmpty(f)).ToList();
        return list.Count == 0 ? null : string.Join(',', list);
    }

    /// <summary>
    /// カット・字幕を反映した映像を GIF 用に整えるフィルタ（音声は使わない）。
    /// 順序: カット（trim/concat）→ 字幕の焼き込み → GIF 用の FPS → 縮小（拡大はしない）→ tail。
    /// 出力ラベルは [outv]。
    /// </summary>
    public static string BuildGifFilter(IReadOnlyList<TimeRange> keepRanges, VideoMetadata meta, string? subtitleFile, GifPreset preset, string tail,
        double transitionSeconds = 0, ShapeFilterPlan? shapes = null, TitleFilterPlan? title = null)
    {
        // 図形・字幕は元の大きさの映像に重ね、その後で GIF 用に FPS・大きさを下げる（MP4 と同じ見た目の比率）。
        var fpsFilter = string.Create(CultureInfo.InvariantCulture, $"fps={preset.Fps}");
        var scaleFilter = string.Create(CultureInfo.InvariantCulture,
            $"scale=w='min(iw,{preset.MaxWidth})':h='min(ih,{preset.MaxHeight})':force_original_aspect_ratio=decrease:flags=lanczos");
        var chain = ComposeVideoChain(null, shapes, meta.Width, meta.Height, JoinFilters(
            subtitleFile is null ? null : SubtitlesFilter(subtitleFile), fpsFilter, scaleFilter, title is null ? tail : null))!;
        var main = EditTimelineCalculator.IsIdentity(meta.Duration, keepRanges)
            ? $"[0:v:0]{chain}[outv]"
            : BuildJoinFilter(keepRanges, hasAudio: false, chain, transitionSeconds);
        if (title is null) return main;
        // タイトルあり: 縮小まで済ませた本編にフェードをかけ、同じ縮小をかけたタイトルを前に連結してから tail（パレット生成など）へ渡す。
        return ReplaceLast(main, "[outv]", "[tmain]") + ";"
            + TitleStage(title, 1 + (shapes?.Images.Count ?? 0), hasAudio: false, EditTimelineCalculator.OutputDuration(keepRanges),
                scale: scaleFilter, outV: "[tfull]", outA: null, mainLabel: "[tmain]") + $";[tfull]{tail}[outv]";
    }

    private static string ReplaceLast(string s, string old, string value)
    {
        int i = s.LastIndexOf(old, StringComparison.Ordinal);
        return i < 0 ? s : s[..i] + value + s[(i + old.Length)..];
    }

    /// <summary>
    /// GIF 1 回目: 全フレームから最適なパレット（palettegen）を作り PNG に保存する。
    /// 画面録画は背景がほぼ固定のため、背景を含む全体の色で作る（stats_mode=full）。
    /// </summary>
    public static ExportCommand GifPalette(string input, IReadOnlyList<TimeRange> keepRanges, VideoMetadata meta,
        string? subtitleFile, GifPreset preset, string paletteOutput, int ffmpegMajorVersion, double transitionSeconds = 0,
        ShapeFilterPlan? shapes = null, TitleFilterPlan? title = null)
    {
        if (keepRanges.Count == 0) throw new ArgumentException("出力する区間がありません。", nameof(keepRanges));
        var filter = BuildGifFilter(keepRanges, meta, subtitleFile, preset,
            string.Create(CultureInfo.InvariantCulture, $"palettegen=max_colors={preset.MaxColors}:stats_mode=full"), transitionSeconds, shapes, title);
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", input };
        AddShapeInputs(args, shapes);
        AddTitleInput(args, title);
        var (script, placeholder) = AddFilterComplex(args, filter, ffmpegMajorVersion);
        // パレットは拡張子なしの一時ファイルに書く（.png は置いた瞬間に暗号化される環境があり、2 回目で読めなくなるため。v1.4.2）。
        args.AddRange(["-map", "[outv]", "-an", "-sn", "-dn", "-frames:v", "1", "-update", "1",
            "-progress", "pipe:1", "-nostats", "-stats_period", "0.5", "-c:v", "png", "-f", "image2", paletteOutput]);
        return new ExportCommand(args, script, placeholder);
    }

    /// <summary>
    /// GIF 2 回目: パレットを使って GIF にする（paletteuse）。diff_mode=rectangle で前フレームから変化した範囲だけを処理し、
    /// GIF の差分フレーム（変化していない部分を透明にする）と組み合わせてサイズを抑える。無限ループ。
    /// </summary>
    public static ExportCommand GifEncode(string input, IReadOnlyList<TimeRange> keepRanges, VideoMetadata meta,
        string? subtitleFile, GifPreset preset, string paletteInput, string output, int ffmpegMajorVersion, double transitionSeconds = 0,
        ShapeFilterPlan? shapes = null, TitleFilterPlan? title = null)
    {
        if (keepRanges.Count == 0) throw new ArgumentException("出力する区間がありません。", nameof(keepRanges));
        // パレットは図形の画像・タイトルの画像の後の入力（どちらも無ければ従来どおり 1 番）。
        var paletteIndex = 1 + (shapes?.Images.Count ?? 0) + (title is null ? 0 : 1);
        var filter = ReplaceLast(BuildGifFilter(keepRanges, meta, subtitleFile, preset, tail: "null", transitionSeconds, shapes, title), "[outv]", "[gifsrc]")
            + string.Create(CultureInfo.InvariantCulture, $";[gifsrc][{paletteIndex}:v]paletteuse=dither={preset.Dither}:diff_mode=rectangle[outv]");
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", input };
        AddShapeInputs(args, shapes);
        AddTitleInput(args, title);
        args.AddRange(["-f", "png_pipe", "-i", paletteInput]);
        var (script, placeholder) = AddFilterComplex(args, filter, ffmpegMajorVersion);
        args.AddRange(["-map", "[outv]", "-an", "-sn", "-dn", "-map_metadata", "-1", "-loop", "0", "-f", "gif",
            "-progress", "pipe:1", "-nostats", "-stats_period", "0.5", output]);
        return new ExportCommand(args, script, placeholder);
    }

    // ---------------- 黒背景タイトル（v1.15） ----------------

    /// <summary>タイトルの画像を入力に加える（図形の画像の後。拡張子なしのため形式を明示する）。</summary>
    private static void AddTitleInput(List<string> args, TitleFilterPlan? title)
    {
        if (title is not null) args.AddRange(["-f", "png_pipe", "-i", title.File]);
    }

    /// <summary>フレームレートの文字列（29.97 → 30000/1001 のように分数で渡す。不明は 30）。</summary>
    public static string FpsText(double fps)
    {
        if (fps <= 0 || double.IsNaN(fps) || double.IsInfinity(fps)) return "30";
        foreach (var n in new[] { 24, 30, 60, 120 })
            if (Math.Abs(fps - n * 1000.0 / 1001.0) < 0.005) return $"{n * 1000}/1001";
        return Math.Abs(fps - Math.Round(fps)) < 0.005 ? I((int)Math.Round(fps)) : fps.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>タイトルの表示フレーム数（秒数 × フレームレート。1 以上）。</summary>
    public static int TitleFrameCount(TitleFilterPlan title)
        => Math.Max(1, (int)Math.Round(title.Seconds * (title.Fps > 0 ? title.Fps : 30), MidpointRounding.AwayFromZero));

    /// <summary>
    /// タイトルと本編の連結。タイトルの画像（入力 titleIndex）を秒数ぶん繰り返して先頭に置き、本編（既定のラベル [outv] / [outa]）の前に連結する。
    /// 本編の映像（図形・字幕を重ねた後）だけに冒頭フェード（黒から）をかける。タイトルは無音で、本編の音声は先頭から通常再生する（音声のフェードなし）。
    /// フレームレート・SAR・画素形式・音声の形式（48kHz ステレオ）をそろえて連結する。scale は GIF のように縮小する場合にタイトルへも同じ縮小をかける。
    /// </summary>
    private static string TitleStage(TitleFilterPlan title, int titleIndex, bool hasAudio, double mainDuration, string? scale, string outV, string? outA,
        string mainLabel = "[outv]")
    {
        var fps = FpsText(title.Fps);
        var frames = TitleFrameCount(title);
        var fade = title.FadeSeconds > 0
            ? string.Create(CultureInfo.InvariantCulture, $"fade=t=in:st=0:d={S(Math.Min(title.FadeSeconds, Math.Max(0.001, mainDuration)))}:color=black,")
            : "";
        var sb = new StringBuilder();
        sb.Append(mainLabel).Append(fade).Append(CultureInfo.InvariantCulture, $"fps={fps},setsar=1,format=yuv420p[tm]");
        sb.Append(CultureInfo.InvariantCulture, $";[{titleIndex}:v]loop=loop={frames - 1}:size=1:start=0,setpts=N/({fps}*TB),");
        if (scale is not null) sb.Append(scale).Append(',');
        sb.Append(CultureInfo.InvariantCulture, $"fps={fps},setsar=1,format=yuv420p[tt]");
        if (hasAudio)
        {
            var titleAudio = frames / (title.Fps > 0 ? title.Fps : 30);
            sb.Append(CultureInfo.InvariantCulture, $";anullsrc=r=48000:cl=stereo:d={S(titleAudio)}[ta]");
            sb.Append(";[outa]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo[ma]");
            sb.Append($";[tt][ta][tm][ma]concat=n=2:v=1:a=1{outV}{outA}");
        }
        else
        {
            sb.Append($";[tt][tm]concat=n=2:v=1:a=0{outV}");
        }
        return sb.ToString();
    }

    // ---------------- 図形の重ね合わせ（v1.6） ----------------

    /// <summary>図形の画像を入力に加える（入力 1 番から。拡張子なしのため形式を明示する）。</summary>
    private static void AddShapeInputs(List<string> args, ShapeFilterPlan? shapes)
    {
        if (shapes is null) return;
        foreach (var img in shapes.Images) args.AddRange(["-f", "png_pipe", "-i", img.File]);
    }

    /// <summary>
    /// 映像のフィルタの連なり「pre → 図形 → post」を作る。図形が無ければ従来どおり pre,post（どちらも無ければ null）。
    /// 図形がある場合は内部にラベル [shpN] を持つフィルタグラフの断片になるが、先頭・末尾は必ずフィルタなので、
    /// 従来の「[入力]断片[出力]」の形にそのまま埋め込める。frameWidth / frameHeight は図形を重ねる時点の映像の大きさ。
    /// モザイクは split した映像を切り出し → 縮小（平均）→ 最近傍で拡大してブロックにし、表示時間だけ元の位置へ重ねる。
    /// 画像は表示時間だけ重ねる（1 枚の画像は最後のコマを繰り返す）。時刻は出力動画の秒（字幕と同じ）。
    /// </summary>
    public static string? ComposeVideoChain(string? pre, ShapeFilterPlan? shapes, int frameWidth, int frameHeight, string? post)
    {
        if (shapes is null || shapes.IsEmpty) return JoinFilters(pre, post);
        var sb = new StringBuilder();
        sb.Append(pre ?? "null").Append("[shp0]");
        int n = 0;
        foreach (var m in shapes.Mosaics)
        {
            if (ClipMosaic(m, frameWidth, frameHeight) is not { } r) continue;
            int block = Math.Max(1, m.BlockSize);
            int cw = (r.W + block - 1) / block, ch = (r.H + block - 1) / block;
            sb.Append(CultureInfo.InvariantCulture,
                $";[shp{n}]split[shp{n}a][shp{n}b];[shp{n}b]crop={r.W}:{r.H}:{r.X}:{r.Y},scale={cw}:{ch}:flags=area,scale={cw * block}:{ch * block}:flags=neighbor,crop={r.W}:{r.H}:0:0[shpm{n}]");
            sb.Append(CultureInfo.InvariantCulture, $";[shp{n}a][shpm{n}]overlay={r.X}:{r.Y}:enable='{Between(m.Start, m.End)}'[shp{n + 1}]");
            n++;
        }
        for (int i = 0; i < shapes.Images.Count; i++)
        {
            var img = shapes.Images[i];
            sb.Append(CultureInfo.InvariantCulture, $";[shp{n}][{i + 1}:v]overlay={img.X}:{img.Y}:enable='{Between(img.Start, img.End)}'[shp{n + 1}]");
            n++;
        }
        sb.Append(CultureInfo.InvariantCulture, $";[shp{n}]").Append(post ?? "null");
        return sb.ToString();
    }

    /// <summary>表示時間 [start, end) の式（字幕と同じく終了時刻は含まない）。</summary>
    private static string Between(double start, double end) => $"gte(t,{S(start)})*lt(t,{S(end)})";

    /// <summary>
    /// モザイクの範囲を映像内に収め、色差の間引き（yuv420p）とずれないよう左上を偶数へ切り下げ、右下を偶数へ広げる。狭すぎれば null。
    /// </summary>
    private static (int X, int Y, int W, int H)? ClipMosaic(ShapeMosaic m, int frameWidth, int frameHeight)
    {
        int x1 = Math.Max(0, m.X) & ~1, y1 = Math.Max(0, m.Y) & ~1;
        int x2 = Math.Min(frameWidth, (m.X + m.Width + 1) & ~1), y2 = Math.Min(frameHeight, (m.Y + m.Height + 1) & ~1);
        return x2 - x1 >= 2 && y2 - y1 >= 2 ? (x1, y1, x2 - x1, y2 - y1) : null;
    }

    private static (string? Script, string? Placeholder) AddFilterComplex(List<string> args, string filter, int ffmpegMajorVersion)
    {
        if (filter.Length <= MaxInlineFilterLength)
        {
            args.AddRange(["-filter_complex", filter]);
            return (null, null);
        }
        args.AddRange([ffmpegMajorVersion >= 7 ? "-/filter_complex" : "-filter_complex_script", FilterScriptPlaceholder]);
        return (filter, FilterScriptPlaceholder);
    }

    // ---------------- 画面録画 ----------------

    /// <summary>
    /// 画面録画の 1 区間（一時停止で区切られた単位）。gdigrab で仮想スクリーン上の範囲（物理 px）を取り込み、
    /// 軽い設定（ultrafast）で H.264 にして Matroska に書く（強制終了されても途中まで読める形式）。
    /// 停止は標準入力への "q"。-nostdin は付けない。FPS は固定（取り込みが遅れたフレームは複製）。
    /// </summary>
    public static IReadOnlyList<string> ScreenRecordingSegment(ScreenRecordingOptions o, string output)
    {
        var r = o.Region;
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-y",
            "-thread_queue_size", "512",
            "-f", "gdigrab", "-framerate", I(o.Fps), "-draw_mouse", o.DrawCursor ? "1" : "0",
            "-offset_x", I(r.X), "-offset_y", I(r.Y), "-video_size", $"{I(r.Width)}x{I(r.Height)}",
            "-i", "desktop",
        };
        if (o.Audio is { } a)
        {
            args.AddRange(["-thread_queue_size", "1024"]);
            if (a.Format == "dshow") args.AddRange(["-audio_buffer_size", "50"]);
            if (a.ExtraInputOptions is { } extra) args.AddRange(extra);
            args.AddRange(["-f", a.Format, "-i", a.Input]);
        }
        args.AddRange(["-map", "0:v:0"]);
        if (o.Audio is not null) args.AddRange(["-map", "1:a:0"]);
        args.AddRange([
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "20", "-pix_fmt", "yuv420p",
            "-r", I(o.Fps), "-fps_mode", "cfr", "-g", I(o.Fps * 2),
        ]);
        if (o.Audio is not null) args.AddRange(["-c:a", "aac", "-b:a", "160k", "-ac", "2", "-ar", "48000"]);
        else args.Add("-an");
        // 書き込み開始を早く検知するため、進捗は 0.2 秒ごとに出す（録画中の経過時間の表示を実際の録画に近づける）。
        args.AddRange(["-progress", "pipe:1", "-nostats", "-stats_period", "0.2", "-f", "matroska", output]);
        return args;
    }

    /// <summary>録画の区間を 1 本の MP4 にまとめる（再エンコードしない）。concatList は区間ファイル名の一覧（作業フォルダー基準）。</summary>
    public static IReadOnlyList<string> ConcatRecordingSegments(IReadOnlyList<string> segmentFiles, string concatListFile, string output)
    {
        if (segmentFiles.Count == 0) throw new ArgumentException("録画の区間がありません。", nameof(segmentFiles));
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-y" };
        if (segmentFiles.Count == 1) args.AddRange(["-i", segmentFiles[0]]);
        else args.AddRange(["-f", "concat", "-safe", "0", "-i", concatListFile]);
        args.AddRange(["-map", "0", "-c", "copy", "-movflags", "+faststart", output]);
        return args;
    }

    /// <summary>
    /// 別々の MP4 を順番に 1 本へ連結する（再エンコードしない。映像と音声の先頭ストリームだけを取り込む）。
    /// 一覧ファイルは <see cref="ConcatListForPaths"/> で作る（絶対パスを書くため -safe 0 が必要）。
    /// </summary>
    public static IReadOnlyList<string> ConcatVideos(string concatListFile, string output)
        => ["-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", concatListFile,
            "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy", "-movflags", "+faststart", output];

    /// <summary>
    /// 絶対パスの concat 一覧ファイルの内容。パスの区切りは / にそろえ（\ はエスケープに読まれるため）、
    /// 空白・日本語・' を含んでも読めるよう ' で囲み、中の ' は '\'' にする。
    /// </summary>
    public static string ConcatListForPaths(IEnumerable<string> paths)
        => string.Concat(paths.Select(p => $"file '{Path.GetFullPath(p).Replace('\\', '/').Replace("'", "'\\''")}'\n"));

    /// <summary>concat demuxer の一覧ファイルの内容。</summary>
    public static string ConcatList(IEnumerable<string> segmentFiles)
        => string.Concat(segmentFiles.Select(f => $"file '{f.Replace("'", "'\\''")}'\n"));

    /// <summary>DirectShow の入力デバイス一覧（stderr に出る）。</summary>
    public static IReadOnlyList<string> ListDshowDevices()
        => ["-hide_banner", "-nostdin", "-list_devices", "true", "-f", "dshow", "-i", "dummy"];

    /// <summary>つなぎ目の処理を選ぶ。トランジションを入れるつなぎ目が無ければ従来の concat（引数も従来と同一）。</summary>
    public static string BuildJoinFilter(IReadOnlyList<TimeRange> ranges, bool hasAudio, string? videoPostFilter, double transitionSeconds)
    {
        var halves = TransitionHalves(ranges, transitionSeconds);
        return halves.Any(h => h > 0)
            ? BuildTransitionFilter(ranges, halves, hasAudio, videoPostFilter)
            : BuildConcatFilter(ranges, hasAudio, videoPostFilter);
    }

    /// <summary>これより短いのりしろ（片側の秒）ではトランジションを入れない（1 フレームに満たず効果が無い）。</summary>
    public const double MinTransitionHalf = 0.02;

    /// <summary>
    /// つなぎ目ごとののりしろ（片側の秒）。つなぎ目 i は区間 i と i+1 の間。
    /// 前の区間を h 秒延ばし、次の区間を h 秒早く始め、2h 秒重ねる。h は「指定の半分」「削る部分の半分」「前後の区間の 1/4」の最小。
    /// 延ばす部分は削る部分（区間の間）から取るため、重ねても出力の長さ・字幕の時刻は変わらない。
    /// </summary>
    public static IReadOnlyList<double> TransitionHalves(IReadOnlyList<TimeRange> ranges, double transitionSeconds)
    {
        var halves = new double[Math.Max(0, ranges.Count - 1)];
        if (transitionSeconds <= 0) return halves;
        for (int i = 0; i < halves.Length; i++)
        {
            var a = ranges[i];
            var b = ranges[i + 1];
            var h = Math.Min(Math.Min(transitionSeconds / 2, (b.Start - a.End) / 2), Math.Min(a.Duration, b.Duration) / 4);
            halves[i] = h >= MinTransitionHalf ? Math.Round(h, 3) : 0;
        }
        return halves;
    }

    /// <summary>
    /// つなぎ目にクロスフェード（映像 xfade=fade・音声 acrossfade）を入れて区間をつなぐ。halves は <see cref="TransitionHalves"/> の値で、
    /// 0 のつなぎ目は concat でそのままつなぐ。出力ラベルは [outv]（音声は [outa]）。
    /// </summary>
    public static string BuildTransitionFilter(IReadOnlyList<TimeRange> ranges, IReadOnlyList<double> halves, bool hasAudio, string? videoPostFilter = null)
    {
        var sb = new StringBuilder();
        var lengths = new double[ranges.Count];
        for (int i = 0; i < ranges.Count; i++)
        {
            double start = ranges[i].Start - (i > 0 ? halves[i - 1] : 0);
            double end = ranges[i].End + (i < halves.Count ? halves[i] : 0);
            lengths[i] = end - start;
            sb.Append(CultureInfo.InvariantCulture, $"[0:v:0]trim=start={S(start)}:end={S(end)},setpts=PTS-STARTPTS[v{i}];");
            if (hasAudio)
                sb.Append(CultureInfo.InvariantCulture, $"[0:a:0]atrim=start={S(start)}:end={S(end)},asetpts=PTS-STARTPTS[a{i}];");
        }
        string v = "[v0]", a = "[a0]";
        double length = lengths[0];
        var vLast = videoPostFilter is null ? "[outv]" : "[vcat]";
        for (int i = 1; i < ranges.Count; i++)
        {
            bool last = i == ranges.Count - 1;
            string vo = last ? vLast : $"[vx{i}]", ao = last ? "[outa]" : $"[ax{i}]";
            double d = halves[i - 1] * 2;
            if (d > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"{v}[v{i}]xfade=transition=fade:duration={S(d)}:offset={S(length - d)}{vo};");
                if (hasAudio) sb.Append(CultureInfo.InvariantCulture, $"{a}[a{i}]acrossfade=d={S(d)}{ao};");
            }
            else
            {
                sb.Append(CultureInfo.InvariantCulture, $"{v}[v{i}]concat=n=2:v=1:a=0{vo};");
                if (hasAudio) sb.Append(CultureInfo.InvariantCulture, $"{a}[a{i}]concat=n=2:v=0:a=1{ao};");
            }
            length += lengths[i] - d;
            v = vo;
            a = ao;
        }
        if (videoPostFilter is not null) sb.Append("[vcat]").Append(videoPostFilter).Append("[outv]");
        return sb.ToString().TrimEnd(';');
    }

    public static string BuildConcatFilter(IReadOnlyList<TimeRange> ranges, bool hasAudio, string? videoPostFilter = null)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < ranges.Count; i++)
        {
            var r = ranges[i];
            sb.Append(CultureInfo.InvariantCulture, $"[0:v:0]trim=start={S(r.Start)}:end={S(r.End)},setpts=PTS-STARTPTS[v{i}];");
            if (hasAudio)
                sb.Append(CultureInfo.InvariantCulture, $"[0:a:0]atrim=start={S(r.Start)}:end={S(r.End)},asetpts=PTS-STARTPTS[a{i}];");
        }
        for (int i = 0; i < ranges.Count; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"[v{i}]");
            if (hasAudio) sb.Append(CultureInfo.InvariantCulture, $"[a{i}]");
        }
        var vOut = videoPostFilter is null ? "[outv]" : "[vcat]";
        sb.Append(CultureInfo.InvariantCulture, $"concat=n={ranges.Count}:v=1:a={(hasAudio ? 1 : 0)}{vOut}");
        if (hasAudio) sb.Append("[outa]");
        if (videoPostFilter is not null) sb.Append(";[vcat]").Append(videoPostFilter).Append("[outv]");
        return sb.ToString();
    }

    /// <summary>
    /// ローカル解析を 1 回のデコードで行う。freezedetect はすべてのフレームを見た後、select で画面変化フレームだけを showinfo に通す。
    /// 無音は -af silencedetect。結果は stderr のログから解析する。
    /// </summary>
    public static IReadOnlyList<string> Analysis(string input, AnalysisSettings settings, bool hasAudio)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "info", "-i", input };
        var video = new List<string>();
        if (settings.ScaleWidth > 0) video.Add($"scale='min({I(settings.ScaleWidth)},iw)':-2");
        if (settings.EnableFreeze)
            video.Add(string.Create(CultureInfo.InvariantCulture, $"freezedetect=n={settings.FreezeNoiseDb:0.#}dB:d={S(settings.FreezeMinSeconds)}"));
        if (settings.EnableSceneChange)
            video.Add(string.Create(CultureInfo.InvariantCulture, $"select='gt(scene,{settings.SceneThreshold:0.###})',showinfo"));

        bool doVideo = settings.EnableFreeze || settings.EnableSceneChange;
        bool doAudio = settings.EnableSilence && hasAudio;
        if (doVideo) args.AddRange(["-map", "0:v:0", "-vf", string.Join(',', video)]);
        if (doAudio)
            args.AddRange(["-map", "0:a:0", "-af",
                string.Create(CultureInfo.InvariantCulture, $"silencedetect=noise={settings.SilenceNoiseDb:0.#}dB:d={S(settings.SilenceMinSeconds)}")]);
        if (!doVideo && !doAudio) throw new InvalidOperationException("解析対象がありません。");
        if (!doVideo) args.Add("-vn");
        if (!doAudio) args.Add("-an");
        args.AddRange(["-sn", "-dn", "-progress", "pipe:1", "-nostats", "-f", "null", "-"]);
        return args;
    }
}
