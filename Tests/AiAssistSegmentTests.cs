using System.IO;
using ManualVideoStudio.Core.Ai;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Services;
using ManualVideoStudio.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Tests;

/// <summary>AI 連携の画面の区間表・区間方式の取り込み・依頼用動画（v1.21。設計 4.1・4.2・4.4）。</summary>
public class AiAssistSegmentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-aiseg-" + Guid.NewGuid().ToString("N"));

    public AiAssistSegmentTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class Clip : IClipboardService
    {
        public string? Copied { get; private set; }
        public bool SetText(string t) { Copied = t; return true; }
        public bool TryGetText(out string t) { t = ""; return false; }
    }

    private sealed class NoProcess : IProcessRunner
    {
        public int Calls { get; private set; }
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new NotSupportedException();
        }
    }

    private sealed record Opened(AiAssistViewModel Ai, MainViewModel Main, Clip Clipboard, MainViewModelPhaseTests.FakeWindows Windows,
        CutRequestSession Session, CaptionRequestStore Store, NoProcess Runner)
    {
        public int AnalysisRuns { get; set; }
    }

    /// <summary>60 秒の動画（画面変化・候補なし）を開いて、AI 連携の画面を「不要区間の解析」にする。簡易解析は差し替える。</summary>
    private async Task<Opened> OpenAsync(Action<ProjectDocument>? onAnalysis = null, Action<ProjectDocument>? before = null)
    {
        var (main, dialogs, windows) = await MainViewModelPhaseTests.OpenAsync();
        before?.Invoke(main.Project!);
        var clip = new Clip();
        var store = new CaptionRequestStore(_dir);
        var runner = new NoProcess();
        var session = new CutRequestSession();
        var ai = new AiAssistViewModel(main, new PromptTemplateService(Path.Combine(_dir, "p.json")), new MainViewModelPhaseTests.FakeSettings(),
            clip, dialogs, new MainViewModelPhaseTests.Unused(), NullLogger<AiAssistViewModel>.Instance,
            new AiRequestVideoService(runner, store), store, windows, session);
        var opened = new Opened(ai, main, clip, windows, session, store, runner);
        ai.RunLocalAnalysis = () =>
        {
            opened.AnalysisRuns++;
            onAnalysis?.Invoke(main.Project!);
            return Task.CompletedTask;
        };
        ai.Purpose = AiPurpose.CutAnalysis;
        return opened;
    }

    private const string SegmentAnswer = "{\"steps\":[],\"segments\":[{\"id\":2,\"category\":\"mistake\",\"confidence\":0.8,\"reason\":\"やり直し\"}]}";

    [Fact]
    public async Task PromptShowsTheSegmentTableAndCount()
    {
        var o = await OpenAsync();
        // 画面変化の無い 60 秒は 20 秒ずつの 3 区間。
        Assert.Equal(3, o.Ai.SegmentTable!.Count);
        Assert.Contains("1 | 00:00:00〜00:00:20 | 20秒 | -", o.Ai.PromptText);
        Assert.Contains("3 区間", o.Ai.SegmentInfo);
        Assert.True(o.Ai.HasSegmentInfo);
        // 簡易解析をまだ実行していないことを案内する。
        Assert.Contains("簡易解析", o.Ai.PromptWarning);
        Assert.True(o.Ai.IsCutAnalysisMode);

        o.Ai.Purpose = AiPurpose.CaptureCaption;
        Assert.Null(o.Ai.SegmentTable);
        Assert.False(o.Ai.HasSegmentInfo);
    }

    [Fact]
    public async Task CopyRunsAnalysisFirstWhenSceneChangesAreMissingAndSavesTheTable()
    {
        var o = await OpenAsync(onAnalysis: p => p.SceneChanges.AddRange([25]));
        await o.Ai.CopyPromptCommand.ExecuteAsync(null);

        Assert.Equal(1, o.AnalysisRuns);
        Assert.Contains("簡易解析を実行し", o.Ai.CopyMessage);
        Assert.Contains("コピーしました", o.Ai.CopyMessage);
        var entry = Assert.Single(o.Main.Project!.AiHistory);
        // 解析後の区間表（画面変化 25 秒。各区間を 20 秒以下に等分して 4 区間）が履歴に残り、コピーした文にも入っている。
        Assert.Equal([(1, 0.0, 12.5), (2, 12.5, 25.0), (3, 25.0, 42.5), (4, 42.5, 60.0)], entry.Segments.Select(s => (s.Id, s.Start, s.End)));
        Assert.Contains("3 | 00:00:25〜00:00:43 | 18秒", o.Clipboard.Copied);
        Assert.DoesNotContain("簡易解析", o.Ai.PromptWarning);

        // 2 回目のコピーでは解析しない。
        await o.Ai.CopyPromptCommand.ExecuteAsync(null);
        Assert.Equal(1, o.AnalysisRuns);
    }

    [Fact]
    public async Task AnalysisIsNotRunWhenSceneChangesExist()
    {
        var o = await OpenAsync(before: p => p.SceneChanges.AddRange([20, 40]));
        Assert.DoesNotContain("簡易解析", o.Ai.PromptWarning);
        await o.Ai.CopyPromptCommand.ExecuteAsync(null);
        await o.Ai.GeneratePromptCommand.ExecuteAsync(null);
        Assert.Equal(0, o.AnalysisRuns);
        Assert.Equal(3, o.Main.Project!.AiHistory.Single().Segments.Count);
    }

    [Fact]
    public async Task AnalysisIsNotRunWhenItWasRunBeforeWithoutSceneChanges()
    {
        // ローカル解析の候補があれば、画面変化が 0 件でも解析は済んでいる。
        var o = await OpenAsync(before: p => p.CutCandidates.Add(new CutCandidate { Start = 5, End = 9, Source = SegmentSource.LocalAnalysis }));
        await o.Ai.CopyPromptCommand.ExecuteAsync(null);
        Assert.Equal(0, o.AnalysisRuns);
    }

    [Fact]
    public async Task CancelledAnalysisContinuesWithoutSceneChangesAndIsNotRepeated()
    {
        var o = await OpenAsync(); // 解析しても画面変化は増えない（キャンセル・失敗と同じ）
        await o.Ai.CopyPromptCommand.ExecuteAsync(null);
        Assert.Equal(1, o.AnalysisRuns);
        Assert.Contains("画面変化なしで区間表を作りました", o.Ai.CopyMessage);
        Assert.Equal(3, o.Main.Project!.AiHistory.Single().Segments.Count);

        await o.Ai.CopyPromptCommand.ExecuteAsync(null);
        Assert.Equal(1, o.AnalysisRuns);
    }

    [Fact]
    public async Task SegmentAnswerUsesTheTableSavedAtCopyTime()
    {
        var o = await OpenAsync(before: p => p.SceneChanges.AddRange([20, 40]));
        await o.Ai.CopyPromptCommand.ExecuteAsync(null);
        // コピーの後に画面変化が変わっても（今の表なら区間 2 = 10〜40 秒）、取り込みはコピーした時点の表（区間 2 = 20〜40 秒）を使う。
        o.Main.Project!.SceneChanges.Clear();
        o.Main.Project.SceneChanges.AddRange([10, 40]);

        o.Ai.ResponseText = SegmentAnswer;
        o.Ai.ParseCommand.Execute(null);
        Assert.True(o.Ai.HasResult, o.Ai.ErrorMessage);
        var row = Assert.Single(o.Ai.CutRows);
        Assert.Equal((20.0, 40.0), (row.Row.Start, row.Row.End));
        Assert.Equal("2", row.SegmentIdsText);
        Assert.Equal("やり直し", row.CategoryText);
        Assert.Contains("［やり直し］", row.Reason);
        Assert.True(row.Include);

        o.Ai.ImportCommand.Execute(null);
        var c = Assert.Single(o.Main.Project.CutCandidates);
        Assert.Equal((20.0, 40.0, SegmentSource.Ai), (c.Start, c.End, c.Source));
    }

    [Fact]
    public async Task ParsingWithoutCopyingUsesTheDisplayedTable()
    {
        var o = await OpenAsync(before: p => p.SceneChanges.AddRange([20, 40]));
        o.Ai.ResponseText = SegmentAnswer;
        o.Ai.ParseCommand.Execute(null);
        Assert.True(o.Ai.HasResult, o.Ai.ErrorMessage);
        Assert.Equal((20.0, 40.0), (o.Ai.CutRows.Single().Row.Start, o.Ai.CutRows.Single().Row.End));
        var entry = Assert.Single(o.Main.Project!.AiHistory);
        Assert.Equal(3, entry.Segments.Count);
    }

    [Fact]
    public async Task SegmentAnswerForAHistoryEntryWithoutTableIsAnError()
    {
        var o = await OpenAsync();
        var old = new AiHistoryEntry { Purpose = AiPurpose.CutAnalysis, Prompt = "古いプロンプト", Response = "", PromptCreatedAt = DateTimeOffset.Now };
        o.Main.Project!.AiHistory.Add(old);
        o.Ai.SelectedHistory = old;
        o.Ai.RestoreHistoryCommand.Execute(null);

        o.Ai.ResponseText = SegmentAnswer;
        o.Ai.ParseCommand.Execute(null);
        Assert.False(o.Ai.HasResult);
        Assert.Contains("区間表が無いプロンプトの回答です。もう一度プロンプトをコピーしてから回答を貼り付けてください。", o.Ai.ErrorMessage);
        Assert.Empty(o.Ai.CutRows);
        Assert.False(old.ParseSucceeded);
    }

    [Fact]
    public async Task TimeModeAnswerStillImportsAndSnapsToTheSavedTable()
    {
        var o = await OpenAsync(before: p => p.SceneChanges.AddRange([30]));
        await o.Ai.CopyPromptCommand.ExecuteAsync(null);
        o.Ai.ResponseText = "{\"cuts\":[{\"start\":\"00:12.0\",\"end\":\"00:29.4\",\"action\":\"remove\",\"confidence\":0.8,\"reason\":\"静止\"}]}";
        o.Ai.ParseCommand.Execute(null);
        Assert.True(o.Ai.HasResult, o.Ai.ErrorMessage);
        var row = Assert.Single(o.Ai.CutRows);
        Assert.True(row.IsValid);
        Assert.Equal((12.0, 30.0), (row.Row.Start, row.Row.End)); // 終了は 1 秒以内の境目 30 秒へ寄せる
        Assert.Equal("", row.SegmentIdsText);
        Assert.Equal("", row.CategoryText);
        o.Ai.ImportCommand.Execute(null);
        Assert.Single(o.Main.Project!.CutCandidates);
    }

    [Fact]
    public async Task AlreadyCutSegmentsAreShownButNotImportable()
    {
        var o = await OpenAsync(before: p =>
        {
            p.SceneChanges.AddRange([30]);
            p.CutSegments.Add(new CutSegment { Start = 0, End = 30 });
        });
        o.Ai.ResponseText = "{\"segments\":[{\"id\":1,\"category\":\"wait\",\"confidence\":0.9,\"reason\":\"待ち\"},{\"id\":3,\"category\":\"mistake\",\"confidence\":0.6,\"reason\":\"失敗\"}]}";
        // 確定カット 0〜30 秒は 15 秒ずつの区間 1・2（カット済み）、区間 3 は 30〜45 秒。
        o.Ai.ParseCommand.Execute(null);
        Assert.Equal(2, o.Ai.CutRows.Count);
        var excluded = o.Ai.CutRows.Single(r => r.IsExcluded);
        Assert.False(excluded.Include);
        Assert.False(excluded.IsValid);
        Assert.Contains("カット済み", excluded.Note);
        excluded.Include = true; // 取り込まない行は選べない
        Assert.False(excluded.Include);
    }

    [Fact]
    public async Task UpToDateRequestVideoIsReusedWithoutRunningFFmpeg()
    {
        var o = await OpenAsync(before: p => p.SceneChanges.AddRange([30]));
        var path = Path.Combine(_dir, "cut-request_test.mp4");
        File.WriteAllBytes(path, [1, 2, 3]);
        var segments = o.Ai.SegmentTable!.ToHistory();
        o.Session.Video = new CaptionRequestVideo(path, AiRequestVideoService.Fingerprint(o.Main.Project!, segments), 60, DateTimeOffset.Now);

        await o.Ai.PrepareRequestVideoCommand.ExecuteAsync(null);
        Assert.Equal(0, o.Runner.Calls);
        Assert.Contains("そのまま使えます", o.Ai.RequestVideoMessage);
        Assert.True(o.Ai.HasRequestVideo);
        Assert.Equal(path, o.Ai.RequestVideoPath);
        Assert.Contains("準備しました", o.Ai.RequestVideoStatus);

        // 区間表が変わると、番号が合わないことを示す。
        o.Main.Project!.SceneChanges.Add(50);
        await o.Ai.GeneratePromptCommand.ExecuteAsync(null);
        Assert.Contains("合いません", o.Ai.RequestVideoStatus);
    }

    [Fact]
    public async Task ImportAsksToDeleteTheRequestVideo()
    {
        var o = await OpenAsync(before: p => p.SceneChanges.AddRange([20, 40]));
        var path = Path.Combine(_dir, "cut-request_delete.mp4");
        File.WriteAllBytes(path, [1]);
        o.Store.Register(path);
        o.Session.Video = new CaptionRequestVideo(path, "x", 60, DateTimeOffset.Now);

        o.Ai.ResponseText = SegmentAnswer;
        o.Ai.ParseCommand.Execute(null);
        o.Ai.ImportCommand.Execute(null);
        Assert.Equal([path], o.Windows.DeletePaths);
        Assert.False(File.Exists(path));
        Assert.Null(o.Session.Video);
        Assert.Contains("依頼用動画を削除しました", o.Ai.ParseMessage);
    }

    [Fact]
    public async Task DecliningTheDeleteKeepsTheRequestVideo()
    {
        var o = await OpenAsync(before: p => p.SceneChanges.AddRange([20, 40]));
        o.Windows.DeleteAnswer = false;
        var path = Path.Combine(_dir, "cut-request_keep.mp4");
        File.WriteAllBytes(path, [1]);
        o.Store.Register(path);
        o.Session.Video = new CaptionRequestVideo(path, "x", 60, DateTimeOffset.Now);

        o.Ai.ResponseText = SegmentAnswer;
        o.Ai.ParseCommand.Execute(null);
        o.Ai.ImportCommand.Execute(null);
        Assert.True(File.Exists(path));
        Assert.NotNull(o.Session.Video);
        Assert.Contains("削除せず", o.Ai.ParseMessage);
    }

    [Fact]
    public void SegmentIdsText()
    {
        Assert.Equal("", CutRowViewModel.SegmentIds([]));
        Assert.Equal("12", CutRowViewModel.SegmentIds([12]));
        Assert.Equal("12〜14", CutRowViewModel.SegmentIds([12, 13, 14]));
        Assert.Equal("12, 15", CutRowViewModel.SegmentIds([12, 15]));
    }
}
