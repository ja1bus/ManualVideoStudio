using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Naming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Core.Export;

/// <summary>準備した字幕依頼用動画（Gemini へ添付する字幕なしの動画。v1.12）。</summary>
/// <param name="Path">フルパス。</param>
/// <param name="Fingerprint">作ったときの元動画・カット・生成条件の指紋（一致するときだけ再利用する）。</param>
/// <param name="Duration">動画の長さ（秒。カット後の本編の長さ＝完成動画の本編部分と同じ時間軸。タイトルは含まない）。</param>
public sealed record CaptionRequestVideo(string Path, string Fingerprint, double Duration, DateTimeOffset CreatedAt);

/// <summary>依頼用動画の削除の結果。</summary>
public sealed record CaptionRequestDeleteResult(bool Success, string Message)
{
    public static CaptionRequestDeleteResult Ok(string message = "") => new(true, message);
    public static CaptionRequestDeleteResult Fail(string message) => new(false, message);
}

/// <summary>
/// 依頼用動画の保存場所と管理（v1.12）。保存場所は %LOCALAPPDATA%\ManualVideoStudio\caption-requests\（キャッシュ cache\ とは別。
/// キャッシュの自動整理 <see cref="FrameCacheCleaner"/> は触れない）。
/// 削除できるのは、このアプリが作って <see cref="Register"/> したファイルだけ（ファイル名や拡張子からは推測しない）。
/// フォルダー全体・複数のまとめ削除はしない。
/// </summary>
public sealed class CaptionRequestStore
{
    private readonly HashSet<string> _managed = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public CaptionRequestStore(string root) => Root = Path.GetFullPath(root);

    public string Root { get; }

    /// <summary>新しい依頼用動画の保存先（保存場所を作る。同名があれば連番を付ける）。</summary>
    public string NewPath(string projectName, DateTime now)
    {
        Directory.CreateDirectory(Root);
        var name = FilenameSanitizer.Sanitize(projectName, 40);
        if (name.Length == 0) name = "動画";
        var baseName = $"{name}_{now:yyyyMMdd_HHmmss}_gemini";
        var path = Path.Combine(Root, baseName + ".mp4");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(Root, $"{baseName}_{i}.mp4");
        return path;
    }

    /// <summary>作成に成功したファイルを、削除してよい依頼用動画として記録する。</summary>
    public void Register(string path)
    {
        lock (_gate) _managed.Add(Path.GetFullPath(path));
    }

    public bool IsManaged(string path)
    {
        lock (_gate) return _managed.Contains(Path.GetFullPath(path));
    }

