namespace ManualVideoStudio.Core.FFmpeg;

/// <summary>実行ファイルの識別（大きさと更新日時。変わっていれば別のファイルとみなして調べ直す）。</summary>
public sealed record ToolFileStamp(long Length, DateTime LastWriteTimeUtc);

/// <summary>
/// 前回の起動で見つけた ffmpeg / ffprobe と、調べた版・機能（v1.6.1。設定ファイルの <c>ffmpegCache</c>）。
/// 起動のたびに PATH を探して ffmpeg を 4 回実行すると、遅い PC では数秒～10 秒かかり、その間に操作すると
/// 「FFmpeg が見つかりません」になっていた。記録したファイルが同じ（パス・大きさ・更新日時が一致）なら、ffmpeg を実行せずにこれを使う。
/// </summary>
public sealed class FFmpegCache
{
    public string FfmpegPath { get; set; } = "";
    public long FfmpegLength { get; set; }
    public DateTime FfmpegLastWriteTimeUtc { get; set; }
    public string FfprobePath { get; set; } = "";
    public long FfprobeLength { get; set; }
    public DateTime FfprobeLastWriteTimeUtc { get; set; }
    public string FfmpegVersion { get; set; } = "";
    public string FfprobeVersion { get; set; } = "";
    public int MajorVersion { get; set; }
    public List<string> Filters { get; set; } = [];
    public List<string> Encoders { get; set; } = [];

    public static FFmpegCache Create(FFmpegLocation location, FFmpegCapabilities caps, ToolFileStamp ffmpeg, ToolFileStamp ffprobe) => new()
    {
        FfmpegPath = location.Ffmpeg.Path ?? "",
        FfmpegLength = ffmpeg.Length,
        FfmpegLastWriteTimeUtc = ffmpeg.LastWriteTimeUtc,
        FfprobePath = location.Ffprobe.Path ?? "",
        FfprobeLength = ffprobe.Length,
        FfprobeLastWriteTimeUtc = ffprobe.LastWriteTimeUtc,
        FfmpegVersion = caps.FfmpegVersion,
        FfprobeVersion = caps.FfprobeVersion,
        MajorVersion = caps.MajorVersion,
        Filters = caps.Filters.Order(StringComparer.Ordinal).ToList(),
        Encoders = caps.Encoders.Order(StringComparer.Ordinal).ToList(),
    };

    /// <summary>
    /// 見つけた ffmpeg / ffprobe が記録と同じファイルなら、記録した版・機能を返す（違えば null）。
    /// stamp はファイルの識別を返す（無ければ null）。
    /// </summary>
    public FFmpegCapabilities? Match(FFmpegLocation location, Func<string, ToolFileStamp?> stamp)
    {
        if (!location.IsAvailable || Filters.Count == 0 || Encoders.Count == 0) return null;
        if (!Same(location.Ffmpeg.Path!, FfmpegPath, FfmpegLength, FfmpegLastWriteTimeUtc, stamp)) return null;
        if (!Same(location.Ffprobe.Path!, FfprobePath, FfprobeLength, FfprobeLastWriteTimeUtc, stamp)) return null;
        return new FFmpegCapabilities
        {
            FfmpegVersion = FfmpegVersion,
            FfprobeVersion = FfprobeVersion,
            MajorVersion = MajorVersion,
            Filters = new HashSet<string>(Filters, StringComparer.Ordinal),
            Encoders = new HashSet<string>(Encoders, StringComparer.Ordinal),
        };
    }

    private static bool Same(string path, string cachedPath, long length, DateTime writeTime, Func<string, ToolFileStamp?> stamp)
        => string.Equals(path, cachedPath, StringComparison.OrdinalIgnoreCase)
           && stamp(path) is { } s && s.Length == length && s.LastWriteTimeUtc == writeTime;

    /// <summary>実際のファイルの識別（無い・読めなければ null）。</summary>
    public static ToolFileStamp? StampOf(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? new ToolFileStamp(fi.Length, fi.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
