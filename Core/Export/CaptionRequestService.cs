using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Naming;
using ManualVideoStudio.Core.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Core.Export;

/// <summary>準備した字幕依頼用動画（Gemini へ添付する字幕なしの動画。v1.12）。</summary>
/// <param name="Path">フルパス。</param>
/// <param name="Fingerprint">作ったときの元動画・カット・生成条件の指紋（一致するときだけ再利用する）。</param>
/// <param name="Duration">動画の長さ（秒。カット後の本編の長さ＝完成動画の本編部分と同じ時間軸。タイトルは含まない）。</param>
public sealed record CaptionRequestVideo(string Path, string Fingerprint, double Duration, DateTimeOffset CreatedAt)
{
    /// <summary>プロジェクトフォルダーを使えず別の場所に作ったときの案内（通常は空）。</summary>
    public string Notice { get; init; } = "";
}

/// <summary>依頼用動画の削除の結果。</summary>
public sealed record CaptionRequestDeleteResult(bool Success, string Message)
{
    public static CaptionRequestDeleteResult Ok(string message = "") => new(true, message);
    public static CaptionRequestDeleteResult Fail(string message) => new(false, message);
}

/// <summary>依頼用動画の保存先（どこに作るか）。</summary>
/// <param name="Directory">保存先フォルダーのフルパス。</param>
/// <param name="Notice">プロジェクトフォルダーを使えず別の場所にしたときの案内（通常は空）。</param>
public sealed record CaptionRequestLocation(string Directory, string Notice = "");

/// <summary>
/// 依頼用動画の保存場所と管理（v1.12、v1.17.0 で保存先を変更）。
/// 保存先は<b>プロジェクトフォルダー（プロジェクトファイルのあるフォルダー）の中の caption-requests\</b>だけ。アプリ・FFmpeg・ブラウザーが
/// 同じ実パスで扱え、利用者も場所を把握できる（Store 版で AppData が振り替えられる問題を避ける）。AppData などへの退避はしない。
/// キャッシュ cache\ とは別で、キャッシュの自動整理 <see cref="FrameCacheCleaner"/> は触れない。zip の書き出しにも含めない。
/// 削除できるのは、このアプリが作って <see cref="Register"/> したファイルだけ（ファイル名や拡張子からは推測しない）。
/// フォルダー全体・複数のまとめ削除はしない（削除後に空になったプロジェクトフォルダー内の caption-requests だけは片付ける）。
/// </summary>
public sealed class CaptionRequestStore
{
    private readonly HashSet<string> _managed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <param name="root">（互換用。保存先の決定には使わない。削除を許す保存先に含めるだけ。）</param>
    public CaptionRequestStore(string root)
    {
        Root = Path.GetFullPath(root);
        _folders.Add(Trim(Root));
    }

    public string Root { get; }

