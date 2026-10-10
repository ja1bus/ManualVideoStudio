using System.Text;
using System.Text.RegularExpressions;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Subtitles;

namespace ManualVideoStudio.Core.Export;

/// <summary>確認画像の結果。SelectedFont は libass が実際に使ったフォント（PostScript 名。分からなければ null）。</summary>
public sealed record SubtitleSampleResult(bool Succeeded, string? SelectedFont, string? Error);

/// <summary>
/// 字幕の確認画像（v1.5）。書き出しと同じ ASS・libass で 1 コマだけ描き、実際に使われたフォントも調べる。
/// 動画を渡さなければ灰色の画面に描く（書き出し前のフォントの確認に使う）。
/// </summary>
public sealed class SubtitleSampleRenderer(IProcessRunner runner)
{
    // 例: "fontselect: (Meiryo, 400, 0) -> Meiryo, 0, Meiryo"
    private static readonly Regex FontSelect = new(@"fontselect:\s*\((?<req>.+?),\s*\d+,\s*\d+\)\s*->\s*(?<ps>[^,]+),", RegexOptions.Compiled);

    /// <summary>
    /// 確認画像を output（拡張子なしでよい。中身は PNG）に書く。videoPath が null なら灰色の画面に描く。
    /// sourceTime は動画の位置（秒）、width / height は字幕の座標に使う映像の大きさ。
    /// </summary>
    public async Task<SubtitleSampleResult> RenderAsync(string ffmpegPath, string? videoPath, double sourceTime, int width, int height,
        SubtitleSegment segment, SubtitleStyle style, ISubtitleTextMeasurer? measurer, string output, CancellationToken ct)
    {
        if (width <= 0 || height <= 0) return new SubtitleSampleResult(false, null, "映像の大きさが不明です。");
        var sample = new SubtitleSegment
        {
            Start = 0,
            End = 3600,
            Text = segment.Text,
            Position = segment.Position,
            FontSize = segment.FontSize,
            VerticalOffset = segment.VerticalOffset,
        };
        using var work = await ExportWorkspace.CreateAsync([sample], style, width, height, measurer, ct);
        var r = await runner.RunAsync(new ProcessRequest(ffmpegPath,
            FFmpegArguments.SubtitleSample(videoPath, Math.Max(0, sourceTime), width, height, work.SubtitleFile!, Path.GetFullPath(output)))
        {
            WorkingDirectory = work.Directory,
            Timeout = TimeSpan.FromSeconds(30),
        }, ct);
        if (!r.Succeeded || !File.Exists(output)) return new SubtitleSampleResult(false, null, r.ErrorTail(5));
        return new SubtitleSampleResult(true, SelectedFont(r.StandardError, SubtitleStyle.NormalizeFontFamily(style.FontFamily)), null);
    }

    /// <summary>stderr から、指定したフォントの代わりに libass が選んだフォント（PostScript 名）を取り出す。</summary>
    public static string? SelectedFont(string stderr, string requested)
    {
        foreach (Match m in FontSelect.Matches(stderr ?? ""))
            if (string.Equals(m.Groups["req"].Value.Trim(), requested, StringComparison.OrdinalIgnoreCase))
                return m.Groups["ps"].Value.Trim();
        return null;
    }

    /// <summary>
    /// 選ばれたフォントが指定どおりか。フォントファイルから読んだ PostScript 名があれば英数字だけで比べ、
    /// 無ければ名前の似かたで判断する（例: "BIZ UDPGothic" → "BIZ-UDPGothic"、"Yu Gothic" → "YuGothic-Regular"）。
    /// </summary>
    public static bool IsExpectedFont(string requested, string selected, IReadOnlyCollection<string>? postScriptNames)
    {
        static string Key(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        var sel = Key(selected);
        if (postScriptNames is { Count: > 0 } && postScriptNames.Any(p => Key(p) == sel)) return true;
        var req = Key(requested);
        return req.Length > 0 && (sel.StartsWith(req, StringComparison.Ordinal) || req.StartsWith(sel, StringComparison.Ordinal) && sel.Length >= 4);
    }
}
