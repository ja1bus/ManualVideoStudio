using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Editing;

/// <summary>クリック位置の強調（v1.21。設計 1.21.0 §7.5）の ON/OFF と大きさ。</summary>
public sealed partial class ProjectEditor
{
    /// <summary>
    /// クリック位置の強調の ON/OFF と大きさを変える（1 回の元に戻す操作で戻せる）。変わらなければ何も記録しない。
    /// 元に戻す記録は設定のオブジェクトを参照で持つため、書き換えずに新しいオブジェクトへ差し替える。
    /// 字幕編集中・カット編集中のどちらでも変えられる（時刻は元動画基準で、カットに依存しない）。
    /// </summary>
    public EditResult SetClickHighlight(bool enabled, ClickHighlightSize size)
    {
        if (!Enum.IsDefined(size)) size = ClickHighlightSize.Medium;
        var current = Project.ClickHighlight;
        if (current is not null && current.Enabled == enabled && current.Size == size) return EditResult.Ok();
        var label = current is null || current.Enabled != enabled
            ? (enabled ? "クリック位置の強調を ON" : "クリック位置の強調を OFF")
            : "クリック位置の強調の大きさの変更";
        Apply(label, () => Project.ClickHighlight = new ClickHighlightSettings { Enabled = enabled, Size = size });
        return EditResult.Ok();
    }
}
