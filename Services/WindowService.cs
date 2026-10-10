using System.Windows;
using ManualVideoStudio.Core.Ai;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.ViewModels;
using ManualVideoStudio.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ManualVideoStudio.Services;

public sealed record ProgressInfo(double? Ratio, string Message);

public sealed record SettingsDialogResult(bool Saved, bool ApplyOutputToProject);

/// <summary>手順書用の書き出しの結果（完了画面に出す。v1.8）。</summary>
public sealed record TejunshoExportDone(string Directory, int Written, int Skipped, bool MetaWritten, string ProjectName = "");

/// <summary>ViewModel から画面（Window）を開くための窓口。View の型は ViewModel から見えない。</summary>
public interface IWindowService
{
    /// <summary>設定画面。sample は字幕タブの確認画像に使う場面（主画面の再生位置・選択中の字幕）。</summary>
    Task<SettingsDialogResult> ShowSettingsAsync(SettingsTab tab = SettingsTab.FFmpeg, SubtitleSampleContext? sample = null);
    void ShowAiAssist();
    /// <summary>Gemini Chat 連携の画面が開いていれば閉じる（v1.11。簡易モードへ切り替えたとき）。</summary>
    void CloseAiAssist();
    /// <summary>前後フレーム候補の比較画面。選んだ時刻、キャンセルなら null。</summary>
    double? ChooseFrame(FrameCandidatesViewModel vm);
    bool EditCut(CutEditViewModel vm);
    /// <summary>手順書画像の注釈編集画面（モーダル）。</summary>
    void EditImage(ImageEditorViewModel vm);
    /// <summary>文字入力（既定は複数行。singleLine なら 1 行で Enter で決定）。キャンセルなら null。</summary>
    string? PromptText(string title, string message, string initial, bool singleLine = false);
    /// <summary>進捗ダイアログを表示して処理を実行する。完了 true / キャンセル false。例外はそのまま伝える。</summary>
    Task<bool> RunWithProgressAsync(string title, Func<IProgress<ProgressInfo>, CancellationToken, Task> work);
    /// <summary>字幕の追加・編集ダイアログ。OK なら true。</summary>
    bool EditSubtitle(SubtitleEditViewModel vm);
    /// <summary>GIF の品質選択。書き出すなら true。</summary>
    bool ChooseGifQuality(GifExportViewModel vm);
    /// <summary>動画タイトルの編集画面（v1.15、モーダル）。OK で反映したら true。</summary>
    bool EditTitle(TitleEditViewModel vm);
    /// <summary>取り消しにくい操作の確認（ボタンに操作名を出す。既定はキャンセル）。</summary>
    bool ConfirmDanger(string title, string message, string okLabel, string cancelLabel);
    /// <summary>録画画面を開く（主画面は最小化・無効化し、閉じたら戻す）。録画して閉じた場合は結果、それ以外は null。</summary>
    Task<RecordingOutcome?> ShowRecorderAsync();
    /// <summary>録画画面が開いているか（開いている間は主画面を閉じない）。</summary>
    bool IsRecorderOpen { get; }
    /// <summary>録画画面を前面に出す。</summary>
    void ActivateRecorder();
    /// <summary>
    /// プロジェクト一覧（v1.7、モーダル）。主画面で行う操作を選んで閉じた場合はその操作、閉じるだけなら null。
    /// openProjectPath / openVideoPath は主画面で開いているもの（削除・名前の変更をさせない）。
    /// </summary>
    ProjectListAction? ShowProjectList(string? openProjectPath, string? openVideoPath);
    /// <summary>手順書用の書き出しの完了画面（v1.8、モーダル）。</summary>
    void ShowTejunshoExportDone(TejunshoExportDone result);
    /// <summary>［Geminiで字幕を作る］の画面（v1.12、モーダル）。</summary>
    void ShowGeminiCaption(GeminiCaptionViewModel vm);
    /// <summary>
    /// 字幕依頼用動画（アプリが作ったもの）を削除するかの確認（v1.12）。対象のフルパスを出す。
    /// true = 削除する（既定）/ false = 削除しない / null = 閉じただけ（削除しない）。
    /// </summary>
    bool? ConfirmDeleteRequestVideo(string message, string path);
}

