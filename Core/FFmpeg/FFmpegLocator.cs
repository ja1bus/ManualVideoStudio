namespace ManualVideoStudio.Core.FFmpeg;

public enum ToolSource
{
    NotFound,
    UserSetting,
    /// <summary>前回の起動で見つけて設定ファイルに記録したパス（v1.6.1）。</summary>
    Remembered,
    SameDirectoryAsFfmpeg,
    PathEnvironment,
}

public sealed record ToolLocation(string? Path, ToolSource Source, string Message)
{
    public bool Found => Path is not null;
}

public sealed record FFmpegLocation(ToolLocation Ffmpeg, ToolLocation Ffprobe)
{
    public bool IsAvailable => Ffmpeg.Found && Ffprobe.Found;
}

/// <summary>ファイル存在・環境変数へのアクセス（テストで差し替える）。</summary>
public interface IToolEnvironment
{
    bool FileExists(string path);
    string? GetEnvironmentVariable(string name);

    /// <summary>探す PATH の値（先頭から順に探す）。既定はこのプロセスの PATH だけ。</summary>
    IEnumerable<string?> GetPathVariables() => [GetEnvironmentVariable("PATH")];
}

public sealed class SystemToolEnvironment : IToolEnvironment
{
    public bool FileExists(string path) => File.Exists(path);
    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    /// <summary>
    /// このプロセスの PATH に加えて、レジストリのユーザー・システムの PATH も探す（v1.6.1）。
    /// winget などで PATH を追加した後、古い環境変数を引き継いだまま起動された場合（サインインし直す前など）でも見つけるため。
    /// </summary>
    public IEnumerable<string?> GetPathVariables()
    {
        yield return Environment.GetEnvironmentVariable("PATH");
        if (!OperatingSystem.IsWindows()) yield break;
        yield return Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
        yield return Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);
    }
}

/// <summary>
/// ffmpeg.exe / ffprobe.exe を探す。アプリには同梱しない。
/// ffmpeg : 設定で指定したパス → 前回見つけたパス（v1.6.1）→ PATH → 見つからない
/// ffprobe: 設定で指定したパス（任意・拡張）→ ffmpeg と同じフォルダ → 前回見つけたパス（v1.6.1）→ PATH → 見つからない
/// PATH はこのプロセスの PATH、ユーザーの PATH、システムの PATH の順に探す（v1.6.1）。
/// カレントフォルダや既定インストール先の推測はしない。PATH の相対ディレクトリは無視する。
/// </summary>
public sealed class FFmpegLocator(IToolEnvironment environment)
{
    public const string FfmpegExe = "ffmpeg.exe";
    public const string FfprobeExe = "ffprobe.exe";

    public FFmpegLocator() : this(new SystemToolEnvironment()) { }

    public FFmpegLocation Locate(string? userFfmpegPath, string? userFfprobePath = null,
        string? rememberedFfmpegPath = null, string? rememberedFfprobePath = null)
    {
        var ffmpeg = LocateFfmpeg(userFfmpegPath, rememberedFfmpegPath);
        var ffprobe = LocateFfprobe(userFfprobePath, ffmpeg, rememberedFfprobePath);
        return new FFmpegLocation(ffmpeg, ffprobe);
    }

    /// <summary>前回見つけたパス（名前が合い、今もあるときだけ使う。winget の更新などで無くなっていれば探し直す）。</summary>
    private string? Remembered(string? path, string exeName)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Trim('"');
        return IsExe(p, exeName) && Path.IsPathFullyQualified(p) && environment.FileExists(p) ? p : null;
    }

    private ToolLocation LocateFfmpeg(string? userPath, string? rememberedPath)
    {
        string? warning = null;
        if (!string.IsNullOrWhiteSpace(userPath))
        {
            var p = userPath.Trim().Trim('"');
            if (IsExe(p, FfmpegExe) && environment.FileExists(p))
                return new ToolLocation(p, ToolSource.UserSetting, "設定で指定したファイルを使用します。");
            warning = $"設定で指定した ffmpeg.exe が見つかりません（{p}）。";
        }
        if (Remembered(rememberedPath, FfmpegExe) is { } remembered)
            return new ToolLocation(remembered, ToolSource.Remembered,
                (warning is null ? "" : warning + " ") + "前回見つけた ffmpeg.exe を使用します。");
        var fromPath = FindInPath(FfmpegExe);
        if (fromPath is not null)
            return new ToolLocation(fromPath, ToolSource.PathEnvironment,
                (warning is null ? "" : warning + " ") + "PATH 上の ffmpeg.exe を使用します。");
        return new ToolLocation(null, ToolSource.NotFound,
            (warning is null ? "" : warning + " ") + "ffmpeg.exe が見つかりません。設定画面で指定するか PATH に追加してください。");
    }

    private ToolLocation LocateFfprobe(string? userPath, ToolLocation ffmpeg, string? rememberedPath)
    {
        string? warning = null;
        if (!string.IsNullOrWhiteSpace(userPath))
        {
            var p = userPath.Trim().Trim('"');
            if (IsExe(p, FfprobeExe) && environment.FileExists(p))
                return new ToolLocation(p, ToolSource.UserSetting, "設定で指定したファイルを使用します。");
            warning = $"設定で指定した ffprobe.exe が見つかりません（{p}）。";
        }
        if (ffmpeg.Path is not null)
        {
            var dir = Path.GetDirectoryName(ffmpeg.Path);
            if (!string.IsNullOrEmpty(dir))
            {
                var candidate = Path.Combine(dir, FfprobeExe);
                if (environment.FileExists(candidate))
                    return new ToolLocation(candidate, ToolSource.SameDirectoryAsFfmpeg,
                        (warning is null ? "" : warning + " ") + "ffmpeg.exe と同じフォルダの ffprobe.exe を使用します。");
            }
        }
        if (Remembered(rememberedPath, FfprobeExe) is { } remembered)
            return new ToolLocation(remembered, ToolSource.Remembered,
                (warning is null ? "" : warning + " ") + "前回見つけた ffprobe.exe を使用します。");
        var fromPath = FindInPath(FfprobeExe);
        if (fromPath is not null)
            return new ToolLocation(fromPath, ToolSource.PathEnvironment,
                (warning is null ? "" : warning + " ") + "PATH 上の ffprobe.exe を使用します。");
        return new ToolLocation(null, ToolSource.NotFound,
            (warning is null ? "" : warning + " ") + "ffprobe.exe が見つかりません。ffmpeg.exe と同じフォルダに置くか PATH に追加してください。");
    }

    private static bool IsExe(string path, string exeName)
        => string.Equals(Path.GetFileName(path), exeName, StringComparison.OrdinalIgnoreCase);

    /// <summary>PATH を先頭から検索する（<see cref="IToolEnvironment.GetPathVariables"/> の順）。相対ディレクトリ・不正な文字を含む要素は無視する。</summary>
    public string? FindInPath(string exeName)
    {
        foreach (var pathVar in environment.GetPathVariables())
        {
            if (string.IsNullOrEmpty(pathVar)) continue;
            if (FindInPathValue(pathVar, exeName) is { } found) return found;
        }
        return null;
    }

    private string? FindInPathValue(string pathVar, string exeName)
    {
        foreach (var raw in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
            if (dir.Length == 0 || dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0) continue;
            if (!Path.IsPathFullyQualified(dir)) continue;
            string candidate;
            try { candidate = Path.Combine(dir, exeName); }
            catch (ArgumentException) { continue; }
            if (environment.FileExists(candidate)) return candidate;
        }
        return null;
    }
}
