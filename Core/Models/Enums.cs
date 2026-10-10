namespace ManualVideoStudio.Core.Models;

/// <summary>区間に対する編集動作。Keep は計算上の「残す区間」を表し、確定編集として保存するのは Remove / Shorten。</summary>
public enum SegmentAction
{
    Keep,
    Remove,
    Shorten,
}

/// <summary>確定済みカット区間・候補の出所。</summary>
public enum SegmentSource
{
    Manual,
    LocalAnalysis,
    Ai,
}

/// <summary>ローカル解析候補の検出種別。AI 候補は Ai。</summary>
public enum CandidateKind
{
    Freeze,
    Silence,
    FreezeAndSilence,
    Ai,
}

/// <summary>候補の状態。Accepted になった候補は確定 CutSegment を別途持つ。</summary>
public enum CandidateStatus
{
    Pending,
    Accepted,
    Dismissed,
}

/// <summary>キャプションの出所。</summary>
public enum CaptionSource
{
    None,
    Manual,
    Ai,
}

public enum ImageFormat
{
    Png,
    Jpeg,
}

/// <summary>AI 連携の用途。プロンプトテンプレートの種類と 1:1。</summary>
public enum AiPurpose
{
    CaptureCaption,
    CutAnalysis,
    CutReview,
    /// <summary>手順書作成の依頼文（v1.9。手順書用の書き出しの完了画面でコピーする。AI 連携画面では使わない）。</summary>
    TejunshoRequest,
    /// <summary>Gemini へ字幕（SRT）を作らせる依頼文（v1.12。字幕編集の［Geminiで字幕を作る］でコピーする。AI 連携画面では使わない。時間軸はカット後の動画）。</summary>
    SubtitleRequest,
}
