using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Naming;

namespace ManualVideoStudio.ViewModels;

/// <summary>モデル一覧を ObservableCollection へ差分反映する（項目 VM を作り直さず、入力中のフォーカスやサムネイルを保つ）。</summary>
public static class CollectionSync
{
    public static void Sync<TItem, TModel, TKey>(ObservableCollection<TItem> target, IReadOnlyList<TModel> source,
        Func<TItem, TKey> itemKey, Func<TModel, TKey> modelKey, Func<TModel, TItem> create, Action<TItem, TModel> update)
        where TKey : notnull
    {
        var keys = new HashSet<TKey>(source.Select(modelKey));
        for (int i = target.Count - 1; i >= 0; i--)
            if (!keys.Contains(itemKey(target[i]))) target.RemoveAt(i);

        for (int i = 0; i < source.Count; i++)
        {
            var key = modelKey(source[i]);
            int existing = -1;
            for (int j = i; j < target.Count; j++)
                if (EqualityComparer<TKey>.Default.Equals(itemKey(target[j]), key)) { existing = j; break; }
            if (existing < 0)
            {
                target.Insert(i, create(source[i]));
            }
            else
            {
                if (existing != i) target.Move(existing, i);
                update(target[i], source[i]);
            }
        }
    }
}

public sealed partial class CapturePointItem : ObservableObject
{
    public CapturePointItem(CapturePoint model, ImageFormat format) => Update(model, format);

    public int Id { get; private set; }
    [ObservableProperty] private int _sequence;
    [ObservableProperty] private double _timestamp;
    [ObservableProperty] private double _selectedFrameTime;
    [ObservableProperty] private string _caption = "";
    [ObservableProperty] private CaptionSource _captionSource;
    [ObservableProperty] private string _outputFilename = "";
    [ObservableProperty] private string _filenamePreview = "";
    [ObservableProperty] private ImageSource? _thumbnail;
    [ObservableProperty] private bool _thumbnailFailed;
    [ObservableProperty] private int _annotationCount;
    [ObservableProperty] private string _stepText = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _chapterTitle = "";
    /// <summary>手順書の文（操作文・補足・章）の欄を開いているか。入力があれば開く（v1.9）。</summary>
    [ObservableProperty] private bool _isDetailOpen;

    /// <summary>サムネイルを生成した時刻（時刻変更で作り直す判定用）。</summary>
    public double? ThumbnailTime { get; set; }

    public string TimeText => TimeFormat.FormatShort(SelectedFrameTime);
    public bool IsAdjusted => Math.Abs(SelectedFrameTime - Timestamp) >= 0.001;
    public string TimeTooltip => IsAdjusted
        ? $"画像の時刻 {TimeFormat.Format(SelectedFrameTime)}（登録時 {TimeFormat.Format(Timestamp)} から調整）"
        : $"画像の時刻 {TimeFormat.Format(SelectedFrameTime)}";
    public bool IsAiCaption => CaptionSource == CaptionSource.Ai;
    public bool HasAnnotations => AnnotationCount > 0;
    public string AnnotationText => $"注釈 {AnnotationCount}";
    public bool IsChapterStart => ChapterTitle.Length > 0;
    public string ChapterBadge => $"章: {ChapterTitle}";
    public bool HasDetail => StepText.Length > 0 || Note.Length > 0 || ChapterTitle.Length > 0;

    /// <summary>手順書の文の欄の見出し（入力済みの項目を示す）。</summary>
    public string DetailHeader
    {
        get
        {
            var parts = new List<string>();
            if (ChapterTitle.Length > 0) parts.Add("章の始まり");
            if (StepText.Length > 0) parts.Add("操作文");
            if (Note.Length > 0) parts.Add("補足");
            return "手順書の文" + (parts.Count > 0 ? $"（{string.Join("・", parts)}）" : "（未入力）");
        }
    }

    partial void OnStepTextChanged(string value) => OnDetailChanged();
    partial void OnNoteChanged(string value) => OnDetailChanged();
    partial void OnChapterTitleChanged(string value)
    {
        OnPropertyChanged(nameof(IsChapterStart));
        OnPropertyChanged(nameof(ChapterBadge));
        OnDetailChanged();
    }

    private void OnDetailChanged()
    {
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(DetailHeader));
        if (HasDetail) IsDetailOpen = true;
    }

    partial void OnAnnotationCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasAnnotations));
        OnPropertyChanged(nameof(AnnotationText));
    }

    public void Update(CapturePoint m, ImageFormat format)
    {
        Id = m.Id;
        Sequence = m.Sequence;
        Timestamp = m.Timestamp;
        SelectedFrameTime = m.SelectedFrameTime;
        Caption = m.Caption;
        CaptionSource = m.CaptionSource;
        OutputFilename = m.OutputFilename;
        AnnotationCount = m.Annotations.Count;
        StepText = m.StepText;
        Note = m.Note;
        ChapterTitle = m.ChapterTitle;
        FilenamePreview = OutputFilenameBuilder.Build(m.Sequence, m.Caption, format);
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(TimeTooltip));
        OnPropertyChanged(nameof(IsAdjusted));
        OnPropertyChanged(nameof(IsAiCaption));
    }
}

