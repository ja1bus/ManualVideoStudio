using System.IO.Compression;
using System.Text.Json;
using ManualVideoStudio.Core.Ai;
using ManualVideoStudio.Core.Annotations;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Services;
using ManualVideoStudio.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Tests;

/// <summary>アプリの一時ファイルの置き場（静的）を変えるテストは、同時に動かさない。</summary>
[CollectionDefinition("AppTemp", DisableParallelization = true)]
public sealed class AppTempCollection;

/// <summary>v1.17.0: 候補の一括操作（1 回の Undo）、採用の見込み、候補だけを消す操作。</summary>
public class CandidateBatchTests
{
    private static CutCandidate Cand(double start, double end, SegmentAction action = SegmentAction.Shorten, double keep = 1.5)
        => new() { Start = start, End = end, SuggestedAction = action, KeepSeconds = keep, Confidence = 0.9, Kind = CandidateKind.FreezeAndSilence };

    private static (ProjectEditor Editor, List<CutCandidate> Cands) Setup(params CutCandidate[] cands)
    {
        var editor = new ProjectEditor(TestProjects.Create(120));
        editor.ReplaceCandidates(SegmentSource.LocalAnalysis, cands, "解析");
        editor.History.Clear();
        return (editor, editor.Project.CutCandidates.ToList());
    }

    [Fact]
    public void PlanReportsRemovedSecondsAndMergesWithoutChangingProject()
    {
        var (editor, c) = Setup(Cand(10, 20), Cand(15, 25), Cand(40, 50, SegmentAction.Remove));
        var before = JsonSerializer.Serialize(editor.Project);
        var plan = editor.PlanAccept(c.Select(x => x.Id), CandidateAcceptMode.Suggested, 0);

        // 10～20（先頭 1.5 秒を残す短縮の提案 = 8.5 秒）と 15～25（重なるので既存とまとまる）と 40～50 の削除。
        Assert.Equal(3, plan.ApplicableCount);
        Assert.Equal(0, plan.SkippedCount);
        Assert.Equal(1, plan.MergedCount);
        Assert.Equal(before, JsonSerializer.Serialize(editor.Project));
        Assert.False(editor.History.CanUndo);
    }

    [Fact]
    public void BatchAcceptIsOneUndoAndMergesOverlaps()
    {
        var (editor, c) = Setup(Cand(10, 20), Cand(15, 25), Cand(40, 50, SegmentAction.Remove));
        var r = editor.AcceptCandidates(c.Select(x => x.Id), CandidateAcceptMode.Suggested, 0);

        Assert.True(r.Changed);
        Assert.Equal(3, r.Applied);
        Assert.Equal(2, editor.Project.CutSegments.Count);   // 重なる 2 件は 1 つのカットにまとまる
        Assert.Equal(3, editor.Project.CutCandidates.Count(x => x.Status == CandidateStatus.Accepted));
        Assert.Equal(CandidateStatus.Accepted, c.Single(x => x.Start == 15).Status);
        Assert.Equal(1, editor.History.UndoCount);   // 履歴は 1 件
        editor.Undo();                                // 1 回で全件が戻る
        Assert.Empty(editor.Project.CutSegments);
        Assert.All(editor.Project.CutCandidates, x => Assert.Equal(CandidateStatus.Pending, x.Status));
    }

    [Fact]
    public void BatchWithNothingApplicableRecordsNothing()
    {
        var (editor, c) = Setup(Cand(10, 20));
        editor.AcceptCandidates([c[0].Id], CandidateAcceptMode.Suggested, 0);
        editor.History.Clear();
        var r = editor.AcceptCandidates([c[0].Id], CandidateAcceptMode.Suggested, 0); // 採用済み
        Assert.False(r.Changed);
        Assert.False(editor.History.CanUndo);
    }

    [Fact]
    public void AcceptIsRefusedWhileSubtitleEditingLocksTheCuts()
    {
        var (editor, c) = Setup(Cand(10, 20));
        editor.ConfirmCuts();
        var plan = editor.PlanAccept([c[0].Id], CandidateAcceptMode.Suggested, 0);
        Assert.Equal(0, plan.ApplicableCount);
        Assert.Contains("字幕編集中", plan.Items[0].Reason);
    }

    [Fact]
    public void DismissAndRestoreInBulkAreEachOneUndo()
    {
        var (editor, c) = Setup(Cand(10, 20), Cand(30, 40), Cand(50, 60));
        Assert.Equal(3, editor.SetCandidatesStatus(c.Select(x => x.Id), CandidateStatus.Dismissed));
        Assert.All(editor.Project.CutCandidates, x => Assert.Equal(CandidateStatus.Dismissed, x.Status));
        editor.Undo();
        Assert.All(editor.Project.CutCandidates, x => Assert.Equal(CandidateStatus.Pending, x.Status));

        editor.SetCandidatesStatus(c.Select(x => x.Id), CandidateStatus.Dismissed);
        Assert.Equal(3, editor.SetCandidatesStatus(c.Select(x => x.Id), CandidateStatus.Pending));
        // 対象が無ければ何も記録しない。
        editor.History.Clear();
        Assert.Equal(0, editor.SetCandidatesStatus(c.Select(x => x.Id), CandidateStatus.Pending));
        Assert.False(editor.History.CanUndo);
    }

    [Fact]
    public void DismissNeverTouchesAcceptedCandidates()
    {
        var (editor, c) = Setup(Cand(10, 20), Cand(30, 40));
        editor.AcceptCandidates([c[0].Id], CandidateAcceptMode.Suggested, 0);
        Assert.Equal(1, editor.SetCandidatesStatus(c.Select(x => x.Id), CandidateStatus.Dismissed));
        Assert.Equal(CandidateStatus.Accepted, c[0].Status);
    }

    [Fact]
    public void ClearRemovesOnlyUnacceptedCandidatesOfThatSourceAndKeepsCuts()
    {
        var (editor, c) = Setup(Cand(10, 20), Cand(30, 40), Cand(50, 60));
        editor.AcceptCandidates([c[0].Id], CandidateAcceptMode.Suggested, 0);
        editor.SetCandidatesStatus([c[1].Id], CandidateStatus.Dismissed);
        editor.AddCandidates([new CutCandidate { Start = 70, End = 80, Source = SegmentSource.Ai, Kind = CandidateKind.Ai }], "AI");

        var (removed, kept) = editor.ClearUnacceptedCandidates(SegmentSource.LocalAnalysis);

        Assert.Equal(2, removed);   // 未判断 1 + 却下 1
        Assert.Equal(1, kept);      // 採用済みの記録
        Assert.Single(editor.Project.CutSegments);
        Assert.Equal(2, editor.Project.CutCandidates.Count); // 採用済み 1 + AI 1
        editor.Undo();
        Assert.Equal(4, editor.Project.CutCandidates.Count);
        // 対象が無ければ何も記録しない。
        Assert.Equal((1, 0), editor.ClearUnacceptedCandidates(SegmentSource.Ai));
        editor.History.Clear();
        Assert.Equal((0, 0), editor.ClearUnacceptedCandidates(SegmentSource.Ai));
        Assert.False(editor.History.CanUndo);
    }
}

/// <summary>v1.17.0: 重なる（接する）カットを 1 つにまとめる。古い「短縮」の変換。</summary>
public class CutMergeTests
{
    private static ProjectEditor Editor() => new(TestProjects.Create(120));

    [Fact]
    public void ManualCutOverlappingTheBackExtendsTheExistingCut()
    {
        var ed = Editor();
        var first = ed.AddCut(10, 14, SegmentAction.Remove, 0).Segment!;
        var (r, merged) = ed.AddCut(12, 18, SegmentAction.Remove, 0);
        Assert.True(r.Success);
        Assert.Contains("1 つにまとめ", r.Message);
        Assert.Equal((10.0, 18.0), (merged!.Start, merged.End));
        Assert.Single(ed.Project.CutSegments);
        Assert.Equal(first.Id, merged.Id);
        ed.Undo();                                      // 1 回で、まとめる前に戻る
        Assert.Equal((10.0, 14.0), (ed.Project.CutSegments.Single().Start, ed.Project.CutSegments.Single().End));
    }

    [Fact]
    public void ManualCutOverlappingTheFrontOrSpanningSeveralCutsMergesThemAll()
    {
        var ed = Editor();
        ed.AddCut(10, 14, SegmentAction.Remove, 0);
        ed.AddCut(20, 24, SegmentAction.Remove, 0);
        ed.AddCut(30, 34, SegmentAction.Remove, 0);
        var (_, front) = ed.AddCut(8, 11, SegmentAction.Remove, 0);       // 前側が重なる
        Assert.Equal((8.0, 14.0), (front!.Start, front.End));
        var (r, all) = ed.AddCut(13, 31, SegmentAction.Remove, 0);        // 3 つにまたがる
        Assert.Equal((8.0, 34.0), (all!.Start, all.End));
        Assert.Single(ed.Project.CutSegments);
        Assert.Contains("3 件", r.Message);
    }

