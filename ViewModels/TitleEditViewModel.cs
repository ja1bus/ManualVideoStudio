using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Imaging;
using ManualVideoStudio.Services;

namespace ManualVideoStudio.ViewModels;

/// <summary>
/// 動画タイトルの編集画面（v1.15）。文章・フォント・文字サイズ・表示秒数・本編の冒頭フェード・静止プレビューをまとめ、
/// ［OK］で 1 回の変更として反映する。［キャンセル］では何も変えない。文章が空ならタイトルなし（別の有効 / 無効の設定は持たない）。
/// ［クリア］は文章を空にするだけで、反映は［OK］で行う。
/// </summary>
public sealed partial class TitleEditViewModel : ObservableObject
{
    /// <summary>プレビュー画像の幅（px。高さは動画の縦横比で決める）。</summary>
    private const int PreviewWidth = 640;

    private readonly ProjectEditor _editor;
    private readonly int _videoWidth;
    private readonly int _videoHeight;
    private readonly double _mainDuration;
    private string _fontName;
    private bool _loadingFont;

    public TitleEditViewModel(ProjectEditor editor, int videoWidth, int videoHeight)
    {
        _editor = editor;
        _videoWidth = videoWidth > 0 ? videoWidth : 1920;
        _videoHeight = videoHeight > 0 ? videoHeight : 1080;
        _mainDuration = editor.OutputDuration;
        var t = editor.Project.Title;
        _text = t.Text;
        _seconds = VideoTitle.NormalizeSeconds(t.Seconds);
        _fadeIn = t.FadeIn;
        _fontName = VideoTitle.NormalizeFontFamily(t.FontFamily);
        _fontSize = VideoTitle.NormalizeFontSize(t.FontSize);
        _fontNote = Loc.Get("Title.FontLoading");
        Refresh();
        _ = LoadFontsAsync();
    }

    public event Action<bool>? CloseRequested;

    public IReadOnlyList<int> SecondsChoices => VideoTitle.SecondsChoices;
    public IReadOnlyList<int> FontSizes => VideoTitle.FontSizes;
    public ObservableCollection<FontChoice> FontChoices { get; } = [];

    [ObservableProperty] private string _text;
    [ObservableProperty] private int _seconds;
    [ObservableProperty] private bool _fadeIn;
    [ObservableProperty] private int _fontSize;
    [ObservableProperty] private FontChoice? _selectedFont;
    [ObservableProperty] private string _fontNote;
    [ObservableProperty] private string _error = "";
    /// <summary>文章が収まらないときの案内（収まるなら空）。入力に合わせて更新する。</summary>
    [ObservableProperty] private string _layoutWarning = "";
    /// <summary>1 行に入る文字数などの目安。</summary>
    [ObservableProperty] private string _capacityText = "";
    [ObservableProperty] private BitmapSource? _previewImage;
    [ObservableProperty] private string _durationText = "";

    public double PreviewAspect => (double)_videoWidth / _videoHeight;

    partial void OnTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanClear));
        ClearCommand.NotifyCanExecuteChanged();
        Refresh();
    }

    partial void OnSecondsChanged(int value) => Refresh();
    partial void OnFadeInChanged(bool value) => Refresh();
    partial void OnFontSizeChanged(int value) => Refresh();

    partial void OnSelectedFontChanged(FontChoice? value)
    {
        // 一覧の読み込みで自動選択したときは、保存済みのフォント名を書き換えない（この PC に無い場合に黙って別のフォントへ変えないため）。
        if (_loadingFont || value is null) return;
        _fontName = value.Name;
        FontNote = value.SupportsJapanese ? "" : "このフォントには日本語の文字がありません（日本語は別のフォントで描かれます）。";
        Refresh();
    }

    /// <summary>文章が入っているときだけ［クリア］を押せる。</summary>
    public bool CanClear => !string.IsNullOrEmpty(Text);

    private async Task LoadFontsAsync()
    {
        var fonts = await SubtitleFonts.LoadAsync();
        _loadingFont = true;
        try
        {
            foreach (var f in fonts) FontChoices.Add(f);
            if (fonts.Count == 0)
            {
                FontNote = "";
                return;
            }
            SelectedFont = FontCatalog.Resolve(FontChoices, _fontName, out var note);
            FontNote = note ?? (SelectedFont is { SupportsJapanese: false } ? "このフォントには日本語の文字がありません（日本語は別のフォントで描かれます）。" : "");
        }
        finally
        {
            _loadingFont = false;
        }
    }

    private void Refresh()
    {
        Error = "";
        int pw = PreviewWidth, ph = Math.Max(1, (int)Math.Round(PreviewWidth * (double)_videoHeight / _videoWidth));
        var normalized = VideoTitle.Normalize(Text);
        // 配置の判定は実際の動画の大きさで行う（プレビューは同じ比率の縮小。文字サイズ・幅・上端は動画の大きさに比例する）。
        var real = TitleRenderer.Layout(normalized, _videoWidth, _videoHeight, _fontName, FontSize);
        LayoutWarning = TitleLayout.TooLongMessage(real) ?? "";
        CapacityText = Loc.Format("Title.Capacity", Math.Max(1, (int)Math.Floor(real.MaxLineWidthPx / real.FontSizePx)));
        PreviewImage = TitleRenderer.Render(real.Fits ? normalized : "", pw, ph, _fontName, FontSize);
        DurationText = BuildDurationText(normalized.Length > 0);
    }

    private string BuildDurationText(bool hasTitle)
    {
        var main = TimeFormat.FormatShort(_mainDuration);
        return hasTitle
            ? $"本編 {main} ＋ タイトル {Seconds} 秒 ＝ 完成動画 {TimeFormat.FormatShort(_mainDuration + Seconds)}"
            : $"本編 {main}（タイトルなし）";
    }

    /// <summary>文章を空にする（確認なし。反映は［OK］。秒数・フォント・文字サイズ・フェードは残す）。</summary>
    [RelayCommand(CanExecute = nameof(CanClear))]
    private void Clear() => Text = "";

    [RelayCommand]
    private void Ok()
    {
        var normalized = VideoTitle.Normalize(Text);
        if (normalized.Length > 0
            && TitleLayout.TooLongMessage(TitleRenderer.Layout(normalized, _videoWidth, _videoHeight, _fontName, FontSize)) is { } tooLong)
        {
            Error = tooLong;
            return;
        }
        var r = _editor.SetTitle(normalized, Seconds, FadeIn, _fontName, FontSize);
        if (!r.Success)
        {
            Error = r.Message;
            return;
        }
        CloseRequested?.Invoke(true);
    }

    [RelayCommand] private void Cancel() => CloseRequested?.Invoke(false);
}
