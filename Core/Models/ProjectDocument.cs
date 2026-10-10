using System.Text.Json.Serialization;

namespace ManualVideoStudio.Core.Models;

/// <summary>
/// プロジェクトファイル（*.mvsproj.json）の内容。元動画は埋め込まずパスで参照する。
/// 時刻はすべて元動画の先頭からの秒（double、ミリ秒精度）で保持する。
/// </summary>
public sealed class ProjectDocument
{
    /// <summary>
    /// 2: CapturePoint に注釈（annotations 等）を追加。
    /// 3: 編集段階（editPhase）・字幕（subtitleSegments / subtitleStyle）・GIF 書き出し設定・録画の記録を追加。
    /// 4: 字幕の見た目（subtitleStyle.preset）・フォントの選択・カットのトランジション（exportSettings.cutTransition）を追加。
    /// 5: 字幕ごとの上下のずらし（subtitleSegments[].verticalOffset）を追加。字幕の見た目・フォント・新しい字幕の初期値は
    ///    アプリ設定へ移し、subtitleStyle は書かない（v1.5）。
    /// 6: 動画に重ねる図形（shapeGroups）と、注釈の直線（annotations[].type = "line"）を追加（v1.6）。
    /// 8: 動画冒頭の黒背景タイトル（title: text / seconds / fadeIn。v1.15）を追加。旧形式はタイトルなしとして読む。
    /// 1～5 はそのまま読める（無い項目は既定値。subtitleStyle は読み飛ばし、字幕はアプリ設定の見た目で描く）。
    /// </summary>
    public const int CurrentSchemaVersion = 8;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string AppVersion { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public SourceVideo SourceVideo { get; set; } = new();
    public VideoMetadata? VideoMetadata { get; set; }

    /// <summary>確定済みの編集区間（Remove / Shorten）。</summary>
    public List<CutSegment> CutSegments { get; set; } = [];

    /// <summary>ローカル解析・AI による候補。確定編集とは別管理。</summary>
    public List<CutCandidate> CutCandidates { get; set; } = [];

    /// <summary>ローカル解析で検出した画面変化点（秒）。タイムライン表示用の参考情報。</summary>
    public List<double> SceneChanges { get; set; } = [];

    /// <summary>手順書画像の切り出しポイント。カット情報とは独立。</summary>
    public List<CapturePoint> CapturePoints { get; set; } = [];

    /// <summary>次に払い出す CapturePoint.Id。削除後も再利用しない。</summary>
    public int NextCaptureId { get; set; } = 1;

    /// <summary>編集の段階。Subtitle の間はカットを変更できない（字幕が 0 件なら確認なしで Cut へ戻れる）。</summary>
    public EditPhase EditPhase { get; set; } = EditPhase.Cut;

    /// <summary>手動字幕。時刻は出力動画（カット適用後）基準。MP4 / GIF で共通に使う。</summary>
    public List<SubtitleSegment> SubtitleSegments { get; set; } = [];

    /// <summary>動画に重ねる図形のまとまり（v1.6）。時刻は字幕と同じく出力動画基準。MP4 / GIF で共通に使う。</summary>
    public List<ShapeGroup> ShapeGroups { get; set; } = [];

    /// <summary>動画冒頭の黒背景タイトル（v1.15）。字幕・図形とは独立。文章が空ならタイトルなし。</summary>
    public VideoTitle Title { get; set; } = new();

    public List<AiHistoryEntry> AiHistory { get; set; } = [];
    public ExportSettings ExportSettings { get; set; } = new();

    /// <summary>Animated GIF の書き出し設定。</summary>
    public GifExportSettings ExportGifSettings { get; set; } = new();

    /// <summary>このアプリで録画した動画から作った場合の録画の記録（参考情報）。それ以外は null。</summary>
    public RecordingInfo? RecordingSettings { get; set; }

    /// <summary>字幕編集中（カットを変更できない状態）か。</summary>
    [JsonIgnore]
    public bool CutLockedBySubtitles => EditPhase == EditPhase.Subtitle;

    /// <summary>作成時に参照したアプリ設定の情報（再現性の記録。読み込み時に設定へは反映しない）。</summary>
    public AppSettingsReference? AppSettingsReference { get; set; }
}

public sealed class SourceVideo
{
    /// <summary>元動画の絶対パス。</summary>
    public string Path { get; set; } = "";

