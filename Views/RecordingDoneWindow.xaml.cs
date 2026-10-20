using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Core.Recording;
using ManualVideoStudio.Services.Recording;
using ManualVideoStudio.ViewModels;

namespace ManualVideoStudio.Views;

/// <summary>
/// 録画の完了（プロジェクト名の入力・編集を開始 / 保存して終了）。
/// v1.4.1: 停止の直後に表示し、録画ファイルができるまで「保存しています」と表示して待つ。
/// 閉じるのはボタンのクリックだけ（Esc・Enter・× では閉じない。IME の確定・取り消しや、ほかのアプリへ打ったキーで閉じないように）。
/// ボタンはファイルができてから <see cref="ButtonGuard"/> の後に押せるようにする。ファイルを作れなかった場合は自動で閉じ、呼び出し元がエラーを表示する。
/// </summary>
public partial class RecordingDoneWindow : Window
{
    /// <summary>ボタンを押せるようにするまでの待ち時間（表示が変わった瞬間の誤クリック・打鍵を防ぐ）。</summary>
    public static readonly TimeSpan ButtonGuard = TimeSpan.FromSeconds(1);

    private readonly string _root;
    private readonly DateTime _recordedAt;
    private readonly AppendRecordingContext? _append;
    private bool _closeAllowed;

    public RecordingDoneWindow(Task<RecordingResult> saving, RecordingInfo info, string projectRoot, AppendRecordingContext? append = null)
    {
        InitializeComponent();
        _root = projectRoot;
        _append = append;
        _recordedAt = info.RecordedAt.LocalDateTime;
        if (append is not null)
        {
            // 追加録画（v1.18）: つなげて編集 / 追加録画だけで編集。
            NormalButtons.Visibility = Visibility.Collapsed;
            AppendButtons.Visibility = Visibility.Visible;
            NameLabel.Text = "新しいプロジェクト名";
            JoinDescription.Text = $"「{append.SourceName}」のあとに追加録画をつなげた、新しい動画で編集を始めます。元の動画・元の編集は変わりません。"
                + "元の動画と追加録画のコピーが増えるため、容量を使います。";
            HelpText.Text = "保存している間にプロジェクト名を入力できます（空なら日時だけの名前）。名前は、つなげた動画のプロジェクト（または追加録画だけで編集するときのプロジェクト）に付きます。"
                + "追加録画は、つなげても別のフォルダーに残ります。";
        }
        InfoText.Text = Describe(info, 1);
        UpdateFolderText();
        SourceInitialized += (_, _) =>
        {
            NativeMethods.RemoveStyle(new WindowInteropHelper(this).Handle, NativeMethods.WS_SYSMENU);
            // 停止の直後はまだ ffmpeg が画面を取り込んでいるため、この画面が録画の最後に写らないようにする（v1.6.2）。
            ScreenScale.ExcludeFromCapture(this);
        };
        Loaded += async (_, _) =>
        {
            NameText.Focus();
            await WaitForFileAsync(saving);
        };
    }

    /// <summary>入力されたプロジェクト名（空でもよい）。</summary>
    public string ProjectName => NameText.Text.Trim();

    public bool StartEditing { get; private set; }

    /// <summary>追加録画での選択（通常の録画では None）。</summary>
    public AppendDoneAction AppendAction { get; private set; }

    /// <summary>録画ファイルを作れずに閉じた。</summary>
    public bool Failed { get; private set; }

    private async Task WaitForFileAsync(Task<RecordingResult> saving)
    {
        RecordingResult result;
        try
        {
            result = await saving;
        }
        catch
        {
            // エラーの表示は呼び出し元（RecorderViewModel）が行う。
            Failed = true;
            CloseBy(false);
            return;
        }
        HeaderText.Text = "録画を MP4 として保存しました。";
        SavingBar.Visibility = Visibility.Collapsed;
        PathText.Text = result.OutputPath;
        DurationText.Text = TimeFormat.Format(result.RecordedDuration.TotalSeconds);
        InfoText.Text = Describe(result.Info, result.SegmentCount);
        await Task.Delay(ButtonGuard);
        EditButton.IsEnabled = true;
        CloseButton.IsEnabled = true;
        JoinButton.IsEnabled = true;
        SingleButton.IsEnabled = true;
        WaitText.Text = "Esc・Enter キーでは閉じません。ボタンを押して選んでください。";
    }

    internal static string Describe(RecordingInfo i, int segmentCount)
    {
        var audio = i.Microphone ? $"マイク（{i.MicrophoneDevice}）" : "なし";
        var pauses = segmentCount > 1 ? $" / 一時停止 {segmentCount - 1} 回" : "";
        return $"{i.Target}（{i.Width}x{i.Height}）/ {i.Fps} fps / カーソル {(i.DrawCursor ? "あり" : "なし")} / 音声 {audio}{pauses}";
    }

    private void NameText_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateFolderText();

    private void UpdateFolderText()
    {
        if (FolderText is null || NameText is null) return;
        if (_append is null)
        {
            FolderText.Text = "保存先: " + Path.Combine(_root, ProjectFolders.FolderName(_recordedAt, ProjectName));
            return;
        }
        var extra = string.IsNullOrWhiteSpace(ProjectName) ? "追加録画" : ProjectName + "_追加録画";
        FolderText.Text = "つなげた動画の保存先: " + Path.Combine(_root, ProjectFolders.FolderName(DateTime.Now, ProjectName)) + Environment.NewLine
            + "追加録画の保存先: " + Path.Combine(_root, ProjectFolders.FolderName(_recordedAt, extra));
    }

    private void Join_Click(object sender, RoutedEventArgs e)
    {
        AppendAction = AppendDoneAction.Join;
        CloseBy(true);
    }

    private void Single_Click(object sender, RoutedEventArgs e)
    {
        AppendAction = AppendDoneAction.Single;
        StartEditing = true;
        CloseBy(true);
    }


    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        StartEditing = true;
        CloseBy(true);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseBy(false);

    private void CloseBy(bool result)
    {
        _closeAllowed = true;
        DialogResult = result;
    }

    /// <summary>Alt+F4 などボタン以外で閉じようとしても閉じない。</summary>
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_closeAllowed && !Dispatcher.HasShutdownStarted) e.Cancel = true;
    }
}