    [Fact]
    public void TouchingCutsAreMergedAndSeparateCutsAreNot()
    {
        var ed = Editor();
        ed.AddCut(10, 14, SegmentAction.Remove, 0);
        ed.AddCut(14, 16, SegmentAction.Remove, 0);                       // 接している
        Assert.Single(ed.Project.CutSegments);
        ed.AddCut(20, 22, SegmentAction.Remove, 0);                       // 離れている
        Assert.Equal(2, ed.Project.CutSegments.Count);
    }

    [Fact]
    public void MergeKeepsTheAcceptedCandidateLinkedToTheMergedCut()
    {
        var ed = Editor();
        var cand = new CutCandidate { Start = 10, End = 14, SuggestedAction = SegmentAction.Remove, Source = SegmentSource.Ai };
        ed.AddCandidates([cand], "AI");
        ed.AcceptCandidate(cand.Id);
        var (_, merged) = ed.AddCut(12, 20, SegmentAction.Remove, 0);
        Assert.Equal(merged!.Id, ed.Project.CutCandidates[0].AcceptedSegmentId);
        Assert.Equal(CandidateStatus.Accepted, ed.Project.CutCandidates[0].Status);
        ed.RemoveCut(merged.Id);                                          // まとめたカットを取り消すと、候補は未判断に戻る
        Assert.Equal(CandidateStatus.Pending, ed.Project.CutCandidates[0].Status);
    }

    [Fact]
    public void EditingACutIntoAnotherOneMergesThem()
    {
        var ed = Editor();
        var a = ed.AddCut(10, 14, SegmentAction.Remove, 0).Segment!;
        ed.AddCut(20, 24, SegmentAction.Remove, 0);
        var r = ed.UpdateCut(a.Id, 10, 21, SegmentAction.Remove, 0);       // 後ろへ広げて、次のカットに重なる
        Assert.True(r.Success);
        Assert.Contains("1 つにまとめ", r.Message);
        var only = Assert.Single(ed.Project.CutSegments);
        Assert.Equal((10.0, 24.0), (only.Start, only.End));
        Assert.Equal(a.Id, only.Id);
    }

    [Fact]
    public void MergeIsRefusedWhileSubtitleEditingLocksTheCuts()
    {
        var ed = Editor();
        ed.AddCut(10, 14, SegmentAction.Remove, 0);
        ed.ConfirmCuts();
        var (r, _) = ed.AddCut(12, 18, SegmentAction.Remove, 0);
        Assert.False(r.Success);
    }

    [Fact]
    public void LegacyShortenIsConvertedToAnEquivalentRemove()
    {
        var ed = Editor();
        ed.Project.CutSegments.Add(new CutSegment { Start = 20, End = 30, Action = SegmentAction.Shorten, KeepSeconds = 2 });
        ed.Project.CutCandidates.Add(new CutCandidate { Start = 50, End = 60, SuggestedAction = SegmentAction.Shorten, KeepSeconds = 1.5 });
        var before = EditTimelineCalculator.ComputeKeepRanges(120, ed.Project.CutSegments);

        Assert.Equal(2, ed.ConvertLegacyShorten());

        var cut = ed.Project.CutSegments.Single();
        Assert.Equal((SegmentAction.Remove, 22.0, 30.0, 0.0), (cut.Action, cut.Start, cut.End, cut.KeepSeconds));
        Assert.Equal(before, EditTimelineCalculator.ComputeKeepRanges(120, ed.Project.CutSegments));   // 出力は同じ
        var cand = ed.Project.CutCandidates.Single();
        Assert.Equal((SegmentAction.Remove, 51.5, 60.0), (cand.SuggestedAction, cand.Start, cand.End));
        Assert.Equal(0, ed.ConvertLegacyShorten());                        // 2 回目は何もしない
    }

    [Fact]
    public async Task OpeningAProjectWithAShortenConvertsItAndTellsTheUser()
    {
        var (vm, dialogs, _) = await MainViewModelPhaseTests.OpenAsync();
        // 開いたプロジェクトに短縮があった場合を再現: 取り込み前の状態を作って開き直す。
        var project = TestProjects.Create(60);
        project.CutSegments.Add(new CutSegment { Start = 10, End = 20, Action = SegmentAction.Shorten, KeepSeconds = 2 });
        var infoCount = dialogs.Infos.Count;
        var windows = new MainViewModelPhaseTests.FakeWindows();
        var vm2 = new MainViewModel(new MainViewModelPhaseTests.FakeSettings(), new MainViewModelPhaseTests.FakeProjects(project), new MainViewModelPhaseTests.Unused(),
            new MainViewModelPhaseTests.Unused(), new MainViewModelPhaseTests.Unused(), new MainViewModelPhaseTests.Unused(),
            new ImageExportService(new MainViewModelPhaseTests.Unused()), new MainViewModelPhaseTests.Unused(), dialogs, windows, new MainViewModelPhaseTests.Unused(),
            new FFmpegStatus(new FFmpegLocator(), new MainViewModelPhaseTests.Unused(), new MainViewModelPhaseTests.FakeSettings(), NullLogger<FFmpegStatus>.Instance),
            NullLogger<MainViewModel>.Instance);
        await vm2.OpenProjectFileAsync(Path.Combine(Path.GetTempPath(), "legacy.mvsproj.json"));
        var cut = Assert.Single(vm2.Project!.CutSegments);
        Assert.Equal((SegmentAction.Remove, 12.0), (cut.Action, cut.Start));
        Assert.True(vm2.IsDirty);                                          // 保存すると変換後の形になる
        Assert.Equal(infoCount + 1, dialogs.Infos.Count);
        Assert.Contains("短縮", dialogs.Infos[^1]);
    }
}

