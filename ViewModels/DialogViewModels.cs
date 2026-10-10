using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Ai;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Services;

namespace ManualVideoStudio.ViewModels;

/// <summary>進捗ダイアログ。</summary>
public sealed partial class ProgressViewModel(string title) : ObservableObject
{
    private readonly CancellationTokenSource _cts = new();

    public string Title { get; } = title;
    [ObservableProperty] private string _message = "準備しています…";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private bool _isIndeterminate = true;
    [ObservableProperty] private bool _isCanceling;

    public CancellationToken Token => _cts.Token;
    public bool IsCompleted { get; private set; }

    public void Update(ProgressInfo info)
    {
        if (info.Ratio is { } r)
        {
            IsIndeterminate = false;
            Percent = Math.Clamp(r, 0, 1) * 100;
        }
        if (!string.IsNullOrEmpty(info.Message)) Message = info.Message;
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsCompleted) return;
        IsCanceling = true;
        Message = "キャンセルしています…";
        _cts.Cancel();
    }

    public void Complete() => IsCompleted = true;
}

/// <summary>確定カット区間の編集ダイアログ。</summary>
public sealed partial class CutEditViewModel : ObservableObject
{
    private readonly ProjectEditor _editor;
    private readonly string _id;
    private readonly Func<double> _currentTime;

    public CutEditViewModel(ProjectEditor editor, CutSegment segment, Func<double> currentTime)
    {
        _editor = editor;
        _id = segment.Id;
        _currentTime = currentTime;
        _startText = TimeFormat.Format(segment.Start);
        _endText = TimeFormat.Format(segment.End);
        _isShorten = segment.Action == SegmentAction.Shorten;
        _keepSecondsText = (segment.Action == SegmentAction.Shorten ? segment.KeepSeconds : 1.5).ToString("0.0##", CultureInfo.InvariantCulture);
        Reason = segment.Reason;
        SourceText = Labels.Source(segment.Source);
    }

    public event Action<bool>? CloseRequested;

    [ObservableProperty] private string _startText;
    [ObservableProperty] private string _endText;
    [ObservableProperty] private bool _isShorten;
    [ObservableProperty] private string _keepSecondsText;
    [ObservableProperty] private string _error = "";
    public string Reason { get; }
    public string SourceText { get; }

    [RelayCommand] private void StartFromCurrent() => StartText = TimeFormat.Format(_currentTime());
    [RelayCommand] private void EndFromCurrent() => EndText = TimeFormat.Format(_currentTime());

    [RelayCommand]
    private void Ok()
    {
        if (!TimeFormat.TryParse(StartText, out var s)) { Error = "開始位置を HH:MM:SS.mmm 形式で入力してください。"; return; }
        if (!TimeFormat.TryParse(EndText, out var e)) { Error = "終了位置を HH:MM:SS.mmm 形式で入力してください。"; return; }
        double keep = 0;
        if (IsShorten && !double.TryParse(KeepSecondsText, NumberStyles.Float, CultureInfo.InvariantCulture, out keep))
        {
            Error = "残す秒数を数値で入力してください。";
            return;
        }
        var r = _editor.UpdateCut(_id, s, e, IsShorten ? SegmentAction.Shorten : SegmentAction.Remove, keep);
        if (!r.Success) { Error = r.Message; return; }
        CloseRequested?.Invoke(true);
    }

    [RelayCommand] private void Cancel() => CloseRequested?.Invoke(false);
}

public sealed partial class FrameCandidateItem : ObservableObject
{
    public double Time { get; init; }
    public string TimeText => TimeFormat.FormatShort(Time);
    public bool IsRegistered { get; init; }
    public string Label { get; init; } = "";
    [ObservableProperty] private ImageSource? _image;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _error = "";
}

/// <summary>前後フレーム候補の比較。候補画像は ffmpeg で切り出してキャッシュする。</summary>
public sealed partial class FrameCandidatesViewModel : ObservableObject
{
    private readonly IFrameExtractionService _frames;
    private readonly string _ffmpeg;
    private readonly string _video;
    private readonly double _duration;
    private readonly double _registered;
    private CancellationTokenSource? _loadCts;

    public FrameCandidatesViewModel(IFrameExtractionService frames, string ffmpeg, string video, double duration,
        CapturePoint point, double step, int countEachSide)
    {
        _frames = frames;
        _ffmpeg = ffmpeg;
        _video = video;
        _duration = duration;
        _registered = point.Timestamp;
        SelectedTime = point.SelectedFrameTime;
        _center = point.SelectedFrameTime;
        _step = step;
        _countEachSide = Math.Clamp(countEachSide, 1, 5);
        Title = $"No.{point.Sequence} の画像を選ぶ（登録時刻 {TimeFormat.Format(point.Timestamp)}）";
    }

    public event Action<bool>? CloseRequested;

