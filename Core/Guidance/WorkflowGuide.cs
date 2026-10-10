namespace ManualVideoStudio.Core.Guidance;

/// <summary>主画面の作業領域（v1.13）。動画編集と画像作成は、同じプロジェクトの見え方を切り替えるだけで、データは変えない。</summary>
public enum GuideWorkspace
{
    Video,
    Images,
}

/// <summary>作業の流れの 1 段階。動画編集は Source → Cut → SubtitleShape → Export、画像作成は Source → ImageAdd → CaptionAnnotation → Export。</summary>
public enum FlowStep
{
    Source,
    Cut,
    SubtitleShape,
    ImageAdd,
    CaptionAnnotation,
    Export,
}

/// <summary>「次にすること」の案内の種類（文言はアプリ側の文字列リソースで持つ）。</summary>
public enum GuideNextAction
{
    /// <summary>動画未読込。録画するか動画を開く。</summary>
    StartNew,
    CutEditing,
    SubtitleEditing,
    /// <summary>Gemini の字幕を取り込んだ直後（取り込んだ字幕のまま）。</summary>
    SubtitlesImported,
    ImageCreation,
}

/// <summary>カット候補の一覧が空のときの理由。解析の失敗を「候補なし」と表示しないために区別する。</summary>
public enum CandidateEmptyKind
{
    /// <summary>候補がある（案内は出さない）。</summary>
    None,
    NotAnalyzed,
    AnalyzedNoCandidates,
    /// <summary>候補はあるが、すべて採用済み・却下済みで一覧に出していない。</summary>
    OnlyHandled,
    AnalysisFailed,
}

/// <summary>解析の実行状態（保存しない。プロジェクトを開いた直後は、保存されている結果から推定する）。</summary>
public enum AnalysisRunState
{
    NotRun,
    Done,
    Failed,
}

/// <summary>主画面の状態から、作業の流れ・次にすること・空の一覧の案内を決める（UI 非依存。永続化する進捗は持たない）。</summary>
public static class WorkflowGuide
{
    public static IReadOnlyList<FlowStep> Steps(GuideWorkspace workspace) => workspace == GuideWorkspace.Images
        ? [FlowStep.Source, FlowStep.ImageAdd, FlowStep.CaptionAnnotation, FlowStep.Export]
        : [FlowStep.Source, FlowStep.Cut, FlowStep.SubtitleShape, FlowStep.Export];

    /// <summary>
    /// 現在地。動画編集は未読込 = 素材、カット編集 = カット、字幕・図形編集 = 字幕・図形。
    /// 画像作成は未読込 = 素材、画像が 0 件 = 画像追加、1 件以上 = キャプション・注釈。「書き出し」は工程の完了を判定しないため現在地にしない。
    /// </summary>
    public static FlowStep CurrentStep(bool hasProject, GuideWorkspace workspace, bool isSubtitlePhase, int captureCount)
    {
        if (!hasProject) return FlowStep.Source;
        if (workspace == GuideWorkspace.Images) return captureCount == 0 ? FlowStep.ImageAdd : FlowStep.CaptionAnnotation;
        return isSubtitlePhase ? FlowStep.SubtitleShape : FlowStep.Cut;
    }

    public static GuideNextAction NextAction(bool hasProject, GuideWorkspace workspace, bool isSubtitlePhase, bool subtitlesJustImported)
    {
        if (!hasProject) return GuideNextAction.StartNew;
        if (workspace == GuideWorkspace.Images) return GuideNextAction.ImageCreation;
        if (!isSubtitlePhase) return GuideNextAction.CutEditing;
        return subtitlesJustImported ? GuideNextAction.SubtitlesImported : GuideNextAction.SubtitleEditing;
    }

    /// <param name="pending">未判断の候補の数。</param>
    /// <param name="total">候補の総数（採用済み・却下済みを含む）。</param>
    /// <param name="shown">一覧に出している候補の数（「却下・採用済みも表示」の設定を反映）。</param>
    public static CandidateEmptyKind CandidateEmpty(AnalysisRunState state, int total, int shown)
    {
        if (shown > 0) return CandidateEmptyKind.None;
        if (state == AnalysisRunState.Failed) return CandidateEmptyKind.AnalysisFailed;
        if (total > 0) return CandidateEmptyKind.OnlyHandled;
        return state == AnalysisRunState.Done ? CandidateEmptyKind.AnalyzedNoCandidates : CandidateEmptyKind.NotAnalyzed;
    }

    /// <summary>プロジェクトを開いた直後の解析状態。解析した結果（画面変化・ローカル候補）が残っていれば実行済みとみなす。</summary>
    public static AnalysisRunState InferAnalysisState(int sceneChanges, int localCandidates)
        => sceneChanges > 0 || localCandidates > 0 ? AnalysisRunState.Done : AnalysisRunState.NotRun;
}