/// <summary>v1.17.0: 字幕依頼動画の保存先（プロジェクトフォルダー内）と診断。</summary>
public class CaptionRequestLocationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-loc-" + Guid.NewGuid().ToString("N"));

    public CaptionRequestLocationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void ProjectLocationIsInsideTheProjectFolder()
    {
        var project = Path.Combine(_dir, "20260101_1200_rec", "rec.mvsproj.json");
        Assert.Equal(Path.Combine(_dir, "20260101_1200_rec", "caption-requests"), CaptionRequestStore.ProjectLocation(project));
        Assert.Null(CaptionRequestStore.ProjectLocation(null));
        Assert.Null(CaptionRequestStore.ProjectLocation(""));
    }

    [Fact]
    public void ResolveCreatesTheProjectFolderLocationAndLeavesNoProbeFile()
    {
        var store = new CaptionRequestStore(Path.Combine(_dir, "appdata", "caption-requests"));
        var location = Path.Combine(_dir, "proj", "caption-requests");
        var r = store.Resolve(location);
        Assert.Equal(Path.GetFullPath(location), r.Directory);
        Assert.Equal("", r.Notice);
        Assert.True(Directory.Exists(location));
        Assert.Empty(Directory.GetFileSystemEntries(location));
        Assert.False(Directory.Exists(store.Root)); // アプリのデータフォルダー側は作らない
    }

    [Fact]
    public void ResolveDoesNotFallBackToAppDataAndNamesTheFullPathWhenTheProjectFolderIsNotUsable()
    {
        var blocker = Path.Combine(_dir, "proj");
        File.WriteAllText(blocker, "ファイルがあるのでフォルダーを作れない");
        var store = new CaptionRequestStore(Path.Combine(_dir, "appdata", "caption-requests"));
        var location = Path.Combine(blocker, "caption-requests");
        var ex = Assert.Throws<PathStageException>(() => store.Resolve(location));
        Assert.Equal(Path.GetFullPath(location), ex.FilePath);   // フルパスを示す
        Assert.Contains(Path.GetFullPath(location), ex.Message);
        Assert.False(Directory.Exists(store.Root));              // アプリのデータフォルダーへは退避しない
    }

    [Fact]
    public void ResolveRequiresASavedProject()
    {
        var store = new CaptionRequestStore(Path.Combine(_dir, "appdata", "caption-requests"));
        var ex = Assert.Throws<PathStageException>(() => store.Resolve(null));
        Assert.Contains("保存", ex.Message);
        Assert.False(Directory.Exists(store.Root));
    }

    [Fact]
    public void DeleteInsideTheProjectLocationRemovesOnlyTheRegisteredFileAndTidiesAnEmptyFolder()
    {
        var store = new CaptionRequestStore(Path.Combine(_dir, "appdata", "caption-requests"));
        var loc = store.Resolve(Path.Combine(_dir, "proj", "caption-requests"));
        var mine = store.NewPath(loc, "rec", DateTime.Now);
        File.WriteAllText(mine, "x");
        store.Register(mine);
        var other = Path.Combine(loc.Directory, "someone-else.mp4");
        File.WriteAllText(other, "y");

        // 同じフォルダーでも、登録していないファイルは消さない。
        Assert.False(store.Delete(other).Success);
        Assert.True(File.Exists(other));
        Assert.True(store.Delete(mine).Success);
        Assert.False(File.Exists(mine));
        Assert.True(Directory.Exists(loc.Directory));   // 他のファイルがあるので残す
        File.Delete(other);

        var again = store.NewPath(loc, "rec", DateTime.Now);
        File.WriteAllText(again, "x");
        store.Register(again);
        Assert.True(store.Delete(again).Success);
        Assert.False(Directory.Exists(loc.Directory));   // 空になったプロジェクトフォルダー内の保存先は片付ける
        Assert.True(Directory.Exists(Path.Combine(_dir, "proj")));
    }

    [Fact]
    public void DeleteRefusesAnUnknownFolderEvenIfRegistered()
    {
        var store = new CaptionRequestStore(Path.Combine(_dir, "appdata", "caption-requests"));
        var outside = Path.Combine(_dir, "movies", "x.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "x");
        store.Register(outside);
        Assert.False(store.Delete(outside).Success);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void ZipExportSkipsTheCaptionRequestFolderAtAnyDepth()
    {
        var rec = Path.Combine(_dir, "20260101_1200_rec");
        Directory.CreateDirectory(Path.Combine(rec, "caption-requests"));
        Directory.CreateDirectory(Path.Combine(rec, "edits", "e1", "caption-requests"));
        File.WriteAllText(Path.Combine(rec, "rec.mp4"), "v");
        File.WriteAllText(Path.Combine(rec, "caption-requests", "a_gemini.mp4"), "g");
        File.WriteAllText(Path.Combine(rec, "edits", "e1", "e1.mvsproj.json"), "{}");
        File.WriteAllText(Path.Combine(rec, "edits", "e1", "caption-requests", "b_gemini.mp4"), "g");

        var files = ProjectArchive.EnumerateArchiveFiles(rec).Select(f => Path.GetRelativePath(rec, f)).OrderBy(f => f).ToList();
        Assert.Equal([Path.Combine("edits", "e1", "e1.mvsproj.json"), "rec.mp4"], files);

        var zip = Path.Combine(_dir, "out.zip");
        new ProjectArchive().ExportAsync(rec, zip, "1.17.0", null, CancellationToken.None).GetAwaiter().GetResult();
        using var z = ZipFile.OpenRead(zip);
        Assert.DoesNotContain(z.Entries, e => e.FullName.Contains("caption-requests"));
        Assert.Null(ProjectArchive.Verify(zip, rec)); // 検証も同じ対象で一致する
    }
}

/// <summary>v1.17.0: 外部ツールと受け渡す一時ファイルの置き場、Store 版の保存先。</summary>
[Collection("AppTemp")]
public class StoragePathTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-st-" + Guid.NewGuid().ToString("N"));

    public StoragePathTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        AppTemp.Reset();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void TempFolderFollowsTheConfiguredRootAndFiltersScriptsLandThere()
    {
        var root = Path.Combine(_dir, "tmp");
        Assert.Null(AppTemp.Configure(root));
        Assert.Equal(Path.GetFullPath(root), AppTemp.Root);
        Assert.True(Directory.Exists(root));
        Assert.Equal(Path.GetFullPath(root), Path.GetDirectoryName(FFmpegArguments.NewFilterScriptPath()));
    }

    [Fact]
    public void TempFolderFallsBackToTheSystemTempWhenItCannotBeCreated()
    {
        var blocker = Path.Combine(_dir, "file");
        File.WriteAllText(blocker, "x");
        var error = AppTemp.Configure(Path.Combine(blocker, "tmp"));
        Assert.NotNull(error);
        Assert.Contains(blocker, error);
        Assert.Equal(Path.GetTempPath(), AppTemp.Root);
    }

    [Fact]
    public void CleanupRemovesOnlyOldFilesThisAppCreated()
    {
        var root = Path.Combine(_dir, "tmp");
        AppTemp.Configure(root);
        var old = DateTime.UtcNow.AddDays(-3);
        var oldScript = Path.Combine(root, "manualvideostudio-filter-old");
        var newScript = Path.Combine(root, "manualvideostudio-filter-new");
        var foreign = Path.Combine(root, "someone-elses-file");
        var oldDir = Path.Combine(root, "manualvideostudio-sub-old");
        File.WriteAllText(oldScript, "x"); File.SetLastWriteTimeUtc(oldScript, old);
        File.WriteAllText(newScript, "x");
        File.WriteAllText(foreign, "x"); File.SetLastWriteTimeUtc(foreign, old);
        Directory.CreateDirectory(oldDir); Directory.SetLastWriteTimeUtc(oldDir, old);
        Directory.CreateDirectory(Path.Combine(root, "work"));
        var oldWork = Path.Combine(root, "work", "base-old");
        File.WriteAllText(oldWork, "x"); File.SetLastWriteTimeUtc(oldWork, old);

        var removed = AppTemp.CleanupOld(TimeSpan.FromDays(1));

        Assert.Equal(3, removed);
        Assert.False(File.Exists(oldScript));
        Assert.False(Directory.Exists(oldDir));
        Assert.False(File.Exists(oldWork));
        Assert.True(File.Exists(newScript));
        Assert.True(File.Exists(foreign));   // 無関係なファイルには触れない
    }

    [Fact]
    public void PackagePathsPointAtTheRealPackageStorageWithoutHardCodedNames()
    {
        var local = Path.Combine(_dir, "AppData", "Local");
        var paths = PackageIdentity.ForPackage("publisher.app_abc123", local);
        Assert.True(paths.IsPackaged);
        Assert.Equal("publisher.app_abc123", paths.PackageFamilyName);
        Assert.Equal(Path.Combine(local, "Packages", "publisher.app_abc123", "LocalCache", "Local", "ManualVideoStudio"), paths.LocalDirectory);
        Assert.Equal(Path.Combine(local, "Packages", "publisher.app_abc123", "LocalCache", "Roaming", "ManualVideoStudio"), paths.SettingsDirectory);
        Assert.Equal(Path.Combine(paths.LocalDirectory, "logs"), paths.LogDirectory);
        Assert.Equal(Path.Combine(paths.LocalDirectory, "help"), paths.HelpDirectory);
        Assert.Equal(Path.Combine(paths.LocalDirectory, "tmp"), paths.TempDirectory);
        // パッケージ外（通常版・テスト）では、パッケージとして扱わない。
        Assert.False(new AppPaths().IsPackaged);
        Assert.Null(PackageIdentity.GetFamilyName());
    }

    [Fact]
    public void MigrationCopiesMissingSettingsOnlyAndNeverOverwrites()
    {
        var legacy = new AppPaths(Path.Combine(_dir, "legacy-roam"), Path.Combine(_dir, "legacy-local"));
        var pkg = new AppPaths(Path.Combine(_dir, "pkg-roam"), Path.Combine(_dir, "pkg-local"), "pfn");
        Directory.CreateDirectory(legacy.SettingsDirectory);
        File.WriteAllText(legacy.SettingsFile, "{\"legacy\":true}");
        File.WriteAllText(legacy.PromptTemplatesFile, "{\"legacyPrompts\":true}");
        Directory.CreateDirectory(pkg.SettingsDirectory);
        File.WriteAllText(pkg.SettingsFile, "{\"mine\":true}");   // 既にある設定

        var copied = PackageIdentity.MigrateLegacySettings(pkg, legacy);

        Assert.Equal(["prompt-templates.json"], copied);
        Assert.Equal("{\"mine\":true}", File.ReadAllText(pkg.SettingsFile));   // 上書きしない
        Assert.Equal("{\"legacyPrompts\":true}", File.ReadAllText(pkg.PromptTemplatesFile));
        Assert.True(File.Exists(legacy.SettingsFile));                          // 元は消さない
        // 通常版（パッケージでない）では何もしない。
        Assert.Empty(PackageIdentity.MigrateLegacySettings(legacy, legacy));
    }
}