    public string Title { get; }
    public ObservableCollection<FrameCandidateItem> Items { get; } = [];
    public double SelectedTime { get; private set; }
    public IReadOnlyList<double> StepChoices { get; } = [0.033, 0.1, 0.2, 0.5, 1.0];

    [ObservableProperty] private double _center;
    [ObservableProperty] private double _step;
    [ObservableProperty] private int _countEachSide;
    [ObservableProperty] private bool _isLoading;
    public string SelectedTimeText => TimeFormat.Format(SelectedTime);

    partial void OnStepChanged(double value) => _ = LoadAsync();

    public async Task LoadAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        Items.Clear();
        var times = new List<double>();
        for (int i = -CountEachSide; i <= CountEachSide; i++)
        {
            var t = Math.Round(Center + i * Step, 3);
            if (t >= 0 && (_duration <= 0 || t <= _duration)) times.Add(t);
        }
        foreach (var t in times)
            Items.Add(new FrameCandidateItem
            {
                Time = t,
                IsRegistered = Math.Abs(t - _registered) < 0.001,
                IsSelected = Math.Abs(t - SelectedTime) < 0.001,
                Label = Math.Abs(t - _registered) < 0.001 ? "登録時刻" : (t < _registered ? "前" : "後"),
            });
        IsLoading = true;
        try
        {
            await Task.WhenAll(Items.ToList().Select(async item =>
            {
                try
                {
                    var path = await _frames.GetThumbnailAsync(_ffmpeg, _video, item.Time, 640, cts.Token);
                    if (!cts.IsCancellationRequested) item.Image = ImageLoader.Load(path);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { item.Error = ex.Message; }
            }));
        }
        finally
        {
            if (_loadCts == cts) IsLoading = false;
        }
    }

    [RelayCommand]
    private void Select(FrameCandidateItem? item)
    {
        if (item is null) return;
        foreach (var i in Items) i.IsSelected = ReferenceEquals(i, item);
        SelectedTime = item.Time;
        OnPropertyChanged(nameof(SelectedTimeText));
    }

    [RelayCommand]
    private void SelectAndClose(FrameCandidateItem? item)
    {
        Select(item);
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private async Task ShiftAsync(string direction)
    {
        var span = Step * (CountEachSide * 2 + 1);
        Center = Math.Max(0, Math.Round(Center + (direction == "-" ? -span : span), 3));
        if (_duration > 0) Center = Math.Min(Center, _duration);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task RecenterAsync()
    {
        Center = SelectedTime;
        await LoadAsync();
    }

    [RelayCommand] private void Ok() => CloseRequested?.Invoke(true);
    [RelayCommand] private void Cancel() => CloseRequested?.Invoke(false);
}

public static class ImageLoader
{
    /// <summary>
    /// ファイルをロックせずに読み込む（キャッシュ削除・上書きを妨げない）。
    /// 読めない・画像として解釈できない場合は、フルパスを含む IOException にする（ログから暗号化解除の対象を追えるように。v1.4.2）。
    /// </summary>
    public static BitmapImage? Load(string path, int decodeWidth = 0)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0) img.DecodePixelWidth = decodeWidth;
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FormatException or InvalidOperationException)
        {
            throw new IOException($"画像を読み込めませんでした（壊れているか暗号化されている可能性があります）: {Path.GetFullPath(path)}", ex);
        }
    }
}

/// <summary>手順書用の書き出しの完了画面（v1.8）。依頼文を Claude Code へ貼り付けるためにコピーする。</summary>
public sealed partial class TejunshoExportDoneViewModel(string directory, int written, int skipped, bool metaWritten,
    PromptRenderResult request, IClipboardService clipboard, IShellService shell) : ObservableObject
{
    public event Action? CloseRequested;

    public string Directory { get; } = directory;
    public string RequestText { get; } = request.Text;
    /// <summary>テンプレートの未定義の変数など（設定→プロンプトで直す）。</summary>
    public string RequestWarning { get; } = request.HasWarnings
        ? request.WarningMessage + Environment.NewLine + "（設定→プロンプト→「手順書作成の依頼文」で直せます）"
        : "";
    public string CountText { get; } = $"{written} 件の画像を書き出しました。" + (skipped > 0 ? $"（スキップ {skipped} 件）" : "");
    public string MetaText { get; } = metaWritten
        ? "meta.json（手順書の表紙・改訂履歴の下書き）を作りました。"
        : "meta.json は既にあるため、そのまま残しました（上書きしていません）。";

    [ObservableProperty] private string _copyStatus = "";

    [RelayCommand]
    private void CopyRequest()
        => CopyStatus = clipboard.SetText(RequestText)
            ? "コピーしました。Claude Code に貼り付けて送ってください。"
            : "コピーできませんでした（ほかのアプリがクリップボードを使用中です）。もう一度押してください。";

    [RelayCommand]
    private void OpenFolder() => shell.OpenFolder(Directory);

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();
}
