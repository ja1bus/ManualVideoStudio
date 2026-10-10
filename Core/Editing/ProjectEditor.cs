using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;

namespace ManualVideoStudio.Core.Editing;

public sealed record EditResult(bool Success, string Message)
{
    public static EditResult Ok(string message = "") => new(true, message);
    public static EditResult Fail(string message) => new(false, message);
}

/// <summary>Undo 対象の編集状態（カット・候補・画像ポイント・字幕・図形・編集段階）。</summary>
public sealed class EditState
{
    public List<CutSegment> CutSegments { get; set; } = [];
    public List<CutCandidate> CutCandidates { get; set; } = [];
    public List<CapturePoint> CapturePoints { get; set; } = [];
    public int NextCaptureId { get; set; } = 1;
    public List<SubtitleSegment> SubtitleSegments { get; set; } = [];
    public List<ShapeGroup> ShapeGroups { get; set; } = [];
    public EditPhase EditPhase { get; set; } = EditPhase.Cut;
    public VideoTitle Title { get; set; } = new();
}

/// <summary>
/// プロジェクトに対する編集操作の窓口。すべての変更はここを通し、Undo 用スナップショットと変更通知を行う。
/// UI 非依存。動画長は VideoMetadata.Duration を使う（未取得なら長さ検証を省略）。
/// </summary>
public sealed class ProjectEditor
{
    /// <summary>同一時刻とみなす差（秒）。</summary>
    public const double SameTimeTolerance = 0.001;

    public ProjectEditor(ProjectDocument project)
    {
        Project = project;
        Resequence();
    }

    public ProjectDocument Project { get; }
    public UndoRedoService<EditState> History { get; } = new();
    public bool IsDirty { get; private set; }

    /// <summary>何らかの変更（Undo/Redo 含む）後に発生する。</summary>
    public event EventHandler? Changed;

    public double VideoDuration => Project.VideoMetadata?.Duration ?? 0;

    /// <summary>新しい字幕の文字サイズ・位置の初期値（v1.5 からアプリ設定の字幕タブ。主画面が設定から渡す）。</summary>
    public SubtitleStyle SubtitleDefaults { get; set; } = new();

    /// <summary>字幕編集中でカットを変更できないときの説明。</summary>
    public const string CutLockedMessage = "字幕編集中のため動画カットを変更できません。カットを変更するには［カット編集へ戻る］を押してください（字幕がある場合はすべて破棄します）。";

    /// <summary>字幕編集中（カット確定済み）でカットを変更できないか。</summary>
    public bool IsCutLocked => Project.EditPhase == EditPhase.Subtitle;

    /// <summary>出力に残す区間（元動画基準）。</summary>
    public IReadOnlyList<TimeRange> KeepRanges => EditTimelineCalculator.ComputeKeepRanges(VideoDuration, Project.CutSegments);

    /// <summary>カット適用後の出力動画の長さ（秒）。動画長が不明なら 0。</summary>
    public double OutputDuration => VideoDuration > 0 ? EditTimelineCalculator.OutputDuration(KeepRanges) : 0;

    public void MarkSaved() => IsDirty = false;