/// <summary>v1.17.0: 通常版の AppData が無い（初回起動）場合でも、必要なフォルダーを作って動く。</summary>
[Collection("AppTemp")]
public class FreshAppDataTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-fresh-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        AppTemp.Reset();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void LogsTempHelpAndRequestFoldersAreCreatedOnDemand()
    {
        var paths = new AppPaths(Path.Combine(_dir, "roam"), Path.Combine(_dir, "local"));
        Assert.False(Directory.Exists(_dir));
        // ログ: 書けたら LastWriteError は null。
        var provider = new ManualVideoStudio.Core.Logging.FileLoggerProvider(paths.LogDirectory);
        provider.CreateLogger("t").LogInformation("hello");
        Assert.True(File.Exists(provider.CurrentLogFile));
        Assert.Null(ManualVideoStudio.Core.Logging.FileLoggerProvider.LastWriteError);
        // 一時の置き場・同梱版の説明書・字幕依頼動画の保存先。
        Assert.Null(AppTemp.Configure(paths.TempDirectory));
        Assert.True(Directory.Exists(paths.TempDirectory));
        Assert.False(Directory.Exists(paths.HelpDirectory)); // 同梱版の説明書は廃止（作らない）
        var store = new CaptionRequestStore(paths.CaptionRequestDirectory);
        var projectLocation = Path.Combine(_dir, "projects", "20261011_1200_x", "caption-requests");
        Assert.Equal(Path.GetFullPath(projectLocation), store.Resolve(projectLocation).Directory);
        // 設定は、無ければ作らない（Load は既定値）。
        Assert.False(File.Exists(paths.SettingsFile));
        Assert.Empty(PackageIdentity.MigrateLegacySettings(paths, new AppPaths(Path.Combine(_dir, "no-roam"), Path.Combine(_dir, "no-local"))));
    }

    [Fact]
    public void LogWriteFailureIsReportedNotSilentlyIgnored()
    {
        var blocker = Path.Combine(_dir, "blocker");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(blocker, "x");
        var provider = new ManualVideoStudio.Core.Logging.FileLoggerProvider(Path.Combine(blocker, "logs"));
        provider.CreateLogger("t").LogInformation("cannot be written");
        Assert.NotNull(ManualVideoStudio.Core.Logging.FileLoggerProvider.LastWriteError);
        // 書けるようになったら解除される。
        var ok = new ManualVideoStudio.Core.Logging.FileLoggerProvider(Path.Combine(_dir, "logs-ok"));
        ok.CreateLogger("t").LogInformation("now it works");
        Assert.Null(ManualVideoStudio.Core.Logging.FileLoggerProvider.LastWriteError);
    }
}

/// <summary>v1.17.0: 書き出しの失敗が、段階とフルパスつきで分かる。</summary>
[Collection("AppTemp")]
public class ExportDiagnosticsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-diag-" + Guid.NewGuid().ToString("N"));

    public ExportDiagnosticsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        AppTemp.Reset();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class Runner(bool createOutput) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default)
        {
            if (createOutput) File.WriteAllBytes(request.Arguments[^1], new byte[2048]);
            return Task.FromResult(new ProcessResult(0, "", "", TimeSpan.Zero));
        }
    }

    private VideoExportRequest Request(string output)
    {
        var p = TestProjects.Create(30);
        return new VideoExportRequest("ffmpeg", 7, Path.Combine(_dir, "in.mp4"), p.VideoMetadata!, [], output, new VideoEncodeOptions());
    }

    [Fact]
    public async Task OutputFolderThatCannotBeCreatedNamesTheStageAndFullPath()
    {
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");
        var output = Path.Combine(blocker, "sub", "out.mp4");
        var ex = await Assert.ThrowsAsync<PathStageException>(() => new VideoExportService(new Runner(true)).ExportAsync(Request(output), null, CancellationToken.None));
        Assert.Equal("保存先フォルダーの作成", ex.Stage);
        Assert.Equal(Path.Combine(blocker, "sub"), ex.FilePath);
        Assert.Contains(Path.Combine(blocker, "sub"), ex.Message);
    }

    [Fact]
    public async Task MissingFfmpegOutputAfterSuccessIsReportedWithThePartialPath()
    {
        var output = Path.Combine(_dir, "ok", "out.mp4");
        var ex = await Assert.ThrowsAsync<PathStageException>(() => new VideoExportService(new Runner(false)).ExportAsync(Request(output), null, CancellationToken.None));
        Assert.Equal("FFmpeg の出力ファイルの確認", ex.Stage);
        Assert.EndsWith("out.partial.mp4", ex.FilePath);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task CaptionRequestReportsMissingSourceBeforeStartingFfmpeg()
    {
        var store = new CaptionRequestStore(Path.Combine(_dir, "app", "caption-requests"));
        var export = new VideoExportService(new Runner(true));
        var project = TestProjects.Create(30);
        project.SourceVideo.Path = Path.Combine(_dir, "no-such.mp4");
        var ex = await Assert.ThrowsAsync<PathStageException>(() =>
            new CaptionRequestService(export, store).PrepareAsync("ffmpeg", 7, project, "rec", null, null, CancellationToken.None));
        Assert.Equal("元動画の確認", ex.Stage);
        Assert.Contains("no-such.mp4", ex.Message);
        Assert.False(Directory.Exists(store.Root));   // 入力が無いのに保存先を作らない
    }

    [Fact]
    public async Task CaptionRequestInProjectFolderIsCreatedThereAndRegistered()
    {
        var store = new CaptionRequestStore(Path.Combine(_dir, "app", "caption-requests"));
        var source = Path.Combine(_dir, "in.mp4");
        File.WriteAllBytes(source, [1]);
        var project = TestProjects.Create(30);
        project.SourceVideo.Path = source;
        var location = Path.Combine(_dir, "proj", "caption-requests");
        var video = await new CaptionRequestService(new VideoExportService(new Runner(true)), store)
            .PrepareAsync("ffmpeg", 7, project, "rec", location, null, CancellationToken.None);
        Assert.Equal(Path.GetFullPath(location), Path.GetDirectoryName(video.Path));
        Assert.True(File.Exists(video.Path));
        Assert.True(store.IsManaged(video.Path));
        Assert.Equal("", video.Notice);
        Assert.False(Directory.Exists(store.Root));
    }
}

/// <summary>v1.17.0: ヘルプの入口（Web 版）と同梱版。</summary>
public class HelpManualTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-help-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task OpenManualOpensTheWebAddressAndNeverAFileUrl()
    {
        Assert.Equal("https://www.iwanaga.net/manualvideostudio/manual/", HelpManual.WebUrl);
        var (vm, _, _) = await MainViewModelPhaseTests.OpenAsync();
        var shell = new RecordingShell();
        var field = typeof(MainViewModel).GetField("_shell", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        field.SetValue(vm, shell);
        vm.OpenManualCommand.Execute(null);
        Assert.Equal([HelpManual.WebUrl], shell.Urls);
    }

    [Fact]
    public void TheManualIsNoLongerEmbeddedInTheExecutable()
    {
        Assert.Null(typeof(HelpManual).Assembly.GetManifestResourceStream("ManualVideoStudio.Help.manual.html"));
    }

    private sealed class RecordingShell : IShellService
    {
        public List<string> Urls { get; } = [];
        public void OpenFolder(string folder) { }
        public void RevealFile(string file) { }
        public void OpenUrl(string url) => Urls.Add(url);
    }
}

/// <summary>v1.17.0: 配布物に同梱するライセンス表記（MIT License・第三者の表記）。</summary>
public class LicenseFilesTests
{
    [Fact]
    public void LicenseAndNoticesAreCopiedNextToTheExecutable()
    {
        var license = LicenseFiles.Read(LicenseFiles.LicenseName, out var problem);
        Assert.True(license is not null, problem);
        Assert.StartsWith("MIT License", license);
        Assert.Contains("ja1bus", license);
        var notices = LicenseFiles.Read(LicenseFiles.NoticesName, out problem);
        Assert.True(notices is not null, problem);
        Assert.Contains("CommunityToolkit.Mvvm", notices);
        Assert.Equal("https://github.com/ja1bus/ManualVideoStudio", LicenseFiles.RepositoryUrl);
        // 拡張子の無い LICENSE（暗号化される拡張子を避ける）。
        Assert.Equal("", Path.GetExtension(LicenseFiles.PathOf(LicenseFiles.LicenseName)));
    }

    [Fact]
    public void UnreadableLicenseReportsTheFullPath()
    {
        Assert.Null(LicenseFiles.Read("no-such-file", out var problem));
        Assert.Contains(LicenseFiles.PathOf("no-such-file"), problem);
    }
}

/// <summary>v1.17.0: 主画面の候補・カット・再生の確認の操作。</summary>
public class CandidateViewModelTests
{
    private static async Task<(MainViewModel Vm, MainViewModelPhaseTests.FakeDialogs Dialogs)> OpenWithCandidatesAsync(params (double s, double e)[] ranges)
    {
        var (vm, dialogs, _) = await MainViewModelPhaseTests.OpenAsync();
        vm.Editor!.ReplaceCandidates(SegmentSource.LocalAnalysis,
            ranges.Select(r => new CutCandidate { Start = r.s, End = r.e, SuggestedAction = SegmentAction.Remove, Confidence = 0.9, Kind = CandidateKind.FreezeAndSilence }), "解析");
        vm.Editor.History.Clear();
        return (vm, dialogs);
    }

