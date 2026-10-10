using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Ai;
using ManualVideoStudio.Core.Analysis;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Services;

namespace ManualVideoStudio.ViewModels;

public enum SettingsTab
{
    FFmpeg,
    Output,
    Subtitle,
    Frames,
    Analysis,
    Prompts,
    Operation,
}

public sealed record Choice<T>(T Value, string Label);

/// <summary>設定画面。OK で AppSettings に反映して保存する。プロンプトテンプレートは［保存］で個別に保存する。</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IPromptTemplateService _templates;
    private readonly IDialogService _dialogs;
    private readonly SubtitleCheckService _subtitleCheck;
    private bool _applyingPreset;
    private bool _detectedWithUnsavedPaths;

    public SettingsViewModel(ISettingsService settings, FFmpegStatus ffmpeg, IPromptTemplateService templates, IDialogService dialogs,
        SubtitleCheckService subtitleCheck)
    {
        _settings = settings;
        _templates = templates;
        _dialogs = dialogs;
        _subtitleCheck = subtitleCheck;
        FFmpeg = ffmpeg;
        var s = settings.Current;

        _ffmpegPath = s.FfmpegPath;
        _ffprobePath = s.FfprobePath;

        _projectRootDirectory = s.Recording.ResolveOutputDirectory();
        var e = s.DefaultExport;
        _videoOutputDirectory = e.VideoOutputDirectory;
        _imageOutputDirectory = e.ImageOutputDirectory;
        _isJpeg = e.ImageFormat == ImageFormat.Jpeg;
        _jpegQuality = e.JpegQuality;
        _writeMetadataJson = e.WriteMetadataJson;
        _videoCrf = e.VideoCrf;
        _videoPreset = e.VideoPreset;
        _audioBitrateKbps = e.AudioBitrateKbps;
        _cutTransition = e.CutTransition;

        _stepSeconds = s.FrameCandidates.StepSeconds;
        _countEachSide = s.FrameCandidates.CountEachSide;
        _thumbnailWidth = s.FrameCandidates.ThumbnailWidth;

        LoadAnalysis(s.Analysis);

        _defaultShortenKeepSeconds = s.Ui.DefaultShortenKeepSeconds;
        _jumpSeconds = s.Ui.JumpSeconds;
        _language = s.Language;
        _isSimpleMode = s.IsSimpleMode;
        _enableExternalDocumentExport = s.EnableExternalDocumentExport;
        _uiContext = SynchronizationContext.Current;

        LoadSubtitle(s.Subtitle ?? new SubtitleStyle());
        _ = LoadSubtitleFontsAsync();

        _selectedPurpose = AiPurpose.CaptureCaption;
        _templateText = templates.Get(_selectedPurpose);
        _isTemplateCustomized = templates.IsCustomized(_selectedPurpose);
        RefreshPurposes();
    }

    public event Action<bool>? CloseRequested;
    public FFmpegStatus FFmpeg { get; }
    [ObservableProperty] private SettingsTab _selectedTab;

    /// <summary>OK で閉じたとき、出力設定を開いているプロジェクトにも適用するか。</summary>
    [ObservableProperty] private bool _applyOutputToProject = true;
    public bool Saved { get; private set; }

    // ---------------- FFmpeg ----------------
    [ObservableProperty] private string _ffmpegPath;
    [ObservableProperty] private string _ffprobePath;

    [RelayCommand]
    private void BrowseFfmpeg()
    {
        var p = _dialogs.OpenFile("ffmpeg.exe|ffmpeg.exe|実行ファイル (*.exe)|*.exe", DirOf(FfmpegPath), "ffmpeg.exe を選択");
        if (p is not null) FfmpegPath = p;
    }

    [RelayCommand]
    private void BrowseFfprobe()
    {
        var p = _dialogs.OpenFile("ffprobe.exe|ffprobe.exe|実行ファイル (*.exe)|*.exe", DirOf(FfprobePath) ?? DirOf(FfmpegPath), "ffprobe.exe を選択");
        if (p is not null) FfprobePath = p;
    }

    [RelayCommand]
    private void ClearFfmpeg()
    {
        FfmpegPath = "";
        FfprobePath = "";
    }

    [RelayCommand]
    private async Task DetectAsync()
    {
        _detectedWithUnsavedPaths = true;
        await FFmpeg.RefreshAsync(FfmpegPath, FfprobePath);
    }

    private static string? DirOf(string path)
        => string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);

    // ---------------- 出力 ----------------
    /// <summary>プロジェクトの保存先（この直下に「日時_名前」のプロジェクトフォルダーを作る。録画画面の保存先と同じ設定）。</summary>
    [ObservableProperty] private string _projectRootDirectory;
    [ObservableProperty] private string _videoOutputDirectory;
    [ObservableProperty] private string _imageOutputDirectory;
    [ObservableProperty] private bool _isJpeg;
    [ObservableProperty] private int _jpegQuality;
    [ObservableProperty] private bool _writeMetadataJson;
    [ObservableProperty] private int _videoCrf;
    [ObservableProperty] private string _videoPreset;
    [ObservableProperty] private int _audioBitrateKbps;
    /// <summary>カットのつなぎ目にクロスフェードを入れる（v1.4）。</summary>
    [ObservableProperty] private bool _cutTransition;
    public IReadOnlyList<string> Presets { get; } = ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];

    [RelayCommand]
    private void BrowseProjectRoot()
    {
        var p = _dialogs.PickFolder(ProjectRootDirectory, "プロジェクトの保存先");
        if (p is not null) ProjectRootDirectory = p;
    }

    [RelayCommand]
    private void BrowseVideoOutput()
    {
        var p = _dialogs.PickFolder(VideoOutputDirectory, "動画の既定の出力先");
        if (p is not null) VideoOutputDirectory = p;
    }

    [RelayCommand]
    private void BrowseImageOutput()
    {
        var p = _dialogs.PickFolder(ImageOutputDirectory, "画像の既定の出力先");
        if (p is not null) ImageOutputDirectory = p;
    }

    // ---------------- 字幕（v1.5。すべてのプロジェクトに共通） ----------------

    /// <summary>字幕の見た目の選択肢（SubtitlePreset の順）。</summary>
    public static IReadOnlyList<string> SubtitlePresetLabels { get; } = ["白文字＋黒の縁取り", "白文字＋半透明の黒帯", "白文字＋黒背景", "黒文字＋白背景"];
    public IReadOnlyList<Choice<double>> SubtitleMargins { get; } = SubtitleStyle.MarginChoices
        .Select(m => new Choice<double>(m, $"高さの {m / 1080 * 100:0.#}%" + (m == SubtitleStyle.DefaultMarginVertical ? "（既定）" : ""))).ToList();
    public IReadOnlyList<double> SubtitleFontSizes => SubtitleStyle.FontSizes;
    public ObservableCollection<FontChoice> SubtitleFontChoices { get; } = [];

    /// <summary>確認画像に使う場面（設定画面を開いたときの主画面の状態。WindowService が渡す）。</summary>
    public SubtitleSampleContext SubtitleSample { get; set; } = SubtitleSampleContext.Empty;

    [ObservableProperty] private int _subtitlePresetIndex;
    [ObservableProperty] private FontChoice? _subtitleFont;
    [ObservableProperty] private string _subtitleFontNote = "";
    [ObservableProperty] private double _subtitleMargin;
    [ObservableProperty] private double _subtitleDefaultFontSize;
    [ObservableProperty] private int _subtitleDefaultPositionIndex;
    [ObservableProperty] private ImageSource? _sampleImage;
    [ObservableProperty] private string _sampleMessage = "";
    [ObservableProperty] private string _sampleFontWarning = "";
    [ObservableProperty] private bool _isSampleBusy;

    /// <summary>フォント一覧を読み終わる前・一覧に無い場合も保存値を保つための名前。</summary>
    private string _subtitleFontName = SubtitleStyle.DefaultFontFamily;
    private CancellationTokenSource? _sampleCts;
    private bool _loadingSubtitle;

    public string SampleSubjectText => SubtitleSample.Segment is { } s
        ? $"確認画像は再生位置（選択中）の字幕「{Shorten(s.Text)}」で描きます（文字サイズ・位置はその字幕の値）。"
        : "確認画像は例の文で、新しい字幕の文字サイズ・位置で描きます。";

    private static string Shorten(string text)
    {
        var t = text.Replace("\n", " ");
        return t.Length > 20 ? t[..20] + "…" : t;
    }

    private void LoadSubtitle(SubtitleStyle st)
    {
        _loadingSubtitle = true;
        SubtitlePresetIndex = (int)(Enum.IsDefined(st.Preset) ? st.Preset : SubtitleStyle.DefaultPreset);
        _subtitleFontName = SubtitleStyle.NormalizeFontFamily(st.FontFamily);
        SubtitleMargin = SubtitleStyle.MarginChoices.MinBy(m => Math.Abs(m - st.MarginVertical));
        SubtitleDefaultFontSize = SubtitleStyle.NormalizeFontSize(st.FontSize);
        SubtitleDefaultPositionIndex = (int)st.Position;
        SelectFont(_subtitleFontName);
        _loadingSubtitle = false;
    }

    private SubtitleStyle BuildSubtitle()
    {
        var st = new SubtitleStyle
        {
            Preset = (SubtitlePreset)Math.Clamp(SubtitlePresetIndex, 0, SubtitlePresetLabels.Count - 1),
            FontFamily = SubtitleFont?.Name ?? _subtitleFontName,
            MarginVertical = SubtitleMargin,
            FontSize = SubtitleDefaultFontSize,
            Position = (SubtitlePosition)Math.Clamp(SubtitleDefaultPositionIndex, 0, 2),
        };
        st.Normalize();
        return st;
    }

    private async Task LoadSubtitleFontsAsync()
    {
        SubtitleFontNote = "フォントの一覧を読み込んでいます…";
        var fonts = await SubtitleFonts.LoadAsync();
        foreach (var f in fonts) SubtitleFontChoices.Add(f);
        _loadingSubtitle = true;
        SelectFont(_subtitleFontName);
        _loadingSubtitle = false;
        if (fonts.Count == 0) SubtitleFontNote = "フォントの一覧を読み込めませんでした（保存済みのフォント名をそのまま使います）。";
        if (SelectedTab == SettingsTab.Subtitle) ScheduleSample();
    }

    /// <summary>保存値のフォントを一覧から選ぶ。この PC に無ければメイリオなどに替え、その旨を出す。</summary>
    private void SelectFont(string name)
    {
        if (SubtitleFontChoices.Count == 0) return;
        SubtitleFont = FontCatalog.Resolve(SubtitleFontChoices, name, out var note);
        SubtitleFontNote = note ?? FontNoteFor(SubtitleFont);
    }

    private static string FontNoteFor(FontChoice? f) => f is { SupportsJapanese: false }
        ? "このフォントには日本語の文字がありません（日本語は別のフォントで描かれます）。" : "";

    partial void OnSubtitleFontChanged(FontChoice? value)
    {
        if (value is not null) _subtitleFontName = value.Name;
        if (!_loadingSubtitle) SubtitleFontNote = FontNoteFor(value);
    }

    partial void OnSelectedTabChanged(SettingsTab value)
    {
        // 簡易モードで隠すタブ（前後フレーム・プロンプト）は開かず、「操作・その他」を開く（v1.11）。
        if (IsSimpleMode && IsFullModeTab(value))
        {
            SelectedTab = SettingsTab.Operation;
            return;
        }
        if (value == SettingsTab.Subtitle && SampleImage is null && !IsSampleBusy) ScheduleSample();
    }

    [RelayCommand]
    private void ResetSubtitle() => LoadSubtitle(new SubtitleStyle());

    [RelayCommand]
    private void RefreshSample() => ScheduleSample(immediately: true);

    /// <summary>確認画像を作り直す（値を変えるたびに少し待ってから。前の要求は取り消す）。</summary>
    private void ScheduleSample(bool immediately = false)
    {
        _sampleCts?.Cancel();
        var cts = _sampleCts = new CancellationTokenSource();
        _ = RenderSampleAsync(immediately ? 0 : 400, cts.Token);
    }

    private async Task RenderSampleAsync(int delayMs, CancellationToken ct)
    {
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs, ct);
            IsSampleBusy = true;
            SampleMessage = "確認画像を作っています…";
            var style = BuildSubtitle();
            var seg = SubtitleSample.Segment is { } s
                ? new SubtitleSegment { Text = s.Text, Position = s.Position, FontSize = s.FontSize, VerticalOffset = s.VerticalOffset }
                : new SubtitleSegment { Text = SubtitleCheckService.SampleText, Position = style.Position, FontSize = style.FontSize };
            var r = await _subtitleCheck.RenderAsync(SubtitleSample, seg, style, ct);
            if (ct.IsCancellationRequested) return;
            SampleImage = r.Image ?? SampleImage;
            SampleMessage = r.Message;
            SampleFontWarning = r.FontWarning ?? "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SampleMessage = "確認画像を作れませんでした: " + ex.Message;
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsSampleBusy = false;
        }
    }

    // ---------------- 前後フレーム ----------------
    [ObservableProperty] private double _stepSeconds;
    [ObservableProperty] private int _countEachSide;
    [ObservableProperty] private int _thumbnailWidth;

    // ---------------- 自動解析 ----------------
    public IReadOnlyList<Choice<AnalysisPreset>> AnalysisPresets { get; } =
    [
        new(AnalysisPreset.Careful, "慎重（長い静止だけを候補にする）"),
        new(AnalysisPreset.Standard, "標準"),
        new(AnalysisPreset.Aggressive, "積極的（短い静止・無音も候補にする）"),
        new(AnalysisPreset.Custom, "カスタム"),
    ];

    [ObservableProperty] private AnalysisPreset _analysisPreset;
    [ObservableProperty] private bool _enableFreeze;
    [ObservableProperty] private bool _enableSilence;
    [ObservableProperty] private bool _enableSceneChange;
    [ObservableProperty] private double _freezeMinSeconds;
    [ObservableProperty] private double _freezeNoiseDb;
    [ObservableProperty] private double _silenceMinSeconds;
    [ObservableProperty] private double _silenceNoiseDb;
    [ObservableProperty] private double _sceneThreshold;
    [ObservableProperty] private double _edgeMarginSeconds;
    [ObservableProperty] private double _shortenKeepSeconds;
    [ObservableProperty] private bool _includeSilenceOnly;
    [ObservableProperty] private int _scaleWidth;

    private void LoadAnalysis(AnalysisSettings a)
    {
        _applyingPreset = true;
        AnalysisPreset = a.Preset;
        EnableFreeze = a.EnableFreeze;
        EnableSilence = a.EnableSilence;
        EnableSceneChange = a.EnableSceneChange;
        FreezeMinSeconds = a.FreezeMinSeconds;
        FreezeNoiseDb = a.FreezeNoiseDb;
        SilenceMinSeconds = a.SilenceMinSeconds;
        SilenceNoiseDb = a.SilenceNoiseDb;
        SceneThreshold = a.SceneThreshold;
        EdgeMarginSeconds = a.EdgeMarginSeconds;
        ShortenKeepSeconds = a.ShortenKeepSeconds;
        IncludeSilenceOnly = a.IncludeSilenceOnly;
        ScaleWidth = a.ScaleWidth;
        _applyingPreset = false;
    }

    private AnalysisSettings BuildAnalysis() => new()
    {
        Preset = AnalysisPreset,
        EnableFreeze = EnableFreeze,
        EnableSilence = EnableSilence,
        EnableSceneChange = EnableSceneChange,
        FreezeMinSeconds = FreezeMinSeconds,
        FreezeNoiseDb = FreezeNoiseDb,
        SilenceMinSeconds = SilenceMinSeconds,
        SilenceNoiseDb = SilenceNoiseDb,
        SceneThreshold = SceneThreshold,
        EdgeMarginSeconds = EdgeMarginSeconds,
        ShortenKeepSeconds = ShortenKeepSeconds,
        IncludeSilenceOnly = IncludeSilenceOnly,
        ScaleWidth = ScaleWidth,
    };

    partial void OnAnalysisPresetChanged(AnalysisPreset value)
    {
        if (_applyingPreset || value == AnalysisPreset.Custom) return;
        var enabled = (EnableFreeze, EnableSilence, EnableSceneChange);
        LoadAnalysis(AnalysisSettings.FromPreset(value));
        (EnableFreeze, EnableSilence, EnableSceneChange) = enabled;
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // 字幕の値を変えたら確認画像を作り直す（字幕タブを開いているときだけ）。
        if (!_loadingSubtitle && SelectedTab == SettingsTab.Subtitle && e.PropertyName is nameof(SubtitlePresetIndex) or nameof(SubtitleFont)
            or nameof(SubtitleMargin) or nameof(SubtitleDefaultFontSize) or nameof(SubtitleDefaultPositionIndex))
            ScheduleSample();
        // 個別値を変えたらプリセットは「カスタム」にする。
        if (!_applyingPreset && e.PropertyName is nameof(FreezeMinSeconds) or nameof(FreezeNoiseDb) or nameof(SilenceMinSeconds)
            or nameof(SilenceNoiseDb) or nameof(SceneThreshold) or nameof(EdgeMarginSeconds) or nameof(ShortenKeepSeconds)
            or nameof(IncludeSilenceOnly) or nameof(ScaleWidth))
        {
            _applyingPreset = true;
            AnalysisPreset = AnalysisPreset.Custom;
            _applyingPreset = false;
        }
    }

    // ---------------- 操作 ----------------
    [ObservableProperty] private double _defaultShortenKeepSeconds;
    [ObservableProperty] private double _jumpSeconds;
    [ObservableProperty] private string _language;

    /// <summary>
    /// 画面のモード（v1.11）。簡易モードは手順書画像・手順書用の書き出し・AI 連携を隠す。
    /// この画面の中でも、選んだモードに合わせて手順書・AI 用の項目をその場で出し分ける（保存は OK のとき）。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFullMode))]
    private bool _isSimpleMode;
    public bool IsFullMode => !IsSimpleMode;

    partial void OnIsSimpleModeChanged(bool value)
    {
        if (value && IsFullModeTab(SelectedTab)) SelectedTab = SettingsTab.Operation;
        OnPropertyChanged(nameof(ShowExternalDocumentSettings));
        RefreshPurposes();
    }

    // ---------------- 詳細・外部連携（v1.14） ----------------

    /// <summary>
    /// 外部ツール向けの文書作成用書き出しを有効にする（下書き。［OK］で保存に成功したあと主画面へ反映する。キャンセルでは変えない）。
    /// 画面のモードは自動では変えない。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowExternalDocumentSettings))]
    private bool _enableExternalDocumentExport;

    /// <summary>この画面で、文書作成用の項目（プロンプトの依頼文など）を出す条件: 全機能モード かつ 有効。</summary>
    public bool ShowExternalDocumentSettings => IsFullMode && EnableExternalDocumentExport;

    private readonly SynchronizationContext? _uiContext;
    private bool _revertingExternalDocument;
    private bool _revertExternalDocumentPending;

    /// <summary>OFF にすると依頼文が非表示になる。選択中で未保存の編集があれば、保存 / 破棄 / キャンセルを選ばせる（キャンセルなら OFF にしない）。</summary>
    partial void OnEnableExternalDocumentExportChanging(bool oldValue, bool newValue)
    {
        if (_revertingExternalDocument || newValue || !oldValue) return;
        if (SelectedPurpose != AiPurpose.TejunshoRequest || !_templateDirty) return;
        var answer = _dialogs.ConfirmYesNoCancel(Loc.Get("Doc.DirtyOff"), Loc.Get("Doc.DirtyOffTitle"));
        if (answer is null) { _revertExternalDocumentPending = true; return; }
        if (answer == true) SaveTemplateFor(AiPurpose.TejunshoRequest);
        _templateDirty = false; // 破棄（保存済みのテンプレートは削除しない）
    }

    partial void OnEnableExternalDocumentExportChanged(bool value)
    {
        if (_revertExternalDocumentPending)
        {
            _revertExternalDocumentPending = false;
            // チェックボックスの変更の最中に同じ値を書き戻すと画面が戻らないため、UI の文脈で少し遅らせて戻す（文脈が無い場合はすぐ戻す）。
            void Revert()
            {
                _revertingExternalDocument = true;
                try { EnableExternalDocumentExport = true; }
                finally { _revertingExternalDocument = false; }
            }
            if (_uiContext is not null) _uiContext.Post(_ => Revert(), null);
            else Revert();
            return;
        }
        RefreshPurposes();
    }

    /// <summary>全機能モードでだけ出すタブ。</summary>
    private static bool IsFullModeTab(SettingsTab tab) => tab is SettingsTab.Frames or SettingsTab.Prompts;

    /// <summary>設定画面に出すアプリ名・バージョン・著作権表示。</summary>
    public string AboutText => $"Manual Video Studio v{ProjectService.AppVersion}　© ja1bus";

    // ---------------- プロンプト ----------------
    /// <summary>AI 連携の 3 種と、手順書作成の依頼文（v1.9）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<Choice<AiPurpose>> Purposes { get; } =
        new([.. AiAssistViewModel.PurposeChoices, new Choice<AiPurpose>(AiPurpose.SubtitleRequest, "Gemini への字幕依頼の文（字幕編集の［Geminiで字幕を作る］でコピー）")]);

    private static readonly Choice<AiPurpose> DocumentPurpose = new(AiPurpose.TejunshoRequest, Loc.Get("Doc.PurposeLabel"));

    /// <summary>
    /// 選べる用途を更新する（v1.14）。文書作成用の依頼文（TejunshoRequest）は、全機能モード かつ 外部文書連携が有効のときだけ出す。
    /// 非表示にしても保存済みのテンプレートは消さず、初期値にも戻さない。有効に戻せば編集済みの依頼文をまた使える。
    /// </summary>
    private void RefreshPurposes()
    {
        var present = Purposes.Any(c => c.Value == AiPurpose.TejunshoRequest);
        if (ShowExternalDocumentSettings && !present)
        {
            Purposes.Insert(Purposes.Count - 1, DocumentPurpose); // 字幕依頼の手前
        }
        else if (!ShowExternalDocumentSettings && present)
        {
            // 非表示になった用途が選択されたまま残らないようにする（未保存の編集は、OFF にする前に確認済み）。
            if (SelectedPurpose == AiPurpose.TejunshoRequest) SelectedPurpose = AiPurpose.CaptureCaption;
            Purposes.Remove(Purposes.First(c => c.Value == AiPurpose.TejunshoRequest));
        }
    }

    private bool IsPurposeVisible(AiPurpose purpose) => Purposes.Any(c => c.Value == purpose);
    /// <summary>選んだテンプレートで使える変数。</summary>
    public IReadOnlyList<VariableInfo> Variables => VariablesFor(SelectedPurpose);

    /// <summary>テンプレートの説明（依頼文は AI の回答を取り込まないため、回答形式の注意を出さない）。</summary>
    public string TemplateHint => SelectedPurpose == AiPurpose.SubtitleRequest
        ? "{{変数名}} は字幕依頼の画面でコピーするときに置き換わります。動画の内容は Gemini に貼り付けた後で書き換えます。回答は SRT をコードブロックで返す形のままにしてください（取り込みは SRT の部分だけを読みます。字幕の見た目・位置は提案させず、アプリの字幕の設定を使います）。"
        : SelectedPurpose == AiPurpose.TejunshoRequest
        ? "{{変数名}} は書き出しの完了時に置き換わります（{{OUTPUT_DIR_HOME}} か {{OUTPUT_DIR}} が必要。別の PC で手順書を作るなら {{OUTPUT_DIR_HOME}}）。未定義の変数はそのまま残り、完了画面に警告を表示します。Claude Code に貼り付ける文なので、複数行でもかまいません。"
        : "{{変数名}} はプロンプト生成時に置き換わります。未定義の変数はそのまま残り、警告を表示します。回答の JSON 形式（キー名）を変えると取り込めなくなるので注意してください。";

    private static IReadOnlyList<VariableInfo> VariablesFor(AiPurpose purpose)
        => PromptVariables.For(purpose).Select(v => new VariableInfo("{{" + v.Name + "}}", v.Description)).ToList();
    [ObservableProperty] private AiPurpose _selectedPurpose;
    [ObservableProperty] private string _templateText;
    [ObservableProperty] private bool _isTemplateCustomized;
    [ObservableProperty] private string _templateMessage = "";
    private bool _templateDirty;
    private bool _loadingTemplate;

    partial void OnSelectedPurposeChanging(AiPurpose oldValue, AiPurpose newValue)
    {
        if (!_templateDirty) return;
        if (_dialogs.Confirm($"{oldValue} のテンプレートの変更を保存しますか？（キャンセルで破棄）", "テンプレート"))
            SaveTemplateFor(oldValue);
        _templateDirty = false;
    }

    partial void OnSelectedPurposeChanged(AiPurpose value)
    {
        OnPropertyChanged(nameof(Variables));
        OnPropertyChanged(nameof(TemplateHint));
        LoadTemplate();
    }

    partial void OnTemplateTextChanged(string value)
    {
        if (_loadingTemplate) return;
        _templateDirty = true;
        TemplateMessage = "未保存の変更があります。";
    }

    private void LoadTemplate()
    {
        _loadingTemplate = true;
        TemplateText = _templates.Get(SelectedPurpose);
        _loadingTemplate = false;
        IsTemplateCustomized = _templates.IsCustomized(SelectedPurpose);
        _templateDirty = false;
        TemplateMessage = ValidateTemplate(SelectedPurpose, TemplateText);
    }

    private static string ValidateTemplate(AiPurpose purpose, string text)
    {
        var vars = PromptRenderer.FindVariables(text);
        var known = PromptVariables.For(purpose).Select(v => v.Name).ToHashSet();
        var msgs = new List<string>();
        var unknown = vars.Where(v => !known.Contains(v)).ToList();
        if (unknown.Count > 0) msgs.Add("未定義の変数: " + string.Join(", ", unknown.Select(v => "{{" + v + "}}")));
        var missing = PromptVariables.MissingRequired(purpose, text);
        if (missing.Count > 0) msgs.Add("必要な変数がありません: " + string.Join(", ", missing));
        return string.Join(" / ", msgs);
    }

    private void SaveTemplateFor(AiPurpose purpose)
    {
        try
        {
            _templates.Save(purpose, TemplateText);
        }
        catch (Exception ex)
        {
            _dialogs.ShowError(ex.Message);
        }
    }

    [RelayCommand]
    private void SaveTemplate()
    {
        if (!IsPurposeVisible(SelectedPurpose)) return;
        SaveTemplateFor(SelectedPurpose);
        LoadTemplate();
        if (TemplateMessage.Length == 0) TemplateMessage = "保存しました。";
    }

    [RelayCommand]
    private void ResetTemplate()
    {
        if (!IsPurposeVisible(SelectedPurpose)) return;
        if (!_dialogs.Confirm($"{SelectedPurpose} のテンプレートを初期値に戻しますか？編集内容は失われます。")) return;
        _templates.ResetToDefault(SelectedPurpose);
        LoadTemplate();
        TemplateMessage = "初期値に戻しました。";
    }

    // ---------------- OK / キャンセル ----------------

    [RelayCommand]
    private void Ok()
    {
        if (_templateDirty && _dialogs.Confirm("プロンプトテンプレートの変更を保存しますか？（キャンセルで破棄）", "テンプレート"))
            SaveTemplateFor(SelectedPurpose);

        var s = _settings.Current;
        var previousExternal = s.EnableExternalDocumentExport;
        var previousMode = s.UiMode;
        var ffmpegPath = FfmpegPath.Trim().Trim('"');
        var ffprobePath = FfprobePath.Trim().Trim('"');
        // 指定を変えたら、前回見つけた ffmpeg の記録を消して探し直させる（v1.6.1）。
        if (ffmpegPath != s.FfmpegPath || ffprobePath != s.FfprobePath) s.FfmpegCache = null;
        s.FfmpegPath = ffmpegPath;
        s.FfprobePath = ffprobePath;
        s.DefaultExport = new ExportSettings
        {
            VideoOutputDirectory = VideoOutputDirectory.Trim(),
            ImageOutputDirectory = ImageOutputDirectory.Trim(),
            ImageFormat = IsJpeg ? ImageFormat.Jpeg : ImageFormat.Png,
            JpegQuality = Math.Clamp(JpegQuality, 2, 31),
            WriteMetadataJson = WriteMetadataJson,
            VideoCrf = Math.Clamp(VideoCrf, 0, 51),
            VideoPreset = Presets.Contains(VideoPreset) ? VideoPreset : "medium",
            AudioBitrateKbps = Math.Clamp(AudioBitrateKbps, 32, 512),
            CutTransition = CutTransition,
        };
        var root = ProjectRootDirectory.Trim().Trim('"');
        s.Recording.OutputDirectory = root.Length == 0 || string.Equals(root, RecordingSettings.DefaultOutputDirectory(), StringComparison.OrdinalIgnoreCase) ? "" : root;
        s.FrameCandidates.StepSeconds = Math.Clamp(StepSeconds, 0.01, 10);
        s.FrameCandidates.CountEachSide = Math.Clamp(CountEachSide, 1, 5);
        s.FrameCandidates.ThumbnailWidth = Math.Clamp(ThumbnailWidth, 80, 640);
        s.Analysis = BuildAnalysis();
        s.Ui.DefaultShortenKeepSeconds = Math.Clamp(DefaultShortenKeepSeconds, 0.1, 60);
        s.Ui.JumpSeconds = Math.Clamp(JumpSeconds, 0.1, 60);
        s.Language = string.IsNullOrWhiteSpace(Language) ? "日本語" : Language.Trim();
        s.UiMode = IsSimpleMode ? UiModes.Simple : UiModes.Full;
        s.EnableExternalDocumentExport = EnableExternalDocumentExport;
        s.Subtitle = BuildSubtitle();
        _sampleCts?.Cancel();
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            // 保存に失敗したら、新しい有効状態・モードがメモリー上だけに残らないよう元に戻す（主画面へも適用しない。v1.14）。
            s.EnableExternalDocumentExport = previousExternal;
            s.UiMode = previousMode;
            _dialogs.ShowError(ex.Message);
            return;
        }
        Saved = true;
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        _sampleCts?.Cancel();
        CloseRequested?.Invoke(false);
    }

    /// <summary>キャンセル時、未保存のパスで検出した状態を保存済み設定で戻す。</summary>
    public async Task RevertDetectionIfNeededAsync()
    {
        if (!Saved && _detectedWithUnsavedPaths) await FFmpeg.RefreshAsync();
        else if (Saved) await FFmpeg.RefreshAsync();
    }
}

public sealed record VariableInfo(string Name, string Description);
