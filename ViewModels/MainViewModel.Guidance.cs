using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Guidance;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using Microsoft.Extensions.Logging;

namespace ManualVideoStudio.ViewModels;

/// <summary>作業の流れの 1 段階の表示（現在地を強調する。クリックしても何も起きない表示専用）。</summary>
public sealed record FlowStepItem(string Label, bool IsCurrent, bool IsDone, bool IsLast)
{
    /// <summary>UI Automation の状態（current / done / todo。表示言語に依存しない）。</summary>
    public string State => IsCurrent ? "current" : IsDone ? "done" : "todo";

    /// <summary>UI Automation が項目の名前に使う（レコードの既定の ToString は読み上げに向かない）。</summary>
    public override string ToString() => Label;
}

/// <summary>開始画面の「最近のプロジェクト」の 1 行。</summary>
public sealed record RecentProjectItem(string Name, string DateText, string FullPath, bool IsVideoOnly, string Problem)
{
    public string AutomationName => Name + (Problem.Length > 0 ? "（" + Problem + "）" : "");

    public override string ToString() => AutomationName;
}

/// <summary>
/// 画面の整理と初心者案内（v1.13）。作業領域（動画編集 / 画像作成）、作業の流れ、状態に応じた「次にすること」、
/// 空の一覧の案内を、プロジェクトの状態から毎回決める。案内の進捗は保存しない。
/// </summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<RecentProjectItem> RecentProjects { get; } = [];

    /// <summary>開始画面に出す最近のプロジェクトの数。</summary>
    public const int RecentProjectCount = 6;

    // ---------------- 作業領域 ----------------

    /// <summary>画像作成（右側が画像一覧）。全機能モードだけ。選択中の一覧が「画像」のとき。</summary>
    public bool IsImageWorkspace => HasProject && IsFullMode && SelectedTabIndex == TabCaptures;
    public bool IsVideoWorkspace => HasProject && !IsImageWorkspace;
    /// <summary>動画未読込の開始画面。</summary>
    public bool ShowWelcome => !HasProject;
    public bool ShowRecentEmpty => RecentProjects.Count == 0;
    public bool ShowWorkspaceSwitch => HasProject && IsFullMode;
    public bool ShowCutTools => IsVideoWorkspace && IsCutPhase;
    public bool ShowSubtitleTools => IsVideoWorkspace && IsSubtitlePhase;

    private GuideWorkspace Workspace => IsImageWorkspace ? GuideWorkspace.Images : GuideWorkspace.Video;

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void ShowVideoWorkspace()
    {
        if (SelectedTabIndex == TabCaptures) SelectedTabIndex = IsSubtitlePhase ? TabSubtitles : TabCuts;
    }

    [RelayCommand(CanExecute = nameof(CanUseFullFeatures))]
    private void ShowImageWorkspace() => SelectedTabIndex = TabCaptures;

    /// <summary>右側の一覧の切り替え（カット区間 / 解析候補 / 字幕 / 図形 / 詳細）。</summary>
    [RelayCommand]
    private void SelectPane(string? index)
    {
        if (int.TryParse(index, out var i)) SelectedTabIndex = i;
    }

    // ---------------- 作業の流れ・次にすること ----------------

    /// <summary>Gemini の字幕を取り込んだ直後の字幕の内容（取り込んだままの間だけ「確認して調整」の案内を出す。保存しない）。</summary>
    private string? _importedSubtitleSignature;

    private string SubtitleSignature() => string.Join("|", (Project?.SubtitleSegments ?? []).Select(s => $"{s.Id}:{s.Start}:{s.End}:{s.Text}"));

    private bool SubtitlesJustImported => _importedSubtitleSignature is { } sig && IsSubtitlePhase && sig == SubtitleSignature();

    /// <summary>Gemini の字幕を取り込んだ直後であることを案内に反映する（取り込み後に内容が変わるまで）。</summary>
    public void NotifySubtitlesImported()
    {
        _importedSubtitleSignature = SubtitleSignature();
        NotifyGuidance();
    }

    public GuideNextAction NextAction => WorkflowGuide.NextAction(HasProject, Workspace, IsSubtitlePhase, SubtitlesJustImported);
    public string NextActionText => Loc.Get("Guide." + NextAction);

    public FlowStep CurrentFlowStep => WorkflowGuide.CurrentStep(HasProject, Workspace, IsSubtitlePhase, Captures.Count);

    public IReadOnlyList<FlowStepItem> FlowSteps
    {
        get
        {
            var steps = WorkflowGuide.Steps(Workspace);
            var current = CurrentFlowStep;
            int index = steps.ToList().IndexOf(current);
            return steps.Select((s, i) => new FlowStepItem(Loc.Get("Flow." + s), s == current, i < index, i == steps.Count - 1)).ToList();
        }
    }

    public string FlowAutomationName => Loc.Format("Flow.Automation", Loc.Get("Flow." + CurrentFlowStep));

    // ---------------- 空の一覧の案内 ----------------

    private AnalysisRunState _analysisState;

    public bool ShowCutsEmpty => HasProject && (Project?.CutSegments.Count ?? 0) == 0;
    public bool ShowSubtitlesEmpty => HasProject && (Project?.SubtitleSegments.Count ?? 0) == 0;
    public bool ShowShapesEmpty => HasProject && (Project?.ShapeGroups.Count ?? 0) == 0;
    public bool ShowCapturesEmpty => HasProject && Captures.Count == 0;

    public CandidateEmptyKind CandidateEmpty => HasProject
        ? WorkflowGuide.CandidateEmpty(_analysisState, Project?.CutCandidates.Count ?? 0, CandidateItems.Count)
        : CandidateEmptyKind.None;

    public bool ShowCandidatesEmpty => CandidateEmpty != CandidateEmptyKind.None;

    public string CandidateEmptyText => CandidateEmpty switch
    {
        CandidateEmptyKind.NotAnalyzed => Loc.Get("Empty.CandNotRun"),
        CandidateEmptyKind.AnalyzedNoCandidates => Loc.Get("Empty.CandNone"),
        CandidateEmptyKind.OnlyHandled => Loc.Get("Empty.CandHandled"),
        CandidateEmptyKind.AnalysisFailed => Loc.Get("Empty.CandFailed"),
        _ => "",
    };

    /// <summary>画面の状態（案内・作業領域・一覧の空表示）を再評価させる。編集・Undo/Redo・プロジェクトの切り替え・モード変更のたびに呼ぶ。</summary>
    private void NotifyGuidance()
    {
        NormalizePaneForPhase(force: false);
        foreach (var name in GuidanceProperties) OnPropertyChanged(name);
        ShowVideoWorkspaceCommand.NotifyCanExecuteChanged();
        ShowImageWorkspaceCommand.NotifyCanExecuteChanged();
    }

    private static readonly string[] GuidanceProperties =
    [
        nameof(IsImageWorkspace), nameof(IsVideoWorkspace), nameof(ShowWelcome), nameof(ShowWorkspaceSwitch),
        nameof(ShowCutTools), nameof(ShowSubtitleTools),
        nameof(NextAction), nameof(NextActionText), nameof(CurrentFlowStep), nameof(FlowSteps), nameof(FlowAutomationName),
        nameof(ShowCutsEmpty), nameof(ShowSubtitlesEmpty), nameof(ShowShapesEmpty), nameof(ShowCapturesEmpty),
        nameof(CandidateEmpty), nameof(ShowCandidatesEmpty), nameof(CandidateEmptyText),
    ];

    /// <summary>編集の段階が変わったら（確定・見直し・Undo・プロジェクトの切り替え）、その段階の一覧を選び直す。画像作成中は変えない。</summary>
    private EditPhase? _paneNormalizedPhase;

    private void NormalizePaneForPhase(bool force)
    {
        if (Project is null) { _paneNormalizedPhase = null; return; }
        var phase = Project.EditPhase;
        if (!force && _paneNormalizedPhase == phase) return;
        _paneNormalizedPhase = phase;
        if (IsImageWorkspace) return;
        var t = SelectedTabIndex;
        if (phase == EditPhase.Subtitle)
        {
            if (t is TabCuts or TabCandidates) SelectedTabIndex = TabSubtitles;
        }
        else if (t is TabSubtitles or TabShapes)
        {
            SelectedTabIndex = TabCuts;
        }
    }

    // ---------------- 最近のプロジェクト ----------------

    /// <summary>開始画面の最近のプロジェクトを、既存のプロジェクト索引から作り直す（新しい管理ファイルは作らない）。</summary>
    public void RefreshRecentProjects()
    {
        RecentProjects.Clear();
        try
        {
            var root = Settings.Recording.ResolveOutputDirectory();
            var index = new ProjectIndexStore(root, _logger).LoadOrRebuild(out _);
            foreach (var e in RecentProjectList.Build(index, root, RecentProjectCount))
                RecentProjects.Add(new RecentProjectItem(e.Name, e.Date.ToString("yyyy/MM/dd HH:mm"), e.FullPath, e.IsVideoOnly, e.Problem));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Recent project list failed");
        }
        OnPropertyChanged(nameof(ShowRecentEmpty));
    }

    /// <summary>最近のプロジェクトを開く（編集はプロジェクトファイル、編集の無い録画は動画を開く）。</summary>
    [RelayCommand]
    private Task OpenRecentProjectAsync(RecentProjectItem? item)
        => item is null ? Task.CompletedTask : item.IsVideoOnly ? OpenVideoFileAsync(item.FullPath) : OpenProjectFileAsync(item.FullPath);
}