    /// <summary>メタデータ等、Undo 対象外の変更を行ったことを通知する。</summary>
    public void MarkDirty()
    {
        IsDirty = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---------------- Undo / Redo ----------------

    public void Undo()
    {
        if (!History.CanUndo) return;
        Restore(History.Undo(Capture()));
    }

    public void Redo()
    {
        if (!History.CanRedo) return;
        Restore(History.Redo(Capture()));
    }

    private EditState Capture() => JsonDefaults.Clone(new EditState
    {
        CutSegments = Project.CutSegments,
        CutCandidates = Project.CutCandidates,
        CapturePoints = Project.CapturePoints,
        NextCaptureId = Project.NextCaptureId,
        SubtitleSegments = Project.SubtitleSegments,
        ShapeGroups = Project.ShapeGroups,
        EditPhase = Project.EditPhase,
        Title = Project.Title,
    });

    private void Restore(EditState state)
    {
        // 注釈は画像ファイルへ上書き保存済みのため主画面の Undo では戻さない（ファイルと食い違わないよう現在の注釈を引き継ぐ）。
        foreach (var p in state.CapturePoints)
        {
            if (Project.CapturePoints.FirstOrDefault(c => c.Id == p.Id) is not { } current) continue;
            p.Annotations = current.Annotations;
            p.AnnotationImageWidth = current.AnnotationImageWidth;
            p.AnnotationImageHeight = current.AnnotationImageHeight;
        }
        Project.CutSegments = state.CutSegments;
        Project.CutCandidates = state.CutCandidates;
        Project.CapturePoints = state.CapturePoints;
        Project.NextCaptureId = state.NextCaptureId;
        Project.SubtitleSegments = state.SubtitleSegments;
        Project.ShapeGroups = state.ShapeGroups;
        Project.EditPhase = state.EditPhase;
        Project.Title = state.Title ?? new VideoTitle();
        Resequence();
        IsDirty = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>変更前状態を記録してから action を実行する。action が false を返したら記録を取り消さない（呼ぶ前に検証すること）。</summary>
    private void Apply(string label, Action action)
    {
        History.Record(label, Capture());
        action();
        Resequence();
        IsDirty = true;
        Project.UpdatedAt = DateTimeOffset.Now;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---------------- 動画冒頭のタイトル（v1.15） ----------------

    /// <summary>
    /// 黒背景タイトルの文章・秒数・フェード・フォント・文字サイズをまとめて設定する（タイトル編集画面の OK。1 回の Undo で戻せる）。
    /// 変わらなければ何も記録せず、編集済みにもしない。文章を空にしても秒数・フェードは保持する。
    /// 字幕・図形・カットの時刻は変えない（タイトルは完成動画の先頭に足されるだけ）。
    /// </summary>
    public EditResult SetTitle(string? text, int seconds, bool fadeIn, string? fontFamily = null, int fontSize = VideoTitle.DefaultFontSize)
    {
        var next = new VideoTitle
        {
            Text = VideoTitle.Normalize(text), Seconds = VideoTitle.NormalizeSeconds(seconds), FadeIn = fadeIn,
            FontFamily = VideoTitle.NormalizeFontFamily(fontFamily), FontSize = VideoTitle.NormalizeFontSize(fontSize),
        };
        if (next.SameAs(Project.Title)) return EditResult.Ok();
        var label = !next.HasTitle ? "タイトルの削除" : Project.Title.HasTitle ? "タイトルの変更" : "タイトルの追加";
        Apply(label, () => Project.Title = next);
        return EditResult.Ok();
    }

    // ---------------- CutSegment ----------------

    public (EditResult Result, CutSegment? Segment) AddCut(double start, double end, SegmentAction action, double keepSeconds,
        SegmentSource source = SegmentSource.Manual, string reason = "", double? confidence = null)
    {
        if (IsCutLocked) return (EditResult.Fail(CutLockedMessage), null);
        start = Math.Round(start, 3);
        end = Math.Round(end, 3);
        var v = CutSegmentValidator.Validate(start, end, action, keepSeconds, VideoDuration, Project.CutSegments);
        if (!v.IsValid) return (EditResult.Fail(v.Message), null);
        var seg = new CutSegment
        {
            Start = start, End = end, Action = action,
            KeepSeconds = action == SegmentAction.Shorten ? Math.Round(keepSeconds, 3) : 0,
            Source = source, Reason = reason, Confidence = confidence,
        };
        Apply(action == SegmentAction.Shorten ? "短縮区間の追加" : "削除区間の追加", () =>
        {
            Project.CutSegments.Add(seg);
            SortCuts();
        });
        return (EditResult.Ok(), seg);
    }

    public EditResult UpdateCut(string id, double start, double end, SegmentAction action, double keepSeconds)
    {
        if (IsCutLocked) return EditResult.Fail(CutLockedMessage);
        var seg = Project.CutSegments.FirstOrDefault(s => s.Id == id);
        if (seg is null) return EditResult.Fail("区間が見つかりません。");
        start = Math.Round(start, 3);
        end = Math.Round(end, 3);
        var v = CutSegmentValidator.Validate(start, end, action, keepSeconds, VideoDuration, Project.CutSegments, id);
        if (!v.IsValid) return EditResult.Fail(v.Message);
        if (seg.Start == start && seg.End == end && seg.Action == action && seg.KeepSeconds == keepSeconds) return EditResult.Ok();
        Apply("区間の変更", () =>
        {
            var target = Project.CutSegments.First(s => s.Id == id);
            target.Start = start;
            target.End = end;
            target.Action = action;
            target.KeepSeconds = action == SegmentAction.Shorten ? Math.Round(keepSeconds, 3) : 0;
            SortCuts();
        });
        return EditResult.Ok();
    }

    public EditResult RemoveCut(string id)
    {
        if (IsCutLocked) return EditResult.Fail(CutLockedMessage);
        if (Project.CutSegments.All(s => s.Id != id)) return EditResult.Fail("区間が見つかりません。");
        Apply("区間の削除", () =>
        {
            Project.CutSegments.RemoveAll(s => s.Id == id);
            // 候補から採用した区間を消した場合は候補を未判断に戻す。
            foreach (var c in Project.CutCandidates.Where(c => c.AcceptedSegmentId == id))
            {
                c.Status = CandidateStatus.Pending;
                c.AcceptedSegmentId = null;
            }
        });
        return EditResult.Ok();
    }

    private void SortCuts() => Project.CutSegments.Sort((a, b) => a.Start.CompareTo(b.Start));

    // ---------------- CutCandidate ----------------

    /// <summary>候補を確定区間として採用する。action / keepSeconds を省略すると候補の提案値を使う。</summary>
    public EditResult AcceptCandidate(string candidateId, SegmentAction? action = null, double? keepSeconds = null)
    {
        if (IsCutLocked) return EditResult.Fail(CutLockedMessage);
        var c = Project.CutCandidates.FirstOrDefault(x => x.Id == candidateId);
        if (c is null) return EditResult.Fail("候補が見つかりません。");
        if (c.Status == CandidateStatus.Accepted) return EditResult.Fail("この候補は採用済みです。");
        var act = action ?? c.SuggestedAction;
        var keep = act == SegmentAction.Shorten ? keepSeconds ?? c.KeepSeconds : 0;
        var start = Math.Round(c.Start, 3);
        var end = Math.Round(Math.Min(c.End, VideoDuration > 0 ? VideoDuration : c.End), 3);
        var v = CutSegmentValidator.Validate(start, end, act, keep, VideoDuration, Project.CutSegments);
        if (!v.IsValid) return EditResult.Fail(v.Message);

        Apply("候補の採用", () =>
        {
            var seg = new CutSegment
            {
                Start = start, End = end, Action = act, KeepSeconds = keep,
                Source = c.Source, Confidence = c.Confidence, Reason = c.Reason,
            };
            Project.CutSegments.Add(seg);
            SortCuts();
            var target = Project.CutCandidates.First(x => x.Id == candidateId);
            target.Status = CandidateStatus.Accepted;
            target.AcceptedSegmentId = seg.Id;
        });
        return EditResult.Ok();
    }

    public EditResult SetCandidateStatus(string candidateId, CandidateStatus status)
    {
        var c = Project.CutCandidates.FirstOrDefault(x => x.Id == candidateId);
        if (c is null) return EditResult.Fail("候補が見つかりません。");
        if (status == CandidateStatus.Accepted) return EditResult.Fail("採用は AcceptCandidate を使ってください。");
        if (c.Status == CandidateStatus.Accepted) return EditResult.Fail("採用済みの候補は、確定区間を削除すると未判断に戻ります。");
        if (c.Status == status) return EditResult.Ok();
        Apply(status == CandidateStatus.Dismissed ? "候補の却下" : "候補を未判断に戻す",
            () => Project.CutCandidates.First(x => x.Id == candidateId).Status = status);
        return EditResult.Ok();
    }

    /// <summary>
    /// 指定出所の未採用候補を置き換える（再解析時）。採用済み候補は採用記録として残す。
    /// </summary>
    public void ReplaceCandidates(SegmentSource source, IEnumerable<CutCandidate> candidates, string label)
    {
        var list = candidates.ToList();
        Apply(label, () =>
        {
            Project.CutCandidates.RemoveAll(c => c.Source == source && c.Status != CandidateStatus.Accepted);
            Project.CutCandidates.AddRange(list);
            Project.CutCandidates.Sort((a, b) => a.Start.CompareTo(b.Start));
        });
    }

    /// <summary>候補を追加する（AI 回答の取り込み。既存候補は残す）。</summary>
    public void AddCandidates(IEnumerable<CutCandidate> candidates, string label)
    {
        var list = candidates.ToList();
        if (list.Count == 0) return;
        Apply(label, () =>
        {
            Project.CutCandidates.AddRange(list);
            Project.CutCandidates.Sort((a, b) => a.Start.CompareTo(b.Start));
        });
    }

    public void ClearCandidates(SegmentSource source)
    {
        if (!Project.CutCandidates.Any(c => c.Source == source && c.Status != CandidateStatus.Accepted)) return;
        Apply("候補のクリア", () => Project.CutCandidates.RemoveAll(c => c.Source == source && c.Status != CandidateStatus.Accepted));
    }

    // ---------------- 編集段階（カット → 字幕） ----------------

    /// <summary>カットを確定して字幕編集へ進む。以後、字幕編集を抜けるまでカットは変更できない。</summary>
    public EditResult ConfirmCuts()
    {
        if (IsCutLocked) return EditResult.Ok();
        if (VideoDuration > 0 && KeepRanges.Count == 0) return EditResult.Fail("すべての区間が削除されるため、字幕を付ける動画がありません。");
        Apply("カットの確定", () => Project.EditPhase = EditPhase.Subtitle);
        return EditResult.Ok();
    }

    /// <summary>
    /// 字幕・図形が 0 件のときだけ、確認なしでカット編集へ戻る。字幕・図形がある場合は失敗する
    /// （<see cref="DiscardSubtitlesAndReturnToCutEditing"/> を使う）。
    /// </summary>
    public EditResult ReturnToCutEditing()
    {
        if (!IsCutLocked) return EditResult.Ok();
        if (Project.SubtitleSegments.Count > 0 || Project.ShapeGroups.Count > 0)
            return EditResult.Fail(Project.ShapeGroups.Count > 0 ? SubtitlesOrShapesExistMessage : SubtitlesExistMessage);
        Apply("カット編集へ戻る", () => Project.EditPhase = EditPhase.Cut);
        return EditResult.Ok();
    }

    public const string SubtitlesExistMessage = "字幕が存在するため動画カットを変更できません。カットを変更するには現在の字幕をすべて破棄する必要があります。";

    /// <summary>図形がある場合の説明（v1.6。図形の時刻も編集後の動画基準のため、字幕と一緒に破棄する）。</summary>
    public const string SubtitlesOrShapesExistMessage = "字幕・図形が存在するため動画カットを変更できません。カットを変更するには現在の字幕と図形をすべて破棄する必要があります。";

    /// <summary>字幕・図形をすべて破棄してカット編集へ戻る（1 回の Undo で元に戻せる）。</summary>
    public EditResult DiscardSubtitlesAndReturnToCutEditing()
    {
        if (!IsCutLocked && Project.SubtitleSegments.Count == 0 && Project.ShapeGroups.Count == 0) return EditResult.Ok();
        Apply(Project.ShapeGroups.Count > 0 ? "字幕・図形の全破棄" : "字幕の全破棄", () =>
        {
            Project.SubtitleSegments.Clear();
            Project.ShapeGroups.Clear();
            Project.EditPhase = EditPhase.Cut;
        });
        return EditResult.Ok();
    }

    // ---------------- 字幕（時刻は出力動画基準） ----------------

    private EditResult? CheckSubtitlePhase()
        => IsCutLocked ? null : EditResult.Fail("字幕を編集するには、先に［カットを確定して字幕編集へ］を押してください。");

    public (EditResult Result, SubtitleSegment? Segment) AddSubtitle(double start, double end, string text,
        SubtitlePosition? position = null, double? fontSize = null)
    {
        if (CheckSubtitlePhase() is { } fail) return (fail, null);
        start = Math.Round(start, 3);
        end = Math.Round(end, 3);
        text = SubtitleValidator.NormalizeText(text);
        var v = SubtitleValidator.Validate(start, end, text, OutputDuration, Project.SubtitleSegments);
        if (!v.IsValid) return (EditResult.Fail(v.Message), null);
        var seg = new SubtitleSegment
        {
            Start = start, End = end, Text = text,
            Position = position ?? SubtitleDefaults.Position,
            FontSize = SubtitleStyle.NormalizeFontSize(fontSize ?? SubtitleDefaults.FontSize),
        };
        Apply("字幕の追加", () =>
        {
            Project.SubtitleSegments.Add(seg);
            SortSubtitles();
        });
        return (EditResult.Ok(), seg);
    }

    public EditResult UpdateSubtitle(string id, double start, double end, string text, SubtitlePosition position, double fontSize)
    {
        if (CheckSubtitlePhase() is { } fail) return fail;
        var seg = Project.SubtitleSegments.FirstOrDefault(s => s.Id == id);
        if (seg is null) return EditResult.Fail("字幕が見つかりません。");
        start = Math.Round(start, 3);
        end = Math.Round(end, 3);
        text = SubtitleValidator.NormalizeText(text);
        fontSize = SubtitleStyle.NormalizeFontSize(fontSize);
        var v = SubtitleValidator.Validate(start, end, text, OutputDuration, Project.SubtitleSegments, id);
        if (!v.IsValid) return EditResult.Fail(v.Message);
        bool timeChanged = seg.Start != start || seg.End != end;
        bool textChanged = seg.Text != text;
        bool styleChanged = seg.Position != position || seg.FontSize != fontSize;
        if (!timeChanged && !textChanged && !styleChanged) return EditResult.Ok();
        var label = (timeChanged, textChanged, styleChanged) switch
        {
            (false, true, false) => "字幕の文字の変更",
            (true, false, false) => "字幕の時刻の変更",
            _ => "字幕の変更",
        };
        Apply(label, () =>
        {
            var target = Project.SubtitleSegments.First(s => s.Id == id);
            target.Start = start;
            target.End = end;
            target.Text = text;
            target.Position = position;
            target.FontSize = fontSize;
            SortSubtitles();
        });
        return EditResult.Ok();
    }

    public EditResult RemoveSubtitles(IEnumerable<string> ids)
    {
        if (CheckSubtitlePhase() is { } fail) return fail;
        var set = ids.ToHashSet();
        if (!Project.SubtitleSegments.Any(s => set.Contains(s.Id))) return EditResult.Fail("字幕が見つかりません。");
        Apply("字幕の削除", () => Project.SubtitleSegments.RemoveAll(s => set.Contains(s.Id)));
        return EditResult.Ok();
    }

    public EditResult RemoveSubtitle(string id) => RemoveSubtitles([id]);

    /// <summary>
    /// 字幕をまとめて取り込み、既存の字幕をすべて置き換える（v1.12。Gemini の回答から。1 回の Undo で元に戻せる）。
    /// 手動字幕と同じ検証（<see cref="SubtitleValidator"/>）を全件に行い、1 件でも不正なら何も変えずに失敗する
    /// （黙って削除・短縮しない）。位置・文字サイズは新しい字幕の既定（<see cref="SubtitleDefaults"/>）、図形は変えない。
    /// </summary>
    public EditResult ImportSubtitles(IReadOnlyList<(double Start, double End, string Text)> entries)
    {
        if (CheckSubtitlePhase() is { } fail) return fail;
        if (entries.Count == 0) return EditResult.Fail("取り込む字幕がありません。");
        var segments = new List<SubtitleSegment>();
        var errors = new List<string>();
        for (int i = 0; i < entries.Count; i++)
        {
            var (s, e, t) = entries[i];
            var start = Math.Round(s, 3);
            var end = Math.Round(e, 3);
            var text = SubtitleValidator.NormalizeText(t);
            var v = SubtitleValidator.Validate(start, end, text, OutputDuration, segments);
            if (!v.IsValid) { errors.AddRange(v.Errors.Select(m => $"{i + 1}番目の字幕: {m}")); continue; }
            segments.Add(new SubtitleSegment
            {
                Start = start, End = end, Text = text,
                Position = SubtitleDefaults.Position,
                FontSize = SubtitleStyle.NormalizeFontSize(SubtitleDefaults.FontSize),
            });
        }
        if (errors.Count > 0) return EditResult.Fail(string.Join(Environment.NewLine, errors.Take(10)));
        var replaced = Project.SubtitleSegments.Count;
        Apply("字幕の取り込み", () =>
        {
            Project.SubtitleSegments = segments;
            SortSubtitles();
        });
        return EditResult.Ok(replaced > 0 ? $"{replaced} 件の字幕を {segments.Count} 件に置き換えました。" : $"字幕を {segments.Count} 件取り込みました。");
    }

    /// <summary>
    /// 字幕 1 件の表示位置・文字サイズ・上下のずらしを変える（v1.5。主画面の字幕の欄から。Undo 対象）。
    /// 時刻・文字は変えない。値が変わらなければ記録しない。
    /// </summary>
    public EditResult SetSubtitleLayout(string id, SubtitlePosition position, double fontSize, double verticalOffset)
    {
        if (CheckSubtitlePhase() is { } fail) return fail;
        var seg = Project.SubtitleSegments.FirstOrDefault(s => s.Id == id);
        if (seg is null) return EditResult.Fail("字幕が見つかりません。");
        if (!Enum.IsDefined(position)) position = SubtitlePosition.Bottom;
        fontSize = SubtitleStyle.NormalizeFontSize(fontSize);
        verticalOffset = SubtitleStyle.NormalizeOffset(verticalOffset);
        bool pos = seg.Position != position, size = seg.FontSize != fontSize, offset = seg.VerticalOffset != verticalOffset;
        if (!pos && !size && !offset) return EditResult.Ok();
        var label = (pos, size, offset) switch
        {
            (true, false, false) => "字幕の位置の変更",
            (false, true, false) => "字幕の文字サイズの変更",
            (false, false, true) => "字幕の上下のずらしの変更",
            _ => "字幕の変更",
        };
        Apply(label, () =>
        {
            var target = Project.SubtitleSegments.First(s => s.Id == id);
            target.Position = position;
            target.FontSize = fontSize;
            target.VerticalOffset = verticalOffset;
        });
        return EditResult.Ok();
    }

    private void SortSubtitles() => Project.SubtitleSegments.Sort((a, b) => a.Start.CompareTo(b.Start));

    // ---------------- 図形（v1.6。時刻は出力動画基準） ----------------

    /// <summary>図形のまとまりを追加する（図形の編集画面の OK）。</summary>
    public (EditResult Result, ShapeGroup? Group) AddShapeGroup(double start, double end, IEnumerable<Annotation> shapes, int imageWidth, int imageHeight)
    {
        if (CheckShapePhase() is { } fail) return (fail, null);
        var list = PrepareShapes(shapes);
        if (list.Count == 0) return (EditResult.Fail("図形がありません。四角形・矢印などを描いてから OK を押してください。"), null);
        start = Math.Round(start, 3);
        end = Math.Round(end, 3);
        var v = ShapeGroupValidator.Validate(start, end, OutputDuration, imageWidth, imageHeight);
        if (!v.IsValid) return (EditResult.Fail(v.Message), null);
        var group = new ShapeGroup { Start = start, End = end, Shapes = list, ImageWidth = imageWidth, ImageHeight = imageHeight };
        Apply("図形の追加", () =>
        {
            Project.ShapeGroups.Add(group);
            SortShapeGroups();
        });
        return (EditResult.Ok(), group);
    }

    /// <summary>
    /// 図形のまとまりの図形・表示時間を置き換える。図形が 0 件になった場合はまとまりを削除する。変化が無ければ記録しない。
    /// </summary>
    public EditResult UpdateShapeGroup(string id, double start, double end, IEnumerable<Annotation> shapes, int imageWidth, int imageHeight)
    {
        if (CheckShapePhase() is { } fail) return fail;
        var group = Project.ShapeGroups.FirstOrDefault(g => g.Id == id);
        if (group is null) return EditResult.Fail("図形が見つかりません。");
        var list = PrepareShapes(shapes);
        if (list.Count == 0) return RemoveShapeGroups([id]);
        start = Math.Round(start, 3);
        end = Math.Round(end, 3);
        var v = ShapeGroupValidator.Validate(start, end, OutputDuration, imageWidth, imageHeight);
        if (!v.IsValid) return EditResult.Fail(v.Message);
        bool timeChanged = group.Start != start || group.End != end;
        bool shapesChanged = ShapeJson(group.Shapes) != ShapeJson(list) || group.ImageWidth != imageWidth || group.ImageHeight != imageHeight;
        if (!timeChanged && !shapesChanged) return EditResult.Ok();
        Apply(timeChanged && !shapesChanged ? "図形の表示時間の変更" : "図形の変更", () =>
        {
            var target = Project.ShapeGroups.First(g => g.Id == id);
            target.Start = start;
            target.End = end;
            target.Shapes = list;
            target.ImageWidth = imageWidth;
            target.ImageHeight = imageHeight;
            SortShapeGroups();
        });
        return EditResult.Ok();
    }

    public EditResult RemoveShapeGroups(IEnumerable<string> ids)
    {
        if (CheckShapePhase() is { } fail) return fail;
        var set = ids.ToHashSet();
        if (!Project.ShapeGroups.Any(g => set.Contains(g.Id))) return EditResult.Fail("図形が見つかりません。");
        Apply("図形の削除", () => Project.ShapeGroups.RemoveAll(g => set.Contains(g.Id)));
        return EditResult.Ok();
    }

    private EditResult? CheckShapePhase()
        => IsCutLocked ? null : EditResult.Fail("図形を入れるには、先に［カットを確定して字幕編集へ］を押してください。");

    /// <summary>保存する図形の複製（座標は小数第 1 位）。</summary>
    private static List<Annotation> PrepareShapes(IEnumerable<Annotation> shapes)
    {
        var list = shapes.Where(a => a is not null).Select(a => a.Clone()).ToList();
        foreach (var a in list) a.RoundCoordinates();
        return list;
    }

    private static string ShapeJson(List<Annotation> shapes) => System.Text.Json.JsonSerializer.Serialize(shapes, JsonDefaults.Options);

    private void SortShapeGroups() => Project.ShapeGroups.Sort((a, b) => a.Start.CompareTo(b.Start));

    // ---------------- CapturePoint ----------------

    public (EditResult Result, CapturePoint? Point) AddCapture(double time)
    {
        time = Math.Round(time, 3);
        if (time < 0 || (VideoDuration > 0 && time > VideoDuration + CutSegmentValidator.Epsilon))
            return (EditResult.Fail("動画の範囲外の時刻です。"), null);
        var dup = Project.CapturePoints.FirstOrDefault(p => Math.Abs(p.SelectedFrameTime - time) < SameTimeTolerance);
        if (dup is not null) return (EditResult.Fail($"同じ時刻の画像ポイント（No.{dup.Sequence}）が既にあります。"), null);

        var point = new CapturePoint { Id = Project.NextCaptureId, Timestamp = time, SelectedFrameTime = time };
        Apply("画像ポイントの追加", () =>
        {
            Project.NextCaptureId = point.Id + 1;
            Project.CapturePoints.Add(point);
        });
        return (EditResult.Ok(), point);
    }

    public EditResult RemoveCapture(int id)
    {
        if (Project.CapturePoints.All(p => p.Id != id)) return EditResult.Fail("画像ポイントが見つかりません。");
        Apply("画像ポイントの削除", () => Project.CapturePoints.RemoveAll(p => p.Id == id));
        return EditResult.Ok();
    }

    /// <summary>画像を切り出す時刻（SelectedFrameTime）を変更する。登録時刻 Timestamp は保持する。</summary>
    public EditResult SetSelectedFrameTime(int id, double time)
    {
        var p = Project.CapturePoints.FirstOrDefault(x => x.Id == id);
        if (p is null) return EditResult.Fail("画像ポイントが見つかりません。");
        time = Math.Round(time, 3);
        if (time < 0 || (VideoDuration > 0 && time > VideoDuration + CutSegmentValidator.Epsilon))
            return EditResult.Fail("動画の範囲外の時刻です。");
        if (Math.Abs(p.SelectedFrameTime - time) < SameTimeTolerance) return EditResult.Ok();
        var dup = Project.CapturePoints.FirstOrDefault(x => x.Id != id && Math.Abs(x.SelectedFrameTime - time) < SameTimeTolerance);
        if (dup is not null) return EditResult.Fail($"同じ時刻の画像ポイント（No.{dup.Sequence}）が既にあります。");
        Apply("画像ポイントの時刻変更", () => Project.CapturePoints.First(x => x.Id == id).SelectedFrameTime = time);
        return EditResult.Ok();
    }

    public EditResult SetCaption(int id, string caption, CaptionSource source = CaptionSource.Manual)
    {
        var p = Project.CapturePoints.FirstOrDefault(x => x.Id == id);
        if (p is null) return EditResult.Fail("画像ポイントが見つかりません。");
        caption = (caption ?? "").Trim();
        if (p.Caption == caption) return EditResult.Ok();
        Apply("キャプションの変更", () =>
        {
            var target = Project.CapturePoints.First(x => x.Id == id);
            target.Caption = caption;
            target.CaptionSource = caption.Length == 0 ? CaptionSource.None : source;
        });
        return EditResult.Ok();
    }

    /// <summary>複数のキャプションを 1 回の Undo 単位で設定する（AI 取り込み）。存在しない Id は無視して件数を返す。</summary>
    public int ApplyCaptions(IEnumerable<(int Id, string Caption)> captions, CaptionSource source, string label = "キャプションの取り込み")
        => ApplyCaptions(captions.Select(c => (c.Id, c.Caption, (string?)null)), source, label);

    /// <summary>
    /// キャプションと操作文（v1.9）を 1 回の Undo 単位で設定する。StepText が null の行は操作文を変えない
    /// （回答に step_text が無い・利用者の編集版テンプレートの場合に、入力済みの操作文を消さないため）。
    /// </summary>
    public int ApplyCaptions(IEnumerable<(int Id, string Caption, string? StepText)> captions, CaptionSource source, string label = "キャプションの取り込み")
    {
        var list = captions.Where(c => Project.CapturePoints.Any(p => p.Id == c.Id)).ToList();
        if (list.Count == 0) return 0;
        Apply(label, () =>
        {
            foreach (var (id, caption, stepText) in list)
            {
                var p = Project.CapturePoints.First(x => x.Id == id);
                p.Caption = caption.Trim();
                p.CaptionSource = p.Caption.Length == 0 ? CaptionSource.None : source;
                if (stepText is not null) p.StepText = CaptureText.Line(stepText);
            }
        });
        return list.Count;
    }

    /// <summary>手順書の操作文を変える（v1.9。1 行）。</summary>
    public EditResult SetStepText(int id, string text)
        => SetCaptureText(id, CaptureText.Line(text), p => p.StepText, (p, v) => p.StepText = v, "操作文の変更");

    /// <summary>手順書の補足を変える（v1.9。複数行可）。</summary>
    public EditResult SetNote(int id, string text)
        => SetCaptureText(id, CaptureText.Note(text), p => p.Note, (p, v) => p.Note = v, "補足の変更");

    /// <summary>この画像から始まる章の名前を変える（v1.9。空にすると章の区切りをなくす）。</summary>
    public EditResult SetChapterTitle(int id, string text)
        => SetCaptureText(id, CaptureText.Line(text), p => p.ChapterTitle, (p, v) => p.ChapterTitle = v, "章の変更");

    private EditResult SetCaptureText(int id, string value, Func<CapturePoint, string> get, Action<CapturePoint, string> set, string label)
    {
        var p = Project.CapturePoints.FirstOrDefault(x => x.Id == id);
        if (p is null) return EditResult.Fail("画像ポイントが見つかりません。");
        if (get(p) == value) return EditResult.Ok();
        Apply(label, () => set(Project.CapturePoints.First(x => x.Id == id), value));
        return EditResult.Ok();
    }

    /// <summary>画像書き出し結果のファイル名を記録する（Undo 対象外）。</summary>
    public void SetOutputFilenames(IReadOnlyDictionary<int, string> filenames)
    {
        foreach (var p in Project.CapturePoints)
            if (filenames.TryGetValue(p.Id, out var name)) p.OutputFilename = name;
        MarkDirty();
    }

    /// <summary>
    /// 画像の注釈を置き換える（注釈編集画面の保存）。画像ファイルは既に上書き済みのため主画面の Undo 対象外。
    /// </summary>
    public EditResult SetAnnotations(int id, IEnumerable<Annotation> annotations, int imageWidth, int imageHeight)
    {
        var p = Project.CapturePoints.FirstOrDefault(x => x.Id == id);
        if (p is null) return EditResult.Fail("画像ポイントが見つかりません。");
        p.Annotations = annotations.Select(a => a.Clone()).ToList();
        foreach (var a in p.Annotations) a.RoundCoordinates();
        p.AnnotationImageWidth = p.Annotations.Count > 0 ? imageWidth : 0;
        p.AnnotationImageHeight = p.Annotations.Count > 0 ? imageHeight : 0;
        Project.UpdatedAt = DateTimeOffset.Now;
        MarkDirty();
        return EditResult.Ok();
    }

    /// <summary>AI 履歴を追加・更新する（Undo 対象外）。</summary>
    public void UpsertAiHistory(AiHistoryEntry entry)
    {
        var i = Project.AiHistory.FindIndex(h => h.Id == entry.Id);
        if (i >= 0) Project.AiHistory[i] = entry; else Project.AiHistory.Add(entry);
        MarkDirty();
    }

    /// <summary>Sequence を SelectedFrameTime 順（同時刻は Id 順）に 1 から振り直す。</summary>
    private void Resequence()
    {
        Project.CapturePoints.Sort((a, b) =>
        {
            int c = a.SelectedFrameTime.CompareTo(b.SelectedFrameTime);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        });
        for (int i = 0; i < Project.CapturePoints.Count; i++) Project.CapturePoints[i].Sequence = i + 1;
    }
}