public sealed class WindowService(IServiceProvider services) : IWindowService
{
    private AiAssistWindow? _aiWindow;
    private RecorderWindow? _recorder;

    private static Window? Owner => Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;

    public async Task<SettingsDialogResult> ShowSettingsAsync(SettingsTab tab = SettingsTab.FFmpeg, SubtitleSampleContext? sample = null)
    {
        var vm = services.GetRequiredService<SettingsViewModel>();
        vm.SubtitleSample = sample ?? SubtitleSampleContext.Empty;
        vm.SelectedTab = tab;
        var w = new SettingsWindow { DataContext = vm, Owner = Owner };
        vm.CloseRequested += ok => w.DialogResult = ok;
        w.ShowDialog();
        await vm.RevertDetectionIfNeededAsync();
        return new SettingsDialogResult(vm.Saved, vm.ApplyOutputToProject);
    }

    public void ShowAiAssist()
    {
        if (_aiWindow is { IsLoaded: true })
        {
            if (_aiWindow.WindowState == WindowState.Minimized) _aiWindow.WindowState = WindowState.Normal;
            _aiWindow.Activate();
            return;
        }
        var vm = services.GetRequiredService<AiAssistViewModel>();
        _aiWindow = new AiAssistWindow { DataContext = vm, Owner = Application.Current.MainWindow };
        _aiWindow.Closed += (_, _) => _aiWindow = null;
        _aiWindow.Show();
    }

    public void CloseAiAssist() => _aiWindow?.Close();

    public double? ChooseFrame(FrameCandidatesViewModel vm)
    {
        var w = new FrameCandidatesWindow { DataContext = vm, Owner = Owner };
        vm.CloseRequested += ok => w.DialogResult = ok;
        _ = vm.LoadAsync();
        return w.ShowDialog() == true ? vm.SelectedTime : null;
    }

    public bool EditCut(CutEditViewModel vm)
    {
        var w = new CutEditWindow { DataContext = vm, Owner = Owner };
        vm.CloseRequested += ok => w.DialogResult = ok;
        return w.ShowDialog() == true;
    }

    public void EditImage(ImageEditorViewModel vm)
    {
        var w = new ImageEditorWindow { DataContext = vm, Owner = Owner };
        vm.CloseRequested += w.Close;
        w.ShowDialog();
    }

    public string? PromptText(string title, string message, string initial, bool singleLine = false)
    {
        var w = new TextInputWindow(title, message, initial, singleLine) { Owner = Owner };
        return w.ShowDialog() == true ? w.Text : null;
    }

    public bool EditSubtitle(SubtitleEditViewModel vm)
    {
        var w = new SubtitleEditWindow { DataContext = vm, Owner = Owner };
        vm.CloseRequested += ok => w.DialogResult = ok;
        return w.ShowDialog() == true;
    }

    public bool ChooseGifQuality(GifExportViewModel vm)
    {
        var w = new GifExportWindow { DataContext = vm, Owner = Owner };
        vm.CloseRequested += ok => w.DialogResult = ok;
        return w.ShowDialog() == true;
    }

    public bool EditTitle(TitleEditViewModel vm)
    {
        var w = new TitleEditWindow { DataContext = vm, Owner = Owner };
        vm.CloseRequested += ok => w.DialogResult = ok;
        return w.ShowDialog() == true;
    }

    public bool ConfirmDanger(string title, string message, string okLabel, string cancelLabel)
        => new ConfirmDangerWindow(title, message, okLabel, cancelLabel) { Owner = Owner }.ShowDialog() == true;

