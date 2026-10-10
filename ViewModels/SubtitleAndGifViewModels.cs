using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.ViewModels;

/// <summary>字幕の追加・編集ダイアログ。時刻は編集後の動画基準。</summary>
public sealed partial class SubtitleEditViewModel : ObservableObject
{
    private readonly ProjectEditor _editor;
    private readonly string? _id;
    private readonly Func<double> _currentOutputTime;

    private SubtitleEditViewModel(ProjectEditor editor, string? id, double start, double end, string text,
        SubtitlePosition position, double fontSize, Func<double> currentOutputTime)
    {
        _editor = editor;
        _id = id;
        _currentOutputTime = currentOutputTime;
        _startText = TimeFormat.Format(start);
        _endText = TimeFormat.Format(end);
        _text = text;
        _position = position;
        _fontSize = SubtitleStyle.NormalizeFontSize(fontSize);
        OutputDurationText = TimeFormat.Format(editor.OutputDuration);
    }

    public static SubtitleEditViewModel ForNew(ProjectEditor editor, double start, double end, Func<double> currentOutputTime)
        => new(editor, null, start, end, "", editor.SubtitleDefaults.Position, editor.SubtitleDefaults.FontSize, currentOutputTime);

    public static SubtitleEditViewModel ForExisting(ProjectEditor editor, SubtitleSegment s, Func<double> currentOutputTime)
        => new(editor, s.Id, s.Start, s.End, s.Text, s.Position, s.FontSize, currentOutputTime);

    public event Action<bool>? CloseRequested;

    public string Title => _id is null ? "字幕の追加" : "字幕の編集";
    public string OutputDurationText { get; }
    public IReadOnlyList<double> FontSizes => SubtitleStyle.FontSizes;

    [ObservableProperty] private string _startText;
    [ObservableProperty] private string _endText;
    [ObservableProperty] private string _text;
    [ObservableProperty] private SubtitlePosition _position;
    [ObservableProperty] private double _fontSize;
    [ObservableProperty] private string _error = "";

    [RelayCommand] private void StartFromCurrent() => StartText = TimeFormat.Format(_currentOutputTime());
    [RelayCommand] private void EndFromCurrent() => EndText = TimeFormat.Format(_currentOutputTime());

    [RelayCommand]
    private void Ok()
    {
        if (!TimeFormat.TryParse(StartText, out var s)) { Error = "開始位置を HH:MM:SS.mmm 形式で入力してください。"; return; }
        if (!TimeFormat.TryParse(EndText, out var e)) { Error = "終了位置を HH:MM:SS.mmm 形式で入力してください。"; return; }
        var r = _id is null
            ? _editor.AddSubtitle(s, e, Text, Position, FontSize).Result
            : _editor.UpdateSubtitle(_id, s, e, Text, Position, FontSize);
        if (!r.Success) { Error = r.Message; return; }
        CloseRequested?.Invoke(true);
    }

    [RelayCommand] private void Cancel() => CloseRequested?.Invoke(false);
}

/// <summary>GIF 書き出しの品質選択。</summary>
public sealed partial class GifExportViewModel : ObservableObject
{
    /// <summary>これより長い GIF は MP4 を勧める（秒）。</summary>
    public const double LongGifSeconds = 60;

    public GifExportViewModel(GifQuality quality, double outputDuration, int subtitleCount, int videoWidth, int videoHeight, bool hasAudio,
        int shapeCount = 0, int titleSeconds = 0)
    {
        ShapeCount = shapeCount;
        TitleSeconds = titleSeconds;
        _quality = quality;
        OutputDuration = outputDuration;
        SubtitleCount = subtitleCount;
        VideoWidth = videoWidth;
        VideoHeight = videoHeight;
        HasAudio = hasAudio;
    }

    public event Action<bool>? CloseRequested;

    public IReadOnlyList<GifPreset> Presets => GifPreset.All;
    public double OutputDuration { get; }
    public int SubtitleCount { get; }
    /// <summary>図形のまとまりの数（v1.6）。</summary>
    public int ShapeCount { get; }
    /// <summary>先頭に付けるタイトルの秒数（v1.15。タイトルなしは 0）。OutputDuration は本編の長さ。</summary>
    public int TitleSeconds { get; }
    public int VideoWidth { get; }
    public int VideoHeight { get; }
    public bool HasAudio { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private GifQuality _quality;

    public string DurationText => TitleSeconds > 0
        ? $"長さ {TimeFormat.Format(OutputDuration + TitleSeconds)}（本編 {TimeFormat.Format(OutputDuration)} ＋ タイトル {TitleSeconds} 秒）"
        : $"長さ {TimeFormat.Format(OutputDuration)}（カット適用後）";
    public string SubtitleText => (SubtitleCount, ShapeCount) switch
    {
        (0, 0) => "字幕・図形はありません。",
        (_, 0) => $"字幕 {SubtitleCount} 件を焼き込みます（MP4 と同じ字幕）。",
        (0, _) => $"図形 {ShapeCount} 件を焼き込みます（MP4 と同じ図形）。",
        _ => $"字幕 {SubtitleCount} 件・図形 {ShapeCount} 件を焼き込みます（MP4 と同じ）。",
    };
    public string AudioText => HasAudio ? "GIF には音声が入りません（動画の音声は使いません）。" : "";
    public bool IsLong => OutputDuration + TitleSeconds > LongGifSeconds;
    public string LongWarning => $"{LongGifSeconds:0} 秒を超える GIF はファイルが大きくなります。長い説明には MP4 の書き出しをおすすめします。";

    /// <summary>選んだプリセットでの GIF の大きさ（縮小のみ）。</summary>
    public string SizeText
    {
        get
        {
            var p = GifPreset.Of(Quality);
            if (VideoWidth <= 0 || VideoHeight <= 0) return "";
            double scale = Math.Min(1.0, Math.Min((double)p.MaxWidth / VideoWidth, (double)p.MaxHeight / VideoHeight));
            int w = (int)Math.Round(VideoWidth * scale), h = (int)Math.Round(VideoHeight * scale);
            return string.Create(CultureInfo.InvariantCulture, $"GIF の大きさ: {w}x{h} / {p.Fps} fps（元 {VideoWidth}x{VideoHeight}）");
        }
    }

    [RelayCommand] private void Ok() => CloseRequested?.Invoke(true);
    [RelayCommand] private void Cancel() => CloseRequested?.Invoke(false);
}