    /// <summary>プロジェクトを保存しているフォルダーの中の保存先（フォルダーはまだ作らない）。プロジェクトが未保存なら null。</summary>
    public static string? ProjectLocation(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath));
        return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, AppPaths.CaptionRequestFolderName);
    }

    /// <summary>
    /// 保存先（プロジェクトフォルダー内）を確かめる。作成・書き込みできなければ、フルパスと理由を含む <see cref="PathStageException"/>。
    /// アプリのデータフォルダーへの退避はしない（v1.17.0。プロジェクトを保存してから準備する。保存先は常にプロジェクトフォルダー）。
    /// </summary>
    public CaptionRequestLocation Resolve(string? projectLocation)
    {
        if (string.IsNullOrEmpty(projectLocation))
            throw new PathStageException("字幕依頼用動画の保存先の準備", "(プロジェクト未保存)", "プロジェクトが保存されていないため、保存先（プロジェクトフォルダー）が決まりません。先にプロジェクトを保存してください。");
        var problem = TryPrepare(projectLocation);
        if (problem is not null) throw new PathStageException("字幕依頼用動画の保存先の準備", Path.GetFullPath(projectLocation), problem);
        return new CaptionRequestLocation(Path.GetFullPath(projectLocation));
    }

    /// <summary>フォルダーを作って書き込みを試す。使えなければ理由、使えれば null。</summary>
    private string? TryPrepare(string folder)
    {
        try
        {
            var full = Path.GetFullPath(folder);
            Directory.CreateDirectory(full);
            // 拡張子を付けない（.txt などは暗号化される環境がある）。書けたらすぐ消す。
            var probe = Path.Combine(full, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, [0]);
            File.Delete(probe);
            lock (_gate) _folders.Add(Trim(full));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ex.Message;
        }
    }

    /// <summary>新しい依頼用動画のパス（<paramref name="location"/> の中。同名があれば連番を付ける）。</summary>
    public string NewPath(CaptionRequestLocation location, string projectName, DateTime now)
    {
        var name = FilenameSanitizer.Sanitize(projectName, 40);
        if (name.Length == 0) name = "動画";
        var baseName = $"{name}_{now:yyyyMMdd_HHmmss}_gemini";
        var path = Path.Combine(location.Directory, baseName + ".mp4");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(location.Directory, $"{baseName}_{i}.mp4");
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
    /// 記録済みの依頼用動画 1 件だけを削除する。記録に無い・このアプリが決めた保存先の外のファイルは削除しない。
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
        bool inFolder;
        lock (_gate) inFolder = dir is not null && _folders.Contains(Trim(dir));
        if (!IsManaged(full) || !inFolder)
            return CaptionRequestDeleteResult.Fail("このアプリが作った字幕依頼用動画ではないため、削除しません。");
        try
        {
            if (!File.Exists(full)) { Forget(full); return CaptionRequestDeleteResult.Ok("既に削除されています。"); }
            File.Delete(full);
            Forget(full);
            RemoveFolderIfEmpty(dir!);
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

    /// <summary>空になったプロジェクトフォルダー内の保存先を片付ける（アプリのデータフォルダー側は残す）。</summary>
    private void RemoveFolderIfEmpty(string dir)
    {
        if (string.Equals(Trim(dir), Trim(Root), StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string Trim(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
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
    /// 失敗したときは、どの段階か（元動画の確認／保存先の準備／FFmpeg の書き出し／一時ファイルの移動／生成後の確認）と、
    /// 対象のフルパスを <see cref="PathStageException"/>（書き出しの失敗は <see cref="FFmpegException"/>）で返し、ログにも記録する。
    /// </summary>
    /// <param name="projectLocation">プロジェクトフォルダー内の保存先（<see cref="CaptionRequestStore.ProjectLocation"/>）。未保存なら null。</param>
    public async Task<CaptionRequestVideo> PrepareAsync(string ffmpegPath, int ffmpegMajorVersion, ProjectDocument project, string projectName,
        string? projectLocation, IProgress<VideoExportProgress>? progress, CancellationToken ct)
    {
        var meta = project.VideoMetadata ?? throw new InvalidOperationException("動画の情報がありません。");
        var transition = project.ExportSettings.TransitionSecondsForExport;
        var fingerprint = Fingerprint(project, transition);

        // 1. 元動画（読めない原因は、移動・削除のほか、暗号化された拡張子のこともある）
        var source = project.SourceVideo.Path;
        if (!File.Exists(source))
            throw Fail(new PathStageException("元動画の確認", source, "元動画が見つかりません（移動・削除された可能性があります）。元動画を指定し直してください。"));
        try { using var _ = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Fail(new PathStageException("元動画の読み取り", source, ex.Message, ex));
        }

        // 2. 保存先（プロジェクトフォルダー内 → だめならアプリのデータフォルダー）
        CaptionRequestLocation location;
        try { location = store.Resolve(projectLocation); }
        catch (PathStageException ex) { throw Fail(ex); }
        if (location.Notice.Length > 0) _logger.LogWarning("Caption request: {Notice}", location.Notice);
        var path = store.NewPath(location, projectName, DateTime.Now);
        _logger.LogInformation("Caption request prepare: source={Source} output={Output}", source, path);

        // 3. FFmpeg の書き出し（字幕・図形は渡さない。カット・クロスフェードは完成動画と同じ）
        var request = new VideoExportRequest(ffmpegPath, ffmpegMajorVersion, source, meta,
            project.CutSegments.ToList(), path, Encode, TransitionSeconds: transition);
        VideoExportResult result;
        try { result = await export.ExportAsync(request, progress, ct); }
        catch (OperationCanceledException) { throw; }
        catch (PathStageException ex) { throw Fail(ex); }
        catch (FFmpegException ex)
        {
            _logger.LogError("Caption request: FFmpeg failed: {Message} / {Detail}", ex.Message, ex.ErrorDetail);
            throw;
        }

        // 4. 生成後の確認
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw Fail(new PathStageException("生成後のファイルの確認", path, "書き出しは完了しましたが、出力ファイルが見つからない、または空です。"));
        store.Register(path);
        _logger.LogInformation("Caption request video prepared: {Path} ({Duration:0.000}s)", path, result.OutputDuration);
        return new CaptionRequestVideo(path, fingerprint, result.OutputDuration, DateTimeOffset.Now) { Notice = location.Notice };
    }

    private PathStageException Fail(PathStageException ex)
    {
        _logger.LogError(ex, "Caption request failed at {Stage}: {Path}: {Reason}", ex.Stage, ex.FilePath, ex.Reason);
        return ex;
    }
}