    /// <summary>
    /// 記録済みの依頼用動画 1 件だけを削除する。記録に無い・保存場所の外のファイルは削除しない。
    /// 既に無ければ成功として扱う。失敗は例外にせず理由を返す。
    /// </summary>
    public CaptionRequestDeleteResult Delete(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return CaptionRequestDeleteResult.Fail($"パスが正しくありません: {ex.Message}");
        }
        var dir = Path.GetDirectoryName(full);
        if (!IsManaged(full) || !string.Equals(dir, Root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return CaptionRequestDeleteResult.Fail("このアプリが作った字幕依頼用動画ではないため、削除しません。");
        try
        {
            if (!File.Exists(full)) { Forget(full); return CaptionRequestDeleteResult.Ok("既に削除されています。"); }
            File.Delete(full);
            Forget(full);
            return CaptionRequestDeleteResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CaptionRequestDeleteResult.Fail(ex.Message);
        }
    }

    private void Forget(string full)
    {
        lock (_gate) _managed.Remove(full);
    }
}

/// <summary>
/// 依頼用動画の作成（v1.12）。元動画に確定カットを適用した、字幕・図形なしの動画を作る。
/// 完成動画の本編部分と同じカット・再生速度・クロスフェードの条件（<see cref="FFmpegArguments.Export"/>）で作るため、本編部分と時間軸が一致する。
/// 黒背景タイトルと冒頭フェード（v1.15）は含めない（字幕の時刻は常に本編基準。完成動画ではタイトルの秒数だけ後ろにずれて表示される）。
/// 完成動画の書き出しにはこの動画を使わない（完成動画は常に元動画から作る）。
/// </summary>
public sealed class CaptionRequestService(IVideoExportService export, CaptionRequestStore store, ILogger<CaptionRequestService>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<CaptionRequestService>.Instance;

    /// <summary>
    /// 依頼用動画の画質。画面の文字が読めるよう解像度は変えず、完成動画（CRF 22）より少し軽くする程度に留める。
    /// 生成条件を変えたら <see cref="EncodeProfile"/> も変える（準備済みの動画を再利用しないため）。
    /// </summary>
    public static readonly VideoEncodeOptions Encode = new() { Crf = 24, Preset = "veryfast", AudioBitrateKbps = 128 };

    /// <summary>生成条件の名前（指紋に含める）。</summary>
    public const string EncodeProfile = "crf24-veryfast-a128-v1";

    /// <summary>元動画・カット・生成条件の指紋。同じなら準備済みの動画をそのまま使える。</summary>
    public static string Fingerprint(ProjectDocument project, double transitionSeconds)
    {
        var sb = new StringBuilder();
        var src = project.SourceVideo.Path;
        var fi = new FileInfo(src);
        sb.Append(Path.GetFullPath(src).ToUpperInvariant()).Append('|').Append(fi.Exists ? fi.Length : 0).Append('|')
          .Append(fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0).Append('|').Append(F(project.VideoMetadata?.Duration ?? 0)).Append('|')
          .Append(project.VideoMetadata?.HasAudio == true ? "a" : "-").Append('|').Append(F(transitionSeconds)).Append('|').Append(EncodeProfile);
        foreach (var c in project.CutSegments.Where(c => c.Action is SegmentAction.Remove or SegmentAction.Shorten).OrderBy(c => c.Start))
            sb.Append('|').Append(F(c.Start)).Append('-').Append(F(c.End)).Append(c.Action == SegmentAction.Shorten ? 's' : 'r').Append(F(c.KeepSeconds));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();

        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>準備済みの動画を今のプロジェクトでそのまま使えるか（ファイルがあり、指紋が一致する）。</summary>
    public static bool IsCurrent(CaptionRequestVideo? video, ProjectDocument project, double transitionSeconds)
        => video is not null && File.Exists(video.Path) && video.Fingerprint == Fingerprint(project, transitionSeconds);

    /// <summary>
    /// 依頼用動画を作る。成功したときだけ保存場所に置き、削除してよい動画として記録する。
    /// キャンセル・失敗では途中のファイルを残さない（<see cref="IVideoExportService"/> が一時ファイルへ書いて成功時に移す）。
    /// </summary>
    public async Task<CaptionRequestVideo> PrepareAsync(string ffmpegPath, int ffmpegMajorVersion, ProjectDocument project, string projectName,
        IProgress<VideoExportProgress>? progress, CancellationToken ct)
    {
        var meta = project.VideoMetadata ?? throw new InvalidOperationException("動画の情報がありません。");
        var transition = project.ExportSettings.TransitionSecondsForExport;
        var fingerprint = Fingerprint(project, transition);
        var path = store.NewPath(projectName, DateTime.Now);
        // 字幕・図形は渡さない（Subtitles / Shapes = null）。カット・クロスフェードは完成動画と同じ。
        var request = new VideoExportRequest(ffmpegPath, ffmpegMajorVersion, project.SourceVideo.Path, meta,
            project.CutSegments.ToList(), path, Encode, TransitionSeconds: transition);
        var result = await export.ExportAsync(request, progress, ct);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new FFmpegException("字幕依頼用の動画を作成できませんでした。", "出力ファイルがありません。");
        store.Register(path);
        _logger.LogInformation("Caption request video prepared: {Path} ({Duration:0.000}s)", path, result.OutputDuration);
        return new CaptionRequestVideo(path, fingerprint, result.OutputDuration, DateTimeOffset.Now);
    }
}
