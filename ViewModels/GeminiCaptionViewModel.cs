using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Ai;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Core.Subtitles;
using ManualVideoStudio.Services;
using Microsoft.Extensions.Logging;

namespace ManualVideoStudio.ViewModels;

/// <summary>
/// 字幕依頼用動画の状態（v1.12。主画面が持つ。画面を閉じても残り、開き直したときに続きから進める）。
/// 準備済みの動画が今のカット・元動画・生成条件と合わなくなったら <see cref="Invalidate"/> で無効にする
/// （ファイルは確認なしに消さず、<see cref="Orphans"/> に残して、利用者が確認して削除できるようにする）。
/// </summary>
public sealed class CaptionRequestSession
{
    /// <summary>準備済みの依頼用動画（無ければ null）。</summary>
    public CaptionRequestVideo? Video { get; set; }

    /// <summary>使えなくなったが、まだ削除していない依頼用動画（このアプリが作ったもの）。</summary>
    public List<CaptionRequestVideo> Orphans { get; } = [];

    /// <summary>準備済みの動画を無効にする（カットのやり直し・プロジェクトの切り替え）。回答の取り込み待ちも終わる。</summary>
    public void Invalidate()
    {
        if (Video is { } v && File.Exists(v.Path)) Orphans.Add(v);
        Video = null;
    }

    /// <summary>今のプロジェクトと合わなくなっていたら無効にする。無効にしたら true。</summary>
    public bool InvalidateIfStale(ProjectDocument? project, bool subtitlePhase)
    {
        if (Video is null) return false;
        if (project is not null && subtitlePhase && CaptionRequestService.IsCurrent(Video, project, project.ExportSettings.TransitionSecondsForExport)) return false;
        // ファイルが無くなっているだけなら、残すものは無い。
        if (!File.Exists(Video.Path)) { Video = null; return true; }
        Invalidate();
        return true;
    }
}

/// <summary>字幕依頼の画面が使う部品（主画面から渡す）。</summary>
public sealed record GeminiCaptionServices(
    CaptionRequestService Requests, CaptionRequestStore Store, IPromptTemplateService Templates, IClipboardService Clipboard,
    IShellService Shell, IDialogService Dialogs, IWindowService Windows, FFmpegStatus FFmpeg, ISettingsService Settings, ILogger Logger);

/// <summary>
/// ［Geminiで字幕を作る］の画面（v1.12）。依頼用動画の準備 → プロンプトのコピーと Gemini を開く → 回答の貼り付け →
/// 解析・検証 → 取り込み → 依頼用動画の削除の確認、を順に行う。Gemini とは手動（コピー＆ペースト・ファイルの添付）で連携し、
/// API・ブラウザの自動操作はしない。
/// ここで扱う時間軸は<b>カット後の本編の動画（完成動画の本編部分と同じ。タイトルは含まない）</b>で、Gemini Chat 連携（<see cref="AiAssistViewModel"/>。元動画の時間軸）とは別。
/// </summary>
public sealed partial class GeminiCaptionViewModel : ObservableObject
{
    public const string GeminiUrl = AiAssistViewModel.GeminiUrl;
    private const int MaxShownIssues = 30;

    private readonly ProjectEditor _editor;
    private readonly CaptionRequestSession _session;
    private readonly GeminiCaptionServices _s;
    private readonly string _projectName;

    public GeminiCaptionViewModel(ProjectEditor editor, CaptionRequestSession session, GeminiCaptionServices services, string projectName)
    {
        _editor = editor;
        _session = session;
        _s = services;
        _projectName = projectName;
        Refresh();
    }

    /// <summary>字幕を取り込んだとき（主画面が字幕一覧などを更新するため）。</summary>
    public event Action<string>? Imported;
    public event Action? CloseRequested;

    public ObservableCollection<string> Issues { get; } = [];

    [ObservableProperty] private string _responseText = "";
    [ObservableProperty] private string _videoStatus = "";
    [ObservableProperty] private string _videoPath = "";
    [ObservableProperty] private string _promptMessage = "";
    [ObservableProperty] private string _parseSummary = "";
    [ObservableProperty] private bool _parseOk;
    [ObservableProperty] private string _replaceNotice = "";
    [ObservableProperty] private string _resultMessage = "";
    [ObservableProperty] private string _orphanText = "";
    [ObservableProperty] private string _pasteMessage = "";

