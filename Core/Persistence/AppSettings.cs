using System.Text.Json.Serialization;
using ManualVideoStudio.Core.Analysis;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Persistence;

/// <summary>アプリ設定（%APPDATA%\ManualVideoStudio\settings.json）。プロンプトテンプレートは別ファイル。</summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>ユーザーが明示指定した ffmpeg.exe。空なら PATH から探す。</summary>
    public string FfmpegPath { get; set; } = "";
    /// <summary>ユーザーが明示指定した ffprobe.exe（任意）。空なら ffmpeg と同じフォルダ → PATH。</summary>
    public string FfprobePath { get; set; } = "";
    /// <summary>
    /// 前回見つけた ffmpeg / ffprobe と、調べた版・機能（v1.6.1。アプリが記録する）。
    /// 次回の起動では、同じファイルならこれを使って ffmpeg を実行せずに済ませる。無ければ（null）探して調べる。
    /// 設定画面で ffmpeg / ffprobe の指定を変えると消す。
    /// </summary>
    public FFmpegCache? FfmpegCache { get; set; }

    /// <summary>新規プロジェクトの出力設定の既定値。</summary>
    public ExportSettings DefaultExport { get; set; } = new();

    public FrameCandidateSettings FrameCandidates { get; set; } = new();
    public AnalysisSettings Analysis { get; set; } = AnalysisSettings.FromPreset(AnalysisPreset.Standard);
    public UiSettings Ui { get; set; } = new();

    /// <summary>画面録画の設定。マイク録音は読み込み時に常に OFF に戻す。</summary>
    public RecordingSettings Recording { get; set; } = new();

    /// <summary>
    /// 字幕の見た目・フォント・上下の余白・新しい字幕の初期値（v1.5。設定画面の字幕タブ）。
    /// すべてのプロジェクトの書き出し・プレビューで使う（v1.4 まではプロジェクトごと）。
    /// </summary>
    public SubtitleStyle Subtitle { get; set; } = new();

    /// <summary>プロンプトの {{LANGUAGE}} に入れる言語名。</summary>
    public string Language { get; set; } = "日本語";

    /// <summary>
    /// 画面のモード（v1.11。<see cref="UiModes"/>）。簡易モードは手順書画像・手順書用の書き出し・AI 連携を隠す。
    /// 既定値を空にしておき、読み込み時に決める（<see cref="SettingsService.Load"/>）。
    /// System.Text.Json はファイルに無い項目に初期値を残すため、ここを simple にすると v1.10 以前の設定も簡易モードになってしまう。
    /// </summary>
    public string UiMode { get; set; } = "";

    [JsonIgnore]
    public bool IsSimpleMode => UiMode == UiModes.Simple;

    /// <summary>
    /// 外部ツール向けの文書作成用書き出し（手順書用の書き出し・その依頼文・章 / 操作文 / 補足の入力欄）を使うか（v1.14）。
    /// 既定は OFF（設定にこの項目が無い旧設定も OFF）。全機能モードやプロジェクトの内容から自動では ON にしない。
    /// アプリ共通の設定で、プロジェクトには保存しない。入口の表示と利用可否だけを切り替え、保存・出力の形式は変えない。
    /// </summary>
    public bool EnableExternalDocumentExport { get; set; }
}

/// <summary>画面のモードの値（v1.11）。</summary>
public static class UiModes
{
    public const string Simple = "simple";
    public const string Full = "full";

    /// <summary>知っている値ならそのまま、空・不明な値なら <paramref name="fallback"/>。</summary>
    public static string Normalize(string? mode, string fallback) => mode is Simple or Full ? mode : fallback;
}

public sealed class FrameCandidateSettings
{
    /// <summary>候補の間隔（秒）。</summary>
    public double StepSeconds { get; set; } = 0.2;
    /// <summary>登録時刻の前後それぞれの候補数（1 なら 3 枚）。</summary>
    public int CountEachSide { get; set; } = 2;
    /// <summary>画像ポイント一覧のサムネイル幅（px）。</summary>
    public int ThumbnailWidth { get; set; } = 240;

    /// <summary>center を中心とした候補時刻（範囲外は除外、昇順）。</summary>
    public IReadOnlyList<double> CandidateTimes(double center, double duration)
    {
        var list = new List<double>();
        int n = Math.Clamp(CountEachSide, 0, 10);
        double step = Math.Clamp(StepSeconds, 0.01, 10);
        for (int i = -n; i <= n; i++)
        {
            var t = Math.Round(center + i * step, 3);
            if (t < 0 || (duration > 0 && t > duration)) continue;
            list.Add(t);
        }
        return list;
    }
}

public sealed class UiSettings
{
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 900;
    public bool WindowMaximized { get; set; }
    public double RightPaneWidth { get; set; } = 460;
    public string LastVideoDirectory { get; set; } = "";
    public string LastProjectDirectory { get; set; } = "";
    public List<string> RecentProjects { get; set; } = [];
    /// <summary>「短縮」登録時の既定の残し秒数。</summary>
    public double DefaultShortenKeepSeconds { get; set; } = 1.5;
    /// <summary>Shift+矢印キーで移動する秒数。</summary>
    public double JumpSeconds { get; set; } = 1.0;
    /// <summary>起動時にプロジェクト一覧を表示する（v1.7）。</summary>
    public bool ShowProjectListOnStartup { get; set; } = true;
    /// <summary>最後にエクスポート・インポートした zip のフォルダー（v1.7）。空ならダウンロードフォルダー。</summary>
    public string LastArchiveDirectory { get; set; } = "";
}