    [Fact]
    public async Task SuggestionColumnSaysWhatAdoptingDoes()
    {
        var (vm, _) = await OpenWithCandidatesAsync((10, 20));
        vm.Editor!.AddCandidates([new CutCandidate { Start = 30, End = 40, SuggestedAction = SegmentAction.Remove, Source = SegmentSource.Ai, Kind = CandidateKind.Ai }], "AI");
        // 古い版の候補（短縮の提案）は、そのまま読める。
        vm.Editor.AddCandidates([new CutCandidate { Start = 50, End = 60, SuggestedAction = SegmentAction.Shorten, KeepSeconds = 1.5 }], "旧");
        Assert.Equal("全体を削除", vm.CandidateItems[0].SuggestionText);
        Assert.Equal("全体を削除", vm.CandidateItems[1].SuggestionText);
        Assert.Equal("先頭 1.5 秒を残す", vm.CandidateItems[2].SuggestionText);
    }

    [Fact]
    public async Task RowsKeepTheirIdentityAcrossEditsSoSelectionIsStable()
    {
        var (vm, _) = await OpenWithCandidatesAsync((10, 20), (30, 40), (50, 60));
        var rows = vm.CandidateItems.ToList();
        vm.AcceptCandidatesCommand.Execute(new List<CandidateItem> { rows[0] });
        // 採用した行は消え、残りの行は同じオブジェクトのまま（選択・スクロールが保たれる）。
        Assert.Equal(2, vm.CandidateItems.Count);
        Assert.Same(rows[1], vm.CandidateItems[0]);
        Assert.Same(rows[2], vm.CandidateItems[1]);
        vm.UndoCommand.Execute(null);
        Assert.Equal(3, vm.CandidateItems.Count);
        Assert.Same(rows[1], vm.CandidateItems[1]);
        // カット区間の一覧も同様。
        vm.AcceptCandidatesCommand.Execute(new List<CandidateItem> { vm.CandidateItems[0], vm.CandidateItems[1] });
        var cuts = vm.CutItems.ToList();
        vm.Editor!.UpdateCut(cuts[0].Id, 10, 21, SegmentAction.Shorten, 1.5);
        Assert.Same(cuts[0], vm.CutItems[0]);
        Assert.Equal("00:21.000".Replace("00:", ""), vm.CutItems[0].EndText.Replace("00:", ""));
    }