    public bool HasVideo => _session.Video is not null;
    public bool HasOrphans => _session.Orphans.Count > 0;
    public bool HasVideoPath => VideoPath.Length > 0;
    public string PrepareButtonText => HasVideo ? "作り直す" : "動画を準備する";
    public string OutputDurationText => $"カット後の長さ {TimeFormat.FormatShort(_editor.OutputDuration)}（完成動画と同じカット・クロスフェード）";

    public string FlowText =>
        "① 字幕依頼用の動画を準備（字幕なし・カット後） → ② プロンプトをコピーして Gemini を開き、動画を添付して送信 → "
        + "③ Gemini の回答を下の欄へ貼り付け → ④ 検証して［字幕を取り込む］。取り込んだ字幕は、手動の字幕と同じ一覧・タイムラインで修正できます。";

    partial void OnResponseTextChanged(string value) => Validate();

    /// <summary>状態の表示を更新する（画面を開いたとき・動画の準備や削除のあと）。</summary>
    private void Refresh()
    {
        _session.InvalidateIfStale(_editor.Project, _editor.Project.EditPhase == EditPhase.Subtitle);
        if (_session.Video is { } v)
        {
            VideoPath = v.Path;
            var size = File.Exists(v.Path) ? new FileInfo(v.Path).Length : 0;
            VideoStatus = $"準備済み（長さ {TimeFormat.FormatShort(v.Duration)}、{size / 1024.0 / 1024.0:0.0} MB）。Gemini に添付してください。";
        }
        else
        {
            VideoPath = "";
            VideoStatus = "まだ準備していません。［動画を準備する］を押してください（作成には時間がかかります）。";
        }
        OrphanText = _session.Orphans.Count == 0 ? ""
            : $"使えなくなった古い字幕依頼用動画が {_session.Orphans.Count} 件残っています（カットのやり直しなどで無効になったもの）。不要なら削除できます。";
        OnPropertyChanged(nameof(HasVideo));
        OnPropertyChanged(nameof(HasOrphans));
        OnPropertyChanged(nameof(HasVideoPath));
        OnPropertyChanged(nameof(PrepareButtonText));
        ImportCommand.NotifyCanExecuteChanged();
    }

    // ---------------- ① 依頼用動画の準備 ----------------

    [RelayCommand]
    private async Task PrepareAsync()
    {
        var project = _editor.Project;
        if (_s.FFmpeg.FfmpegPath is not { Length: > 0 } ffmpeg)
        {
            _s.Dialogs.ShowError("ffmpeg が見つかりません。設定で ffmpeg を指定してください。");
            return;
        }
        var transition = project.ExportSettings.TransitionSecondsForExport;
        if (CaptionRequestService.IsCurrent(_session.Video, project, transition))
        {
            ResultMessage = "準備済みの動画が今のカットと同じ条件のため、作り直さずそのまま使えます。";
            return;
        }
        // 作り直す場合、前の動画は使わなくなる（確認して削除できるよう残す）。
        if (_session.Video is not null) _session.Invalidate();

        CaptionRequestVideo? video = null;
        try
        {
            var completed = await _s.Windows.RunWithProgressAsync("字幕依頼用動画の作成", async (progress, ct) =>
            {
                var p = new Progress<VideoExportProgress>(x => progress.Report(new ProgressInfo(x.Ratio,
                    $"作成中 {TimeFormat.Format(x.ProcessedSeconds)} / {TimeFormat.Format(x.TotalSeconds)}（{x.Ratio:0%}）")));
                video = await _s.Requests.PrepareAsync(ffmpeg, _s.FFmpeg.MajorVersion, project, _projectName, p, ct);
            });
            if (!completed)
            {
                ResultMessage = "字幕依頼用動画の作成をキャンセルしました（途中のファイルは削除しました）。";
                Refresh();
                return;
            }
        }
        catch (FFmpegException ex)
        {
            _s.Dialogs.ShowError(ex.Message, ex.ErrorDetail);
            Refresh();
            return;
        }
        catch (Exception ex)
        {
            _s.Logger.LogError(ex, "Caption request video failed");
            _s.Dialogs.ShowError("字幕依頼用動画の作成に失敗しました。", ex.Message);
            Refresh();
            return;
        }
        _session.Video = video;
        ResultMessage = "字幕依頼用の動画を準備しました。";
        Refresh();
    }

