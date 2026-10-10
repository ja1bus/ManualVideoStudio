using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Core.Export;

/// <summary>ffmpeg の実行に失敗した（利用者向けメッセージと stderr 末尾を持つ）。</summary>
public sealed class FFmpegException(string message, string errorDetail) : Exception(message)
{
    public string ErrorDetail { get; } = errorDetail;
}

public interface IFrameExtractionService
{
    /// <summary>指定時刻のフレームを画像ファイルにする。書き込みは一時ファイル経由で、成功時だけ outputPath を置き換える。</summary>
    Task ExtractAsync(string ffmpegPath, string videoPath, double time, string outputPath, ImageFormat format,
        int jpegQuality, int maxWidth, CancellationToken ct);

    /// <summary>サムネイル（JPEG・縮小）を取得する。キャッシュ済みなら ffmpeg を実行しない。</summary>
    Task<string> GetThumbnailAsync(string ffmpegPath, string videoPath, double time, int width, CancellationToken ct);

    /// <summary>原寸・PNG（可逆）のフレームを取得する（注釈編集の元画像）。キャッシュ済みなら ffmpeg を実行しない。</summary>
    Task<string> GetFullFrameAsync(string ffmpegPath, string videoPath, double time, CancellationToken ct);
}

public sealed class FrameExtractionService : IFrameExtractionService
{
    private readonly IProcessRunner _runner;
    private readonly ILogger _logger;
    private readonly string _cacheRoot;
    // サムネイル・候補画像の同時生成数を抑える（大量登録時に ffmpeg を並べすぎない）。
    private readonly SemaphoreSlim _gate = new(3);

    public FrameExtractionService(IProcessRunner runner, string cacheRoot, ILogger<FrameExtractionService>? logger = null)
    {
        _runner = runner;
        _cacheRoot = cacheRoot;
        _logger = logger ?? NullLogger<FrameExtractionService>.Instance;
    }

    public async Task ExtractAsync(string ffmpegPath, string videoPath, double time, string outputPath, ImageFormat format,
        int jpegQuality, int maxWidth, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(outputPath)}.partial-{Guid.NewGuid():N}{Path.GetExtension(outputPath)}");
        await _gate.WaitAsync(ct);
        try
        {
            var args = FFmpegArguments.FrameExtraction(videoPath, time, temp, format, jpegQuality, maxWidth);
            var r = await _runner.RunAsync(new ProcessRequest(ffmpegPath, args) { Timeout = TimeSpan.FromMinutes(2) }, ct);
            if (!r.Succeeded || !File.Exists(temp) || new FileInfo(temp).Length == 0)
            {
                _logger.LogError("Frame extraction failed at {Time}: {Tail}", time, r.ErrorTail(10));
                throw new FFmpegException($"{TimeFormat.Format(time)} の画像を作成できませんでした。", r.ErrorTail(10));
            }
            File.Move(temp, outputPath, overwrite: true);
        }
        finally
        {
            _gate.Release();
            TryDelete(temp);
        }
    }

    // キャッシュの画像はアプリが読み直すため拡張子を付けない（.jpg / .png は置いた瞬間に暗号化される環境があるため。v1.4.2）。
    // 形式は ffmpeg の引数で明示し、読み込みはファイルの中身から判断する。
    public async Task<string> GetThumbnailAsync(string ffmpegPath, string videoPath, double time, int width, CancellationToken ct)
    {
        var dir = Path.Combine(_cacheRoot, VideoKey(videoPath));
        var path = Path.Combine(dir, string.Create(CultureInfo.InvariantCulture, $"t{(long)Math.Round(time * 1000)}_w{width}"));
        if (File.Exists(path) && new FileInfo(path).Length > 0) { TouchAccess(dir); return path; }
        await ExtractAsync(ffmpegPath, videoPath, time, path, ImageFormat.Jpeg, 4, width, ct);
        TouchAccess(dir);
        return path;
    }

    public async Task<string> GetFullFrameAsync(string ffmpegPath, string videoPath, double time, CancellationToken ct)
    {
        var dir = Path.Combine(_cacheRoot, VideoKey(videoPath));
        var path = Path.Combine(dir, string.Create(CultureInfo.InvariantCulture, $"f{(long)Math.Round(time * 1000)}"));
        if (File.Exists(path) && new FileInfo(path).Length > 0) { TouchAccess(dir); return path; }
        await ExtractAsync(ffmpegPath, videoPath, time, path, ImageFormat.Png, 2, 0, ct);
        TouchAccess(dir);
        return path;
    }

    /// <summary>動画ごとのキャッシュフォルダ名（パス・サイズ・更新日時から。動画が差し替わったら別キャッシュ）。</summary>
    public static string VideoKey(string videoPath)
    {
        var fi = new FileInfo(videoPath);
        var raw = $"{Path.GetFullPath(videoPath).ToUpperInvariant()}|{(fi.Exists ? fi.Length : 0)}|{(fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16].ToLowerInvariant();
    }

    /// <summary>動画キーのフォルダーの更新日時を「最終アクセス」として更新する（自動整理 <see cref="FrameCacheCleaner"/> が古さの判断に使う）。</summary>
    private static void TouchAccess(string dir)
    {
        try { Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