    /// <summary>プロジェクトファイルからの相対パス（動画とプロジェクトを一緒に移動した場合の再解決用）。</summary>
    public string? RelativePath { get; set; }

    public long FileSize { get; set; }
    public DateTimeOffset? LastWriteTime { get; set; }
}

public sealed record VideoMetadata
{
    public string Container { get; init; } = "";
    public string VideoCodec { get; init; } = "";
    public string AudioCodec { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; }
    public double Duration { get; init; }
    public string PixelFormat { get; init; } = "";
    /// <summary>bps。不明は 0。</summary>
    public long VideoBitrate { get; init; }
    /// <summary>bps。不明は 0。</summary>
    public long AudioBitrate { get; init; }
    public long FileSize { get; init; }
    public bool HasAudio { get; init; }
    public bool HasVideo { get; init; }

    [JsonIgnore]
    public string Resolution => Width > 0 && Height > 0 ? $"{Width}x{Height}" : "";
}

public sealed class CutSegment
{
    public string Id { get; set; } = NewId();
    public double Start { get; set; }
    public double End { get; set; }
    public SegmentAction Action { get; set; } = SegmentAction.Remove;
    /// <summary>Shorten のとき区間の先頭から残す秒数。Remove では無視。</summary>
    public double KeepSeconds { get; set; }
    public SegmentSource Source { get; set; } = SegmentSource.Manual;
    public double? Confidence { get; set; }
    public string Reason { get; set; } = "";

    [JsonIgnore]
    public double Duration => End - Start;

    /// <summary>この区間で出力から除かれる秒数。</summary>
    [JsonIgnore]
    public double RemovedSeconds => Action switch
    {
        SegmentAction.Remove => Duration,
        SegmentAction.Shorten => Math.Max(0, Duration - KeepSeconds),
        _ => 0,
    };

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];
}

public sealed class CutCandidate
{
    public string Id { get; set; } = CutSegment.NewId();
    public double Start { get; set; }
    public double End { get; set; }
    public SegmentAction SuggestedAction { get; set; } = SegmentAction.Shorten;
    public double KeepSeconds { get; set; }
    /// <summary>0.0～1.0。</summary>
    public double Confidence { get; set; }
    public string Reason { get; set; } = "";
    public SegmentSource Source { get; set; } = SegmentSource.LocalAnalysis;
    public CandidateKind Kind { get; set; }
    public CandidateStatus Status { get; set; } = CandidateStatus.Pending;
    /// <summary>採用時に作成した CutSegment の Id。</summary>
    public string? AcceptedSegmentId { get; set; }
    /// <summary>AI 候補の場合、元になった AiHistoryEntry の Id。</summary>
    public string? AiHistoryId { get; set; }

    [JsonIgnore]
    public double Duration => End - Start;
}

public sealed class CapturePoint
{
    /// <summary>主キー。Gemini との照合に使う。払い出し後は変更しない。</summary>
    public int Id { get; set; }
    /// <summary>1 始まりの表示・出力順。SelectedFrameTime 順に自動で振り直す。</summary>
    public int Sequence { get; set; }
    /// <summary>ユーザーが登録した時刻。</summary>
    public double Timestamp { get; set; }
    /// <summary>前後フレーム比較で最終的に選んだ時刻。画像はこの時刻から切り出す。</summary>
    public double SelectedFrameTime { get; set; }
    public string Caption { get; set; } = "";
    public CaptionSource CaptionSource { get; set; } = CaptionSource.None;
    /// <summary>最後に画像を書き出したときのファイル名（拡張子付き）。未出力は空。</summary>
    public string OutputFilename { get; set; } = "";

    /// <summary>画像の注釈（四角形・矢印・直線・文字・モザイク）。画像の書き出し・注釈の保存時に焼き込む。旧形式では無い（空）。</summary>
    public List<Annotation> Annotations { get; set; } = [];
    /// <summary>注釈座標の基準にした画像の幅（px）。注釈が無ければ 0。</summary>
    public int AnnotationImageWidth { get; set; }
    /// <summary>注釈座標の基準にした画像の高さ（px）。注釈が無ければ 0。</summary>
    public int AnnotationImageHeight { get; set; }

    /// <summary>手順書の操作文（v1.9。1 行・サニタイズしない）。空なら手順書ではキャプションを使う。</summary>
    public string StepText { get; set; } = "";
    /// <summary>手順書の補足（確認ポイント・入力値など。v1.9。改行は LF）。</summary>
    public string Note { get; set; } = "";
    /// <summary>この画像から始まる章の名前（v1.9）。空なら前の画像と同じ章。</summary>
    public string ChapterTitle { get; set; } = "";
}