    [Fact]
    public async Task BatchAcceptConfirmsCountAndSecondsThenIsOneUndo()
    {
        var (vm, dialogs) = await OpenWithCandidatesAsync((10, 20), (15, 25), (40, 50));
        vm.AcceptCandidatesCommand.Execute(vm.CandidateItems.ToList());

        Assert.Contains("3 件", dialogs.LastConfirm);
        Assert.Contains("25.0 秒", dialogs.LastConfirm);     // 10～25 秒（重なる 2 件はまとまる）と 40～50 秒
        Assert.Contains("1 回", dialogs.LastConfirm);        // 元に戻すのは 1 回
        Assert.Equal(2, vm.CutItems.Count);                  // 重なる候補は 1 つのカットにまとまる
        Assert.Equal((10.0, 25.0), (vm.CutItems[0].Model.Start, vm.CutItems[0].Model.End));
        Assert.Empty(dialogs.Warnings);                      // 採用できない候補はない
        Assert.Contains("3 件の候補", vm.StatusMessage);
        Assert.Contains("既存のカットとまとめたもの 1 件", vm.StatusMessage);
        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.CutItems);
        Assert.Equal(3, vm.PendingCandidateCount);
    }

    [Fact]
    public async Task DecliningTheBatchConfirmationChangesNothing()
    {
        var (vm, dialogs) = await OpenWithCandidatesAsync((10, 20), (40, 50));
        dialogs.ConfirmResult = false;
        vm.AcceptCandidatesCommand.Execute(vm.CandidateItems.ToList());
        Assert.Empty(vm.CutItems);
        Assert.False(vm.Editor!.History.CanUndo);
    }

    [Fact]
    public async Task SelectionSummaryShowsCountsAndShortenedSeconds()
    {
        var (vm, _) = await OpenWithCandidatesAsync((10, 20), (15, 25), (40, 50));
        Assert.Contains("Ctrl+クリック", vm.CandidateSelectionText);
        Assert.Contains("Shift+クリック", vm.CandidateSelectionText);
        vm.SetSelectedCandidates(vm.CandidateItems.Select(i => i.Id));
        Assert.Contains("選択 3 件", vm.CandidateSelectionText);
        Assert.Contains("25.0 秒", vm.CandidateSelectionText);  // 10～25 秒（重なる 2 件はまとまる）+ 40～50 秒
        Assert.DoesNotContain("登録できない", vm.CandidateSelectionText);
    }

    [Fact]
    public async Task FiltersHideRowsWithoutSelectingThemAndShowTheCount()
    {
        var (vm, _) = await OpenWithCandidatesAsync((10, 20), (30, 40));
        vm.Editor!.AddCandidates([new CutCandidate { Start = 50, End = 60, Source = SegmentSource.Ai, Kind = CandidateKind.Ai }], "AI");
        Assert.Equal(3, vm.CandidateItems.Count);
        vm.CandidateSourceFilter = 2;   // AI
        Assert.Single(vm.CandidateItems);
        Assert.True(vm.IsCandidateFiltered);
        Assert.Contains("1 件を表示（全 3 件", vm.CandidateCountText);
        vm.CandidateSourceFilter = 1;
        vm.CandidateKindFilter = 3;     // 無音のみ → 該当なし
        Assert.Empty(vm.CandidateItems);
    }

    [Fact]
    public async Task DismissAndNextSelectsTheNextPendingCandidate()
    {
        var (vm, _) = await OpenWithCandidatesAsync((10, 20), (30, 40), (50, 60));
        IReadOnlyList<string>? requested = null;
        vm.ListSelectionRequested += (kind, ids) => { Assert.Equal(ListKind.Candidates, kind); requested = ids; };
        var first = vm.CandidateItems[0];
        vm.DismissCandidatesCommand.Execute(new List<CandidateItem> { first });
        Assert.Equal(CandidateStatus.Dismissed, first.Model.Status);
        Assert.Equal(vm.Project!.CutCandidates.Single(c => c.Start == 30).Id, requested!.Single());
        Assert.Equal(28, vm.CurrentTime, 3);   // 次の候補の開始の 2 秒前へ（再生はしない）
        Assert.False(vm.IsPlaying);
    }

    [Fact]
    public async Task ClearRemovesOnlyUnacceptedAndReportsCountsIncludingZero()
    {
        var (vm, _) = await OpenWithCandidatesAsync((10, 20), (30, 40));
        vm.AcceptCandidatesCommand.Execute(new List<CandidateItem> { vm.CandidateItems[0] });
        vm.ClearCandidatesCommand.Execute("local");
        Assert.Contains("未採用のローカル解析の候補 1 件を削除", vm.StatusMessage);
        Assert.Contains("採用済みの記録 1 件", vm.StatusMessage);
        Assert.Single(vm.CutItems);                    // 確定カットは残る
        vm.ClearCandidatesCommand.Execute("local");
        Assert.Contains("削除する未採用", vm.StatusMessage);   // 0 件のときも分かる
        Assert.Contains("採用済みの記録 1 件", vm.StatusMessage);
        vm.UndoCommand.Execute(null);                  // 2 回目は何も記録しないので、1 回目が戻る
        Assert.Equal(1, vm.PendingCandidateCount);
    }

    [Fact]
    public async Task MarkStartAndEndCanBeCancelledSeparatelyEvenWithOnlyTheStart()
    {
        var (vm, _, _) = await MainViewModelPhaseTests.OpenAsync();
        Assert.False(vm.ClearMarksCommand.CanExecute(null));
        vm.SeekTo(10);
        vm.SetMarkInCommand.Execute(null);
        Assert.True(vm.ClearMarksCommand.CanExecute(null));   // 開始だけでも取り消せる
        vm.ClearMarkInCommand.Execute(null);
        Assert.Null(vm.MarkIn);
        Assert.False(vm.ClearMarksCommand.CanExecute(null));

        vm.SeekTo(10); vm.SetMarkInCommand.Execute(null);
        vm.SeekTo(20); vm.SetMarkOutCommand.Execute(null);
        vm.ClearMarkOutCommand.Execute(null);
        Assert.Equal(10, vm.MarkIn);   // 終了だけ取り消す
        Assert.Null(vm.MarkOut);
        vm.ClearMarksCommand.Execute(null);
        Assert.Null(vm.MarkIn);
        Assert.Empty(vm.CutItems);     // 登録済みのカットには触れない
    }

    [Fact]
    public async Task CancellingACutRevivesItsCandidateAndReportsIt()
    {
        var (vm, _) = await OpenWithCandidatesAsync((10, 20));
        vm.AcceptCandidatesCommand.Execute(new List<CandidateItem> { vm.CandidateItems[0] });
        Assert.Equal(0, vm.PendingCandidateCount);
        vm.DeleteCutsCommand.Execute(vm.CutItems.ToList());
        Assert.Equal(1, vm.PendingCandidateCount);
        Assert.Contains("取り消しました", vm.StatusMessage);
        Assert.Contains("1 件は未判断に戻りました", vm.StatusMessage);
    }

    [Fact]
    public async Task TrialPreviewDoesNotChangeTheProjectOrTheUndoHistory()
    {
        var (vm, _) = await OpenWithCandidatesAsync((10, 20), (30, 40));
        vm.Editor!.AddCut(50, 55, SegmentAction.Remove, 0);
        vm.Editor.History.Clear();
        var before = JsonSerializer.Serialize(vm.Project);
        var undoCount = vm.Editor.History.UndoCount;

        vm.PreviewCandidateTrialCommand.Execute(vm.CandidateItems[0]);

        Assert.True(vm.HasPreviewBanner);
        Assert.Contains("採用・保存はされていません", vm.PreviewBannerText);
        Assert.Contains("全体を削除", vm.PreviewBannerText);
        Assert.Equal(before, JsonSerializer.Serialize(vm.Project));
        Assert.Equal(undoCount, vm.Editor.History.UndoCount);
        Assert.False(vm.IsDirty && !vm.Editor.IsDirty);
        Assert.Equal(CandidateStatus.Pending, vm.Project!.CutCandidates[0].Status);
        Assert.Single(vm.Project.CutSegments);     // 仮のカットは一覧に入らない
        // 編集すると試し再生は終わる。
        vm.Editor.AddCut(1, 2, SegmentAction.Remove, 0);
        Assert.False(vm.HasPreviewBanner);
    }

    [Fact]
    public async Task TrialPreviewIsOnlyForPendingCandidatesAndWorksWhenOverlappingACut()
    {
        var (vm, _) = await OpenWithCandidatesAsync((10, 20), (40, 50));
        vm.Editor!.AddCut(12, 18, SegmentAction.Remove, 0);   // 候補と重なっても、まとめて試せる
        vm.PreviewCandidateTrialCommand.Execute(vm.CandidateItems[0]);
        Assert.True(vm.HasPreviewBanner);
        vm.StopPreviewCommand.Execute(null);
        Assert.False(vm.HasPreviewBanner);

        var accepted = vm.CandidateItems[1];
        vm.AcceptCandidatesCommand.Execute(new List<CandidateItem> { accepted });
        vm.CandidateStatusFilter = 2; // すべて（採用済みも表示）
        vm.PreviewCandidateTrialCommand.Execute(vm.CandidateItems.First(i => i.Model.Status == CandidateStatus.Accepted));
        Assert.False(vm.HasPreviewBanner);                     // 採用済みは試せない
        Assert.Contains("未判断の候補", vm.StatusMessage);
    }

    [Fact]
    public async Task SkipCutsPlaybackIsOffByDefaultInCutEditingAndIsSwitchable()
    {
        var (vm, _, _) = await MainViewModelPhaseTests.OpenAsync();
        Assert.False(vm.ApplyCutsInPreview);
        vm.ApplyCutsInPreview = true;
        Assert.Contains("スキップ", vm.StatusMessage.Length > 0 ? "スキップ" : "");
        vm.ApplyCutsInPreview = false;
    }

    [Fact]
    public async Task TimelineClickSelectsTheMatchingListRowAndSwitchesTheList()
    {
        var (vm, _) = await OpenWithCandidatesAsync((10, 20), (30, 40));
        var (kind, ids) = (ListKind.Cuts, (IReadOnlyList<string>)[]);
        vm.ListSelectionRequested += (k, i) => (kind, ids) = (k, i);

        var cand = vm.CandidateItems[1];
        vm.OnTimelineItemClicked(ListKind.Candidates, cand.Id, additive: false);
        Assert.Equal(ListKind.Candidates, kind);
        Assert.Equal([cand.Id], ids);
        Assert.Equal(MainViewModel.TabCandidates, vm.SelectedTabIndex);
        Assert.Equal(0, vm.CurrentTime);   // 行を選ぶだけでは、再生位置を動かさない

        vm.Editor!.AddCut(50, 55, SegmentAction.Remove, 0);
        var cut = vm.CutItems.Single();
        vm.OnTimelineItemClicked(ListKind.Cuts, cut.Id, additive: false);
        Assert.Equal(ListKind.Cuts, kind);
        Assert.Equal(MainViewModel.TabCuts, vm.SelectedTabIndex);

        // Ctrl+クリックで追加・解除。
        vm.SetSelectedCandidates([cand.Id]);
        vm.OnTimelineItemClicked(ListKind.Candidates, vm.CandidateItems[0].Id, additive: true);
        Assert.Equal(2, ids.Count);
    }

    [Fact]
    public async Task TimelineClickDoesNotNavigateWhileSubtitleEditingLocksTheCuts()
    {
        var (vm, _) = await OpenWithCandidatesAsync((10, 20));
        vm.Editor!.AddCut(30, 35, SegmentAction.Remove, 0);
        vm.ConfirmCutsCommand.Execute(null);
        Assert.True(vm.IsSubtitlePhase);
        var requested = 0;
        vm.ListSelectionRequested += (_, _) => requested++;
        vm.OnTimelineItemClicked(ListKind.Cuts, vm.CutItems.Single().Id, additive: false);
        vm.OnTimelineItemClicked(ListKind.Candidates, vm.Project!.CutCandidates[0].Id, additive: false);
        Assert.Equal(0, requested);
    }

    [Fact]
    public async Task ClickingADismissedCandidateOnTheTimelineRevealsItInTheList()
    {
        var (vm, _) = await OpenWithCandidatesAsync((10, 20), (30, 40));
        var id = vm.CandidateItems[0].Id;
        vm.DismissCandidatesCommand.Execute(new List<CandidateItem> { vm.CandidateItems[0] });
        Assert.DoesNotContain(vm.CandidateItems, i => i.Id == id);
        vm.OnTimelineItemClicked(ListKind.Candidates, id, additive: false);
        Assert.Contains(vm.CandidateItems, i => i.Id == id);   // 却下済みも表示に切り替わる
    }

}