    [RelayCommand]
    private void OpenVideoFolder()
    {
        if (_session.Video is { } v) _s.Shell.RevealFile(v.Path);
        else _s.Shell.OpenFolder(_s.Store.Root);
    }

    /// <summary>使えなくなった古い依頼用動画を、1 件ずつ確認して削除する。</summary>
    [RelayCommand]
    private void DeleteOrphans()
    {
        foreach (var o in _session.Orphans.ToList())
        {
            if (!File.Exists(o.Path)) { _session.Orphans.Remove(o); continue; }
            var answer = _s.Windows.ConfirmDeleteRequestVideo(
                "カットのやり直しなどで使えなくなった、字幕依頼用動画があります。削除しますか？", o.Path);
            if (answer != true) continue;
            var r = _s.Store.Delete(o.Path);
            if (r.Success) _session.Orphans.Remove(o);
            else _s.Dialogs.ShowError($"字幕依頼用動画を削除できませんでした。{Environment.NewLine}{o.Path}", r.Message);
        }
        Refresh();
    }

    // ---------------- ② プロンプト ----------------

    /// <summary>プロンプトの文（設定→プロンプトの「字幕依頼」。変数は字幕依頼用動画＝カット後の動画の値）。</summary>
    public PromptRenderResult BuildPrompt()
    {
        var template = _s.Templates.Get(AiPurpose.SubtitleRequest);
        var vars = PromptVariables.Build(_editor.Project, _s.Settings.Current.Language);
        return PromptRenderer.Render(template, vars);
    }

    [RelayCommand]
    private void CopyPrompt()
    {
        var r = BuildPrompt();
        PromptMessage = _s.Clipboard.SetText(r.Text)
            ? "プロンプトをコピーしました。Gemini に貼り付け、「今回の動画の内容」を書き換えて、準備した動画を添付して送信してください。"
              + (r.HasWarnings ? Environment.NewLine + r.WarningMessage + "（設定→プロンプトで直せます）" : "")
            : "コピーできませんでした（ほかのアプリがクリップボードを使用中です）。もう一度押してください。";
    }

    [RelayCommand]
    private void OpenGemini()
    {
        try { _s.Shell.OpenUrl(GeminiUrl); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            PromptMessage = $"ブラウザを開けませんでした。{GeminiUrl} を開いてください。（{ex.Message}）";
        }
    }

    // ---------------- ③④ 回答の解析・検証 ----------------

    /// <summary>［クリップボードから貼り付け］: 回答欄の内容をクリップボードの文字列に置き換える（追記しない。検証は自動で走る）。</summary>
    [RelayCommand]
    private void PasteFromClipboard()
    {
        if (!_s.Clipboard.TryGetText(out var text))
        {
            PasteMessage = "クリップボードに文字列がありません。Gemini の回答をコピーしてから押してください。回答欄はそのままです。";
            return;
        }
        ResponseText = text;
        PasteMessage = $"クリップボードの内容を貼り付けました（{text.Length:N0} 文字）。";
    }

    /// <summary>貼り付けた文章を解析・検証して、結果を画面に出す（何も変更しない）。</summary>
    private SrtParseResult? Validate()
    {
        Issues.Clear();
        ReplaceNotice = "";
        if (string.IsNullOrWhiteSpace(ResponseText))
        {
            ParseSummary = "";
            ParseOk = false;
            ImportCommand.NotifyCanExecuteChanged();
            return null;
        }
        var result = CaptionSrtParser.Parse(ResponseText, _editor.OutputDuration);
        ParseOk = result.IsValid;
        var from = result.ExtractedFromCodeBlock ? "（コードブロック内の SRT を使用）" : "";
        if (result.IsValid)
        {
            ParseSummary = $"✔ 字幕 {result.Entries.Count} 件を認識しました。問題はありません{from}。";
            var existing = _editor.Project.SubtitleSegments.Count;
            if (existing > 0)
                ReplaceNotice = $"現在の字幕 {existing} 件（手動で直した分を含む）は、取り込む {result.Entries.Count} 件にすべて置き換わります（自動のマージはしません）。";
        }
        else
        {
            ParseSummary = $"✖ 取り込めません（問題 {result.Errors.Count} 件）{from}。回答または SRT を直して貼り付け直してください。";
            foreach (var e in result.Errors.Take(MaxShownIssues)) Issues.Add(e.Display);
            if (result.Errors.Count > MaxShownIssues) Issues.Add($"…ほか {result.Errors.Count - MaxShownIssues} 件");
        }
        ImportCommand.NotifyCanExecuteChanged();
        return result;
    }