/// <summary>手順書の文（操作文・補足・章名。v1.9）の正規化。</summary>
public static class CaptureText
{
    /// <summary>1 行の文（操作文・章名）: 改行・タブは空白にし、前後の空白を除く。null は空。</summary>
    public static string Line(string? text) => (text ?? "").ReplaceLineEndings(" ").Replace('\t', ' ').Trim();

    /// <summary>補足: 改行は LF にそろえ、前後の空白・空行を除く。null は空。</summary>
    public static string Note(string? text) => (text ?? "").ReplaceLineEndings("\n").Trim();
}

/// <summary>画像ポイントの章（v1.9）。連番順に見て、ChapterTitle のある画像から新しい章が始まる。</summary>
public static class CaptureChapters
{
    /// <summary>Id → (属する章の名前（最初の章の前なら null）, この画像から章が始まるか)。</summary>
    public static Dictionary<int, (string? Chapter, bool Start)> Resolve(IEnumerable<CapturePoint> points)
    {
        var result = new Dictionary<int, (string?, bool)>();
        string? current = null;
        foreach (var p in points.OrderBy(p => p.Sequence))
        {
            var start = !string.IsNullOrWhiteSpace(p.ChapterTitle);
            if (start) current = p.ChapterTitle.Trim();
            result[p.Id] = (current, start);
        }
        return result;
    }
}

public sealed class AiHistoryEntry
{
    public string Id { get; set; } = CutSegment.NewId();
    public AiPurpose Purpose { get; set; }
    public string TemplateName { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Response { get; set; } = "";
    public DateTimeOffset? PromptCreatedAt { get; set; }
    public DateTimeOffset? ParsedAt { get; set; }
    public DateTimeOffset? ImportedAt { get; set; }
    public bool ParseSucceeded { get; set; }
    public string ParseMessage { get; set; } = "";
    /// <summary>取り込んだ結果の要約（人間向け）。</summary>
    public string ImportSummary { get; set; } = "";
    /// <summary>取り込んだ結果の JSON（キャプション一覧・候補一覧など）。</summary>
    public string ImportedJson { get; set; } = "";
}

public sealed class ExportSettings
{
    /// <summary>動画の出力先フォルダ。空なら元動画と同じフォルダ（v1.4 以降は元動画をプロジェクトフォルダーに置くため、プロジェクトフォルダー）。</summary>
    public string VideoOutputDirectory { get; set; } = "";
    /// <summary>動画の出力ファイル名。空なら「元ファイル名_edited.mp4」。</summary>
    public string VideoOutputFileName { get; set; } = "";
    /// <summary>画像の出力先フォルダ。空なら元動画と同じフォルダの「images」（v1.4。以前は「元ファイル名_images」）。</summary>
    public string ImageOutputDirectory { get; set; } = "";
    public ImageFormat ImageFormat { get; set; } = ImageFormat.Png;
    /// <summary>JPEG 品質（ffmpeg -q:v、2=高画質～31=低画質）。</summary>
    public int JpegQuality { get; set; } = 2;
    public bool WriteMetadataJson { get; set; } = true;
    /// <summary>x264 CRF。</summary>
    public int VideoCrf { get; set; } = 22;
    public string VideoPreset { get; set; } = "medium";
    public int AudioBitrateKbps { get; set; } = 192;
    /// <summary>カットのつなぎ目に短いクロスフェードを入れる（v1.4。MP4 / GIF 共通。出力の長さは変わらない）。</summary>
    public bool CutTransition { get; set; } = true;
    /// <summary>手順書用の書き出しの出力先（v1.8）。空なら書き出しの既定のフォルダーの「tejunsho」。</summary>
    public string TejunshoOutputDirectory { get; set; } = "";

    /// <summary>クロスフェードの長さ（秒）。削る部分から前後 半分ずつをのりしろにする。</summary>
    public const double CutTransitionSeconds = 0.3;

    /// <summary>書き出しに渡すトランジションの秒数（しないなら 0）。</summary>
    [JsonIgnore]
    public double TransitionSecondsForExport => CutTransition ? CutTransitionSeconds : 0;
}

public sealed class AppSettingsReference
{
    public string FfmpegPath { get; set; } = "";
    public string FfmpegVersion { get; set; } = "";
    public string AnalysisPreset { get; set; } = "";
}