public sealed class CutSegmentItem(CutSegment model)
{
    public CutSegment Model { get; private set; } = model;
    public string Id => Model.Id;
    public string StartText => TimeFormat.FormatShort(Model.Start);
    public string EndText => TimeFormat.FormatShort(Model.End);
    public string DurationText => Model.Duration.ToString("0.0#", CultureInfo.InvariantCulture) + " 秒";
    public string ActionText => Model.Action == SegmentAction.Shorten
        ? string.Create(CultureInfo.InvariantCulture, $"短縮（{Model.KeepSeconds:0.0#} 秒残す）")
        : "削除";
    public string SourceText => Labels.Source(Model.Source);
    public string Reason => Model.Reason;
    public bool IsShorten => Model.Action == SegmentAction.Shorten;
}

public sealed class CandidateItem(CutCandidate model)
{
    public CutCandidate Model { get; } = model;
    public string Id => Model.Id;
    public string StartText => TimeFormat.FormatShort(Model.Start);
    public string EndText => TimeFormat.FormatShort(Model.End);
    public string DurationText => Model.Duration.ToString("0.0#", CultureInfo.InvariantCulture) + " 秒";
    public string SuggestionText => Model.SuggestedAction == SegmentAction.Shorten
        ? string.Create(CultureInfo.InvariantCulture, $"短縮 {Model.KeepSeconds:0.0#} 秒")
        : "削除";
    public string ConfidenceText => Model.Confidence.ToString("0%", CultureInfo.InvariantCulture);
    public string SourceText => Labels.Source(Model.Source);
    public string StatusText => Model.Status switch
    {
        CandidateStatus.Accepted => "採用済み",
        CandidateStatus.Dismissed => "却下",
        _ => "未判断",
    };
    public string Reason => Model.Reason;
    public bool IsPending => Model.Status == CandidateStatus.Pending;
    public bool IsAi => Model.Source == SegmentSource.Ai;
}

/// <summary>字幕一覧の 1 行（時刻は編集後の動画基準）。</summary>
public sealed partial class SubtitleItem : ObservableObject
{
    public SubtitleItem(SubtitleSegment model)
    {
        Model = model;
        Update(model);
    }

    public SubtitleSegment Model { get; private set; }
    public string Id => Model.Id;
    [ObservableProperty] private string _startText = "";
    [ObservableProperty] private string _endText = "";
    [ObservableProperty] private string _durationText = "";
    [ObservableProperty] private string _positionText = "";
    [ObservableProperty] private string _fontSizeText = "";
    [ObservableProperty] private string _text = "";

    public void Update(SubtitleSegment m)
    {
        Model = m;
        StartText = TimeFormat.FormatShort(m.Start);
        EndText = TimeFormat.FormatShort(m.End);
        DurationText = m.Duration.ToString("0.0#", CultureInfo.InvariantCulture) + " 秒";
        PositionText = Labels.Position(m.Position);
        FontSizeText = m.FontSize.ToString("0", CultureInfo.InvariantCulture);
        Text = m.Text.Replace("\n", " / ");
    }
}

/// <summary>図形一覧の 1 行（v1.6。時刻は編集後の動画基準）。</summary>
public sealed partial class ShapeGroupItem : ObservableObject
{
    public ShapeGroupItem(ShapeGroup model)
    {
        Model = model;
        Update(model);
    }

    public ShapeGroup Model { get; private set; }
    public string Id => Model.Id;
    [ObservableProperty] private string _startText = "";
    [ObservableProperty] private string _endText = "";
    [ObservableProperty] private string _durationText = "";
    [ObservableProperty] private string _summary = "";

    public void Update(ShapeGroup m)
    {
        Model = m;
        StartText = TimeFormat.FormatShort(m.Start);
        EndText = TimeFormat.FormatShort(m.End);
        DurationText = m.Duration.ToString("0.0#", CultureInfo.InvariantCulture) + " 秒";
        Summary = m.Summary;
    }
}

public static class Labels
{
    public static string Position(SubtitlePosition p) => p switch
    {
        SubtitlePosition.Top => "上",
        SubtitlePosition.Middle => "中央",
        _ => "下",
    };

    public static string Source(SegmentSource s) => s switch
    {
        SegmentSource.LocalAnalysis => "ローカル解析",
        SegmentSource.Ai => "AI",
        _ => "手動",
    };
}