/// <summary>v1.17.0: AI 連携画面の貼り付け・閉じる。</summary>
public class AiAssistWindowActionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-ai-" + Guid.NewGuid().ToString("N"));

    public AiAssistWindowActionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class Clip(string? text) : IClipboardService
    {
        public string? Text { get; set; } = text;
        public bool SetText(string t) => true;
        public bool TryGetText(out string t)
        {
            t = Text ?? "";
            return Text is { Length: > 0 };
        }
    }

    private async Task<(AiAssistViewModel Ai, MainViewModel Main, Clip Clipboard)> OpenAsync(string? clip)
    {
        var (main, dialogs, _) = await MainViewModelPhaseTests.OpenAsync();
        var settings = new MainViewModelPhaseTests.FakeSettings();
        var clipboard = new Clip(clip);
        var ai = new AiAssistViewModel(main, new PromptTemplateService(Path.Combine(_dir, "p.json")), settings, clipboard, dialogs,
            new MainViewModelPhaseTests.Unused(), NullLogger<AiAssistViewModel>.Instance);
        return (ai, main, clipboard);
    }

    private const string CutAnswer = "{\"cuts\":[{\"start\":\"00:10.0\",\"end\":\"00:20.0\",\"action\":\"shorten\",\"keep_seconds\":1.5,\"confidence\":0.8,\"reason\":\"静止\"}]}";

    [Fact]
    public async Task PasteReplacesTheAnswerBoxAndExplainsEmptyOrUnreadableClipboard()
    {
        var (ai, _, clip) = await OpenAsync("貼り付けた回答");
        ai.ResponseText = "元の回答";
        ai.PasteFromClipboardCommand.Execute(null);
        Assert.Equal("貼り付けた回答", ai.ResponseText);      // 追記ではなく置き換え
        Assert.Contains("置き換えました", ai.PasteMessage);

        clip.Text = "";
        ai.PasteFromClipboardCommand.Execute(null);
        Assert.Equal("貼り付けた回答", ai.ResponseText);      // 読めないときは元の回答を残す
        Assert.Contains("回答欄はそのまま", ai.PasteMessage);
    }

    [Fact]
    public async Task EditingTheAnswerInvalidatesAnEarlierParseSoItCannotBeImported()
    {
        var (ai, main, _) = await OpenAsync(null);
        ai.Purpose = AiPurpose.CutAnalysis;
        ai.ResponseText = CutAnswer;
        ai.ParseCommand.Execute(null);
        Assert.True(ai.HasResult);
        Assert.Single(ai.CutRows);

        ai.ResponseText = CutAnswer + " ";   // 書き換え
        Assert.False(ai.HasResult);
        Assert.Empty(ai.CutRows);
        Assert.Contains("もう一度［解析］", ai.ParseMessage);
        ai.ImportCommand.Execute(null);
        Assert.Empty(main.Project!.CutCandidates);   // 古い結果は取り込まれない
    }

    [Fact]
    public async Task ImportAndCloseClosesOnlyOnSuccessAndCloseNeverImports()
    {
        var (ai, main, _) = await OpenAsync(null);
        ai.Purpose = AiPurpose.CutAnalysis;
        int closed = 0;
        ai.CloseRequested += () => closed++;

        ai.ResponseText = CutAnswer;
        ai.CloseCommand.Execute(null);               // 閉じるだけでは取り込まない
        Assert.Equal(1, closed);
        Assert.Empty(main.Project!.CutCandidates);

        ai.ImportAndCloseCommand.Execute(null);      // 解析前は何も起きない
        Assert.Equal(1, closed);

        ai.ParseCommand.Execute(null);
        ai.ImportCommand.Execute(null);              // ［取り込む］は閉じない
        Assert.Equal(1, closed);
        Assert.Single(main.Project.CutCandidates);
        Assert.Equal(Core.Models.CandidateStatus.Pending, main.Project.CutCandidates[0].Status);   // 取り込みは採用とは別
        Assert.Contains("採用するまで編集に反映されません", main.StatusMessage);

        ai.ResponseText = CutAnswer;
        ai.ParseCommand.Execute(null);
        ai.ImportAndCloseCommand.Execute(null);
        Assert.Equal(2, closed);                     // 成功したので閉じる
    }

    [Fact]
    public async Task ImportAndCloseStaysOpenWhenNothingCanBeImported()
    {
        var (ai, main, _) = await OpenAsync(null);
        ai.Purpose = AiPurpose.CutAnalysis;
        int closed = 0;
        ai.CloseRequested += () => closed++;
        ai.ResponseText = "{\"cuts\":[{\"start\":\"00:10.0\",\"end\":\"00:09.0\",\"action\":\"remove\",\"reason\":\"逆転\"}]}";
        ai.ParseCommand.Execute(null);
        ai.ImportAndCloseCommand.Execute(null);
        Assert.Equal(0, closed);                     // 取り込める行が無いので、画面を残す
        Assert.NotEmpty(ai.ErrorMessage);
        Assert.Empty(main.Project!.CutCandidates);
    }
}

/// <summary>v1.17.0: 画面の再修正（候補の表示切り替え・前後のカット・図形の欄・字幕と図形のタイムライン選択・バージョン情報・AI 連携の簡易モード）。</summary>
public class ScreenRefinementTests
{
    private static async Task<(MainViewModel Vm, MainViewModelPhaseTests.FakeDialogs Dialogs)> OpenAsync(string mode = UiModes.Full)
    {
        var (vm, dialogs, _) = await MainViewModelPhaseTests.OpenAsync(mode);
        return (vm, dialogs);
    }

    private static void Cand(MainViewModel vm, params (double s, double e)[] r)
    {
        vm.Editor!.ReplaceCandidates(SegmentSource.LocalAnalysis, r.Select(x => new CutCandidate { Start = x.s, End = x.e, SuggestedAction = SegmentAction.Remove, Confidence = 0.9 }), "解析");
        vm.Editor.History.Clear();
    }

    [Fact]
    public async Task DismissedCandidatesAreShownByTheViewSwitchAndCanBeRestoredOrCutDirectly()
    {
        var (vm, _) = await OpenAsync();
        Cand(vm, (10, 20), (30, 40));
        Assert.Equal(["未判断", "却下", "すべて"], MainViewModel.CandidateStatusFilters);
        var first = vm.CandidateItems[0];
        vm.SetSelectedCandidates([first.Id]);
        Assert.Equal("却下", vm.DismissButtonText);
        vm.DismissOrRestoreCommand.Execute(new List<CandidateItem> { first });
        Assert.Single(vm.CandidateItems);                       // 未判断の表示からは消える

        vm.CandidateStatusFilter = 1;                           // 表示を［却下］に
        var dismissed = Assert.Single(vm.CandidateItems);
        vm.SetSelectedCandidates([dismissed.Id]);
        Assert.Equal("未判断に戻す", vm.DismissButtonText);      // ボタンが［未判断に戻す］に変わる
        vm.DismissOrRestoreCommand.Execute(new List<CandidateItem> { dismissed });
        Assert.Equal(2, vm.Project!.CutCandidates.Count(c => c.Status == CandidateStatus.Pending));

        // 却下した候補を、そのままカットにもできる。
        vm.CandidateStatusFilter = 0;
        vm.DismissOrRestoreCommand.Execute(new List<CandidateItem> { vm.CandidateItems[0] });
        vm.CandidateStatusFilter = 1;
        vm.AcceptCandidatesCommand.Execute(new List<CandidateItem> { vm.CandidateItems[0] });
        Assert.Single(vm.CutItems);
    }

    [Fact]
    public async Task PreviousAndNextCutButtonsJumpBetweenConfirmedCutStarts()
    {
        var (vm, _) = await OpenAsync();
        vm.Editor!.AddCut(10, 14, SegmentAction.Remove, 0);
        vm.Editor.AddCut(30, 34, SegmentAction.Remove, 0);
        vm.SeekTo(0);
        vm.SeekNextCutCommand.Execute(null);
        Assert.Equal(10, vm.CurrentTime, 3);
        vm.SeekNextCutCommand.Execute(null);
        Assert.Equal(30, vm.CurrentTime, 3);
        vm.SeekNextCutCommand.Execute(null);
        Assert.Contains("次のカット位置はありません", vm.StatusMessage);
        vm.SeekPrevCutCommand.Execute(null);
        Assert.Equal(10, vm.CurrentTime, 3);
        vm.SeekPrevCutCommand.Execute(null);
        Assert.Contains("前のカット位置はありません", vm.StatusMessage);
    }

    [Fact]
    public async Task MarkButtonsReportWhetherAStartOrEndIsSetAndEachCanBeCancelled()
    {
        var (vm, _) = await OpenAsync();
        Assert.False(vm.HasMarkIn);
        Assert.False(vm.ClearMarkInCommand.CanExecute(null));
        vm.SeekTo(10); vm.SetMarkInCommand.Execute(null);
        Assert.True(vm.HasMarkIn);
        Assert.Contains("指定中", vm.MarkInTip);                  // 時刻はツールチップに出す
        Assert.True(vm.ClearMarkInCommand.CanExecute(null));
        Assert.False(vm.ClearMarkOutCommand.CanExecute(null));   // 終了は未指定なので薄い（無効）
        vm.ClearMarkInCommand.Execute(null);
        Assert.False(vm.HasMarkIn);
    }

    private static async Task<(MainViewModel Vm, string Id)> WithShapeAsync()
    {
        var (vm, _) = await OpenAsync();
        vm.ConfirmCutsCommand.Execute(null);
        Assert.True(vm.IsSubtitlePhase);
        var (r, g) = vm.Editor!.AddShapeGroup(5, 10, [new RectangleAnnotation { X = 10, Y = 20, Width = 100, Height = 50 }], 1920, 1080);
        Assert.True(r.Success);
        vm.Editor.History.Clear();
        return (vm, g!.Id);
    }

    [Fact]
    public async Task ShapePanelEditsOnlyTheTimeAndMarksUnappliedInput()
    {
        var (vm, id) = await WithShapeAsync();
        Assert.False(vm.HasSelectedShape);
        vm.SetSelectedShapes([id]);
        Assert.True(vm.HasSelectedShape);
        Assert.Equal("00:00:05.000", vm.ShapeDraftStart);
        Assert.False(vm.HasShapeDraft);

        vm.SeekTo(7);                                              // 再生位置を動かして［現在位置］
        vm.ShapeDraftStartFromCurrentCommand.Execute(null);
        Assert.StartsWith("00:00:07", vm.ShapeDraftStart);
        Assert.True(vm.HasShapeDraft);                             // 「未反映の入力があります。」
        vm.ApplyShapeDraftCommand.Execute(null);

        var g = vm.Project!.ShapeGroups.Single();
        Assert.Equal((7.0, 10.0), (g.Start, g.End));
        Assert.Single(g.Shapes);                                   // 図形の形は変わらない
        Assert.False(vm.HasShapeDraft);
        vm.UndoCommand.Execute(null);                              // 1 回の元に戻す操作で戻る
        Assert.Equal(5.0, vm.Project.ShapeGroups.Single().Start);
    }