    private bool CanImport() => ParseOk && _editor.IsCutLocked;

    // ---------------- 取り込み・依頼用動画の削除の確認 ----------------

    [RelayCommand(CanExecute = nameof(CanImport))]
    private void Import()
    {
        // 画面に出した検証結果ではなく、今の入力と今の編集の状態で検証し直す。
        var result = CaptionSrtParser.Parse(ResponseText, _editor.OutputDuration);
        if (!result.IsValid) { Validate(); return; }

        var existing = _editor.Project.SubtitleSegments.Count;
        if (existing > 0 && !_s.Dialogs.Confirm(
                $"現在の字幕 {existing} 件（手動で直した分を含む）を、取り込む字幕 {result.Entries.Count} 件にすべて置き換えます。{Environment.NewLine}"
                + $"自動のマージはしません。取り込み後も［元に戻す］(Ctrl+Z) で戻せます。{Environment.NewLine}{Environment.NewLine}置き換えますか？",
                "字幕の置き換え"))
        {
            ResultMessage = "取り込みを取りやめました（字幕は変わっていません）。";
            return;
        }

        var r = _editor.ImportSubtitles(result.Entries.Select(e => (e.Start, e.End, e.Text)).ToList());
        if (!r.Success)
        {
            // 取り込みの失敗では、字幕も依頼用動画も変えない。
            ParseOk = false;
            Issues.Clear();
            foreach (var line in r.Message.Split(Environment.NewLine)) Issues.Add(line);
            ParseSummary = "✖ 取り込めませんでした。字幕は変わっていません。";
            ImportCommand.NotifyCanExecuteChanged();
            return;
        }

        ResponseText = "";
        ResultMessage = r.Message + "（元に戻す: Ctrl+Z）";
        var closeAfter = AskDeleteRequestVideo();
        Refresh();
        // 画面を閉じても結果が分かるよう、主画面のステータスバーへ集約する。
        Imported?.Invoke(ResultMessage.Replace(Environment.NewLine, " "));
        // 削除確認で［削除する］［削除しない］のどちらかを選んだら、画面を閉じて主画面に戻る（×で閉じた場合は閉じない）。
        if (closeAfter) CloseRequested?.Invoke();
    }

    /// <summary>
    /// 取り込みの成功後に、依頼用動画を削除するか確認する（既定は［削除する］。［削除しない］・画面を閉じるだけでは削除しない。
    /// 選択は今回限りで、次の取り込みでは再び［削除する］が既定になる）。失敗しても取り込んだ字幕は維持する。
    /// </summary>
    private bool AskDeleteRequestVideo()
    {
        if (_session.Video is not { } v) return false;
        if (!File.Exists(v.Path)) { _session.Video = null; return false; }
        var answer = _s.Windows.ConfirmDeleteRequestVideo("字幕を取り込みました。Geminiへ渡した字幕依頼用動画を削除しますか？", v.Path);
        if (answer is null) // ×で閉じただけ: 削除せず、この画面に留まる。
        {
            ResultMessage += Environment.NewLine + "字幕依頼用動画は削除せず、そのまま残しました。";
            return false;
        }
        if (answer == false)
        {
            ResultMessage += Environment.NewLine + "字幕依頼用動画は削除せず、そのまま残しました。";
            return true;
        }
        var d = _s.Store.Delete(v.Path);
        if (d.Success)
        {
            _session.Video = null;
            ResultMessage += Environment.NewLine + "字幕依頼用動画を削除しました。";
            return true;
        }
        _s.Logger.LogWarning("Caption request video delete failed: {Path}: {Reason}", v.Path, d.Message);
        _s.Dialogs.ShowError($"字幕依頼用動画を削除できませんでした（取り込んだ字幕はそのまま残っています）。{Environment.NewLine}{v.Path}", d.Message);
        ResultMessage += Environment.NewLine + "字幕依頼用動画を削除できませんでした。ファイルはそのまま残っています。";
        return true; // エラーを確認した後、閉じる（字幕は取り込み済み）。
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();
}