    public ProjectListAction? ShowProjectList(string? openProjectPath, string? openVideoPath)
    {
        var vm = services.GetRequiredService<ProjectListViewModel>();
        vm.OpenProjectPath = openProjectPath;
        vm.OpenVideoPath = openVideoPath;
        var w = new ProjectListWindow(vm) { Owner = Application.Current.MainWindow };
        ProjectListAction? result = null;
        vm.CloseRequested += action =>
        {
            result = action;
            w.Close();
        };
        vm.Load();
        w.ShowDialog();
        return result;
    }

    public void ShowTejunshoExportDone(TejunshoExportDone result)
    {
        // 依頼文は設定→プロンプトの「手順書作成の依頼文」テンプレートから作る（v1.9）。
        var template = services.GetRequiredService<IPromptTemplateService>().Get(AiPurpose.TejunshoRequest);
        var request = TejunshoExport.RenderRequest(template, result.Directory, result.ProjectName, result.Written + result.Skipped);
        var vm = new TejunshoExportDoneViewModel(result.Directory, result.Written, result.Skipped, result.MetaWritten, request,
            services.GetRequiredService<IClipboardService>(), services.GetRequiredService<IShellService>());
        var w = new TejunshoExportDoneWindow { DataContext = vm, Owner = Owner };
        vm.CloseRequested += w.Close;
        w.ShowDialog();
    }

    public void ShowGeminiCaption(GeminiCaptionViewModel vm)
    {
        var w = new GeminiCaptionWindow { DataContext = vm, Owner = Owner };
        vm.CloseRequested += w.Close;
        w.ShowDialog();
    }

    public bool? ConfirmDeleteRequestVideo(string message, string path)
        => new DeleteRequestVideoWindow(message, path) { Owner = Owner }.ShowDialog();

    public bool IsRecorderOpen => _recorder is not null;

    public void ActivateRecorder()
    {
        if (_recorder is { IsVisible: true } r) r.Activate();
    }

    public Task<RecordingOutcome?> ShowRecorderAsync()
    {
        if (_recorder is not null)
        {
            ActivateRecorder();
            return Task.FromResult<RecordingOutcome?>(null);
        }
        var main = Application.Current.MainWindow;
        var vm = services.GetRequiredService<RecorderViewModel>();
        var w = new RecorderWindow(vm);
        _recorder = w;
        var tcs = new TaskCompletionSource<RecordingOutcome?>();
        var previous = main?.WindowState ?? WindowState.Normal;
        // 録画範囲を選ぶ・録画する間は主画面を邪魔にならないよう最小化し、操作できないようにする。
        if (main is not null)
        {
            main.IsEnabled = false;
            main.WindowState = WindowState.Minimized;
        }
        w.Closed += (_, _) =>
        {
            _recorder = null;
            if (main is not null)
            {
                main.IsEnabled = true;
                main.WindowState = previous == WindowState.Minimized ? WindowState.Normal : previous;
                main.Activate();
            }
            tcs.TrySetResult(w.Outcome);
        };
        w.Show();
        return tcs.Task;
    }

    public Task<bool> RunWithProgressAsync(string title, Func<IProgress<ProgressInfo>, CancellationToken, Task> work)
    {
        var vm = new ProgressViewModel(title);
        var w = new ProgressWindow { DataContext = vm, Owner = Owner };
        var progress = new Progress<ProgressInfo>(vm.Update);
        Exception? error = null;
        bool canceled = false;
        w.Loaded += async (_, _) =>
        {
            try { await work(progress, vm.Token); }
            catch (OperationCanceledException) { canceled = true; }
            catch (Exception ex) { error = ex; }
            finally { vm.Complete(); w.Close(); }
        };
        // ShowDialog は処理の完了（Loaded ハンドラーの finally で Close）まで入れ子のメッセージループで待つ。UI は固まらない。
        w.ShowDialog();
        if (error is not null) return Task.FromException<bool>(error);
        return Task.FromResult(!canceled);
    }
}