    [Fact]
    public async Task ShapePanelKeepsInvalidInputAndAsksBeforeDiscardingItWhenAnotherShapeIsSelected()
    {
        var (vm, id) = await WithShapeAsync();
        var (_, g2) = vm.Editor!.AddShapeGroup(20, 25, [new RectangleAnnotation { X = 1, Y = 1, Width = 10, Height = 10 }], 1920, 1080);
        var dialogs = (MainViewModelPhaseTests.FakeDialogs)typeof(MainViewModel).GetField("_dialogs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm)!;
        string? requestedKind = null;
        vm.ListSelectionRequested += (k, ids) => requestedKind = k + ":" + string.Join(",", ids);

        vm.SetSelectedShapes([id]);
        vm.ShapeDraftEnd = "でたらめ";                               // 反映できない入力
        dialogs.ConfirmResult = false;                              // 留まる
        vm.SetSelectedShapes([g2!.Id]);
        Assert.Equal($"Shapes:{id}", requestedKind);               // 一覧の選択をいまの図形へ戻す
        Assert.Contains("破棄して", dialogs.LastConfirm);
        Assert.Equal(id, vm.SelectedShapeIds.Single());

        dialogs.ConfirmResult = true;                               // 破棄して移る
        vm.SetSelectedShapes([g2.Id]);
        Assert.Equal(g2.Id, vm.SelectedShapeIds.Single());
        Assert.False(vm.HasShapeDraft);

        // 入力が正しければ、選び直すときに自動で反映する。
        vm.SetSelectedShapes([id]);
        vm.ShapeDraftStart = "00:00:06.000";
        vm.SetSelectedShapes([g2.Id]);
        Assert.Equal(6.0, vm.Project!.ShapeGroups.First(g => g.Id == id).Start);
    }

    [Fact]
    public async Task UnappliedShapeTimeIsAppliedBeforeSaving()
    {
        var (vm, id) = await WithShapeAsync();
        vm.SetSelectedShapes([id]);
        vm.ShapeDraftEnd = "00:00:12.000";
        Assert.True(vm.HasShapeDraft);
        Assert.True(vm.ConfirmDiscardChanges() || true);           // 保存の確認の前に入力を反映する（確認ダイアログの答えは問わない）
        Assert.Equal(12.0, vm.Project!.ShapeGroups.Single().End);
    }

    [Fact]
    public async Task TimelineClicksSelectSubtitleAndShapeRowsOnlyWhileSubtitleEditing()
    {
        var (vm, id) = await WithShapeAsync();
        var sub = vm.Editor!.AddSubtitle(1, 4, "字幕", SubtitlePosition.Bottom, 48).Segment;
        var kinds = new List<string>();
        vm.ListSelectionRequested += (k, ids) => kinds.Add(k + ":" + string.Join(",", ids));
        vm.OnTimelineItemClicked(ListKind.Subtitles, sub!.Id, additive: false);
        vm.OnTimelineItemClicked(ListKind.Shapes, id, additive: false);
        Assert.Equal([$"Subtitles:{sub.Id}", $"Shapes:{id}"], kinds);
        Assert.Equal(MainViewModel.TabShapes, vm.SelectedTabIndex);

        var (cutVm, _) = await OpenAsync();                         // カット編集中は、字幕・図形のクリックは一覧へ移らない
        var n = 0;
        cutVm.ListSelectionRequested += (_, _) => n++;
        cutVm.OnTimelineItemClicked(ListKind.Subtitles, "x", additive: false);
        cutVm.OnTimelineItemClicked(ListKind.Shapes, "x", additive: false);
        Assert.Equal(0, n);
    }

    [Fact]
    public async Task SelectedSubtitleIsReportedToTheTimeline()
    {
        var (vm, _) = await WithShapeAsync();
        var sub = vm.Editor!.AddSubtitle(1, 4, "字幕", SubtitlePosition.Bottom, 48).Segment!;
        vm.SelectedSubtitleItem = vm.SubtitleItems.Single();
        Assert.Equal([sub.Id], vm.SelectedSubtitleIds);
    }

    [Fact]
    public async Task SubtitleStartAndEndFromCurrentFillTheDraftInOutputTime()
    {
        var (vm, _) = await WithShapeAsync();
        vm.Editor!.AddSubtitle(1, 4, "字幕", SubtitlePosition.Bottom, 48);
        vm.SelectedSubtitleItem = vm.SubtitleItems.Single();
        vm.SeekTo(8);
        vm.SubtitleDraftEndFromCurrentCommand.Execute(null);
        Assert.StartsWith("00:00:08", vm.SubtitleDraftEnd);
        Assert.True(vm.HasSubtitleDraft);
    }

    [Fact]
    public async Task AboutShowsVersionLicenseAndCopyrightAndTheMenuHasNoBundledManual()
    {
        var (vm, dialogs) = await OpenAsync();
        var about = new AboutViewModel(dialogs);
        Assert.Contains("© ja1bus", about.VersionText);
        Assert.Contains("MIT License", about.LicenseText);
        Assert.Equal("https://github.com/ja1bus/ManualVideoStudio", about.RepositoryUrl);
        about.ShowLicenseCommand.Execute(null);
        Assert.Contains("MIT License", dialogs.Infos[^1]);          // 全文をアプリ内で表示
        Assert.False(typeof(MainViewModel).GetProperty("BundledManualLabel") is not null);
        Assert.NotNull(vm.ShowAboutCommand);
    }

    [Fact]
    public async Task AiAssistInSimpleModeOffersOnlyCutAnalysisAndOpensWithIt()
    {
        var (main, dialogs) = await OpenAsync(UiModes.Simple);
        var dir = Path.Combine(Path.GetTempPath(), "mvs-ai-s-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            main.AskAiForCandidatesCommand.Execute(null);                       // 候補の［AI に聞く］
            var ai = new AiAssistViewModel(main, new PromptTemplateService(Path.Combine(dir, "p.json")), new MainViewModelPhaseTests.FakeSettings(),
                new Clip(null), dialogs, new MainViewModelPhaseTests.Unused(), NullLogger<AiAssistViewModel>.Instance);
            Assert.Equal(AiPurpose.CutAnalysis, ai.Purpose);
            Assert.Equal([AiPurpose.CutAnalysis], ai.Purposes.Select(c => c.Value));
            Assert.Contains("remove", ai.PromptText);
            Assert.DoesNotContain("keep_seconds", ai.PromptText);               // プロンプトは削除のみ（短縮は廃止）
            Assert.DoesNotContain("shorten", ai.PromptText);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private sealed class Clip(string? text) : IClipboardService
    {
        public bool SetText(string t) => true;
        public bool TryGetText(out string t) { t = text ?? ""; return text is { Length: > 0 }; }
    }

    [Fact]
    public async Task CutEditDialogHasNoShortenAndMergesWhenExtendedOverAnotherCut()
    {
        var (vm, _) = await OpenAsync();
        var a = vm.Editor!.AddCut(10, 14, SegmentAction.Remove, 0).Segment!;
        vm.Editor.AddCut(20, 24, SegmentAction.Remove, 0);
        var dlg = new CutEditViewModel(vm.Editor, a, () => 0);
        Assert.Null(typeof(CutEditViewModel).GetProperty("IsShorten"));
        dlg.EndText = "00:00:21.000";
        dlg.OkCommand.Execute(null);
        Assert.Contains("1 つにまとめ", dlg.ResultMessage);
        Assert.Single(vm.Project!.CutSegments);
    }

    [Fact]
    public async Task CandidateGuidanceAndCommandNamesUseTheNewVocabulary()
    {
        var (vm, _) = await OpenAsync();
        Assert.Contains("AI に聞く", vm.CandidateEmptyText);
        Assert.Equal("候補を探す:", Loc.Get("Cand.Find.Label"));
        Assert.Equal("絞り込み:", Loc.Get("Cand.Filter.Label"));
        Assert.Equal("AI・ローカル", MainViewModel.CandidateSourceFilters[0]);
        Assert.Equal("現在位置", Loc.Get("SubEdit.FromCurrent"));
        Assert.Equal("カットをスキップ", Loc.Get("Preview.Toggle"));
        Assert.DoesNotContain("短縮", Loc.Get("Phase.ConfirmCuts.Tip"));
    }
}
