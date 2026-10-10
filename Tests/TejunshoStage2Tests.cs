using System.Text.Json;
using ManualVideoStudio.Core.Ai;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;

namespace ManualVideoStudio.Tests;

/// <summary>手順書との連携の段階 2（v1.9）: 章・操作文・補足、schemaVersion 7、Gemini の操作文、依頼文のテンプレート。</summary>
public sealed class TejunshoStage2Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-stage2-" + Guid.NewGuid().ToString("N"));

    public TejunshoStage2Tests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static ProjectEditor EditorWithCaptures(int count)
    {
        var ed = new ProjectEditor(TestProjects.Create());
        for (int i = 0; i < count; i++) Assert.True(ed.AddCapture(5 + i * 5).Result.Success);
        return ed;
    }

    [Fact]
    public void EditorSetsStepTextNoteAndChapterWithUndo()
    {
        var ed = EditorWithCaptures(1);
        var id = ed.Project.CapturePoints[0].Id;
        Assert.True(ed.SetStepText(id, "  「設定」を\r\nクリックする。\t").Success);
        Assert.Equal("「設定」を クリックする。", ed.Project.CapturePoints[0].StepText);   // 1 行にする
        Assert.Equal("操作文の変更", ed.History.UndoLabel);
        Assert.True(ed.SetNote(id, "\r\n確認する。\r\n値: 8443\r\n").Success);
        Assert.Equal("確認する。\n値: 8443", ed.Project.CapturePoints[0].Note);          // 改行は LF で残す
        Assert.Equal("補足の変更", ed.History.UndoLabel);
        Assert.True(ed.SetChapterTitle(id, " 初期設定 ").Success);
        Assert.Equal("初期設定", ed.Project.CapturePoints[0].ChapterTitle);
        Assert.Equal("章の変更", ed.History.UndoLabel);

        // 同じ値は Undo に積まない。
        Assert.True(ed.SetChapterTitle(id, "初期設定").Success);
        Assert.Equal("章の変更", ed.History.UndoLabel);
        ed.Undo();
        Assert.Equal("", ed.Project.CapturePoints[0].ChapterTitle);
        ed.Undo();
        ed.Undo();
        Assert.Equal("", ed.Project.CapturePoints[0].StepText);
        ed.Redo();
        Assert.Equal("「設定」を クリックする。", ed.Project.CapturePoints[0].StepText);
        Assert.False(ed.SetNote(999, "x").Success);
    }

    [Fact]
    public void ChaptersFollowSequenceOrder()
    {
        List<CapturePoint> points =
        [
            new() { Id = 3, Sequence = 3, ChapterTitle = "Wi-Fi設定" },
            new() { Id = 1, Sequence = 1 },
            new() { Id = 2, Sequence = 2, ChapterTitle = " 初期設定 " },
            new() { Id = 4, Sequence = 4 },
        ];
        var r = CaptureChapters.Resolve(points);
        Assert.Equal((null, false), r[1]);          // 最初の章の前
        Assert.Equal(("初期設定", true), r[2]);
        Assert.Equal(("Wi-Fi設定", true), r[3]);
        Assert.Equal(("Wi-Fi設定", false), r[4]);
    }

    [Fact]
    public void MetadataHasStepTextNoteAndChapters()
    {
        List<CapturePoint> points =
        [
            new() { Id = 1, Sequence = 1, SelectedFrameTime = 1, Caption = "開く" },
            new() { Id = 2, Sequence = 2, SelectedFrameTime = 2, Caption = "入力", ChapterTitle = "接続先の設定", StepText = "「ポート」に「8443」を入力する。", Note = "値を確認する。" },
            new() { Id = 3, Sequence = 3, SelectedFrameTime = 3, Caption = "保存", StepText = "   " },
        ];
        var plan = ImageExportService.Plan(points, Path.Combine(_dir, "images"), ImageFormat.Png);
        using var doc = JsonDocument.Parse(ImageExportService.BuildMetadataJson(@"C:\v\a.mp4", plan, null));
        var c = doc.RootElement.GetProperty("captures");
        Assert.Equal(JsonValueKind.Null, c[0].GetProperty("chapter").ValueKind);
        Assert.False(c[0].GetProperty("chapterStart").GetBoolean());
        Assert.Equal(JsonValueKind.Null, c[0].GetProperty("stepText").ValueKind);
        Assert.Equal("接続先の設定", c[1].GetProperty("chapter").GetString());
        Assert.True(c[1].GetProperty("chapterStart").GetBoolean());
        Assert.Equal("「ポート」に「8443」を入力する。", c[1].GetProperty("stepText").GetString());
        Assert.Equal("値を確認する。", c[1].GetProperty("note").GetString());
        Assert.Equal("接続先の設定", c[2].GetProperty("chapter").GetString());
        Assert.False(c[2].GetProperty("chapterStart").GetBoolean());
        Assert.Equal(JsonValueKind.Null, c[2].GetProperty("stepText").ValueKind);   // 空白だけは null
    }

    [Fact]
    public void Schema7RoundTripAndSchema6LoadsWithEmptyTexts()
    {
        var p = TestProjects.Create();
        p.CapturePoints.Add(new CapturePoint { Id = 1, Sequence = 1, SelectedFrameTime = 1, Timestamp = 1, Caption = "a", StepText = "s", Note = "n1\nn2", ChapterTitle = "章" });
        p.NextCaptureId = 2;
        var file = Path.Combine(_dir, "p.mvsproj.json");
        new ProjectService().Save(p, file);
        var text = File.ReadAllText(file);
        Assert.Contains("\"schemaVersion\": 8", text);
        var loaded = new ProjectService().Load(file).Project.CapturePoints.Single();
        Assert.Equal(("s", "n1\nn2", "章"), (loaded.StepText, loaded.Note, loaded.ChapterTitle));

        // schemaVersion 6（v1.8 以前）のファイルにはキーが無い → 空。
        var old = Path.Combine(_dir, "old.mvsproj.json");
        File.WriteAllText(old, """
            { "schemaVersion": 6, "sourceVideo": { "path": "C:\\none\\v.mp4" },
              "capturePoints": [ { "id": 1, "sequence": 1, "timestamp": 1, "selectedFrameTime": 1, "caption": "a" } ], "nextCaptureId": 2 }
            """);
        var o = new ProjectService().Load(old).Project.CapturePoints.Single();
        Assert.Equal(("", "", ""), (o.StepText, o.Note, o.ChapterTitle));

        // null も空として読む（手編集対策）。
        File.WriteAllText(old, File.ReadAllText(old).Replace("\"caption\": \"a\"", "\"caption\": \"a\", \"stepText\": null, \"note\": null, \"chapterTitle\": null"));
        o = new ProjectService().Load(old).Project.CapturePoints.Single();
        Assert.Equal(("", "", ""), (o.StepText, o.Note, o.ChapterTitle));
    }

    [Fact]
    public async Task ArchiveExportImportKeepsStage2Fields()
    {
        var root = Path.Combine(_dir, "root");
        var folder = Path.Combine(root, "20260926_1000_テスト");
        Directory.CreateDirectory(folder);
        var video = Path.Combine(folder, "rec_20260926_100000.mp4");
        File.WriteAllBytes(video, new byte[1024]);
        var p = TestProjects.Create();
        p.SourceVideo.Path = video;
        p.CapturePoints.Add(new CapturePoint { Id = 1, Sequence = 1, SelectedFrameTime = 1, Timestamp = 1, Caption = "a", StepText = "「保存」をクリックする。", Note = "確認する。", ChapterTitle = "初期設定" });
        p.NextCaptureId = 2;
        new ProjectService().Save(p, Path.Combine(folder, "20260926_1000_テスト.mvsproj.json"));

        var zip = Path.Combine(_dir, "x.zip");
        await new ProjectArchive().ExportAsync(folder, zip, "1.9.0", null, CancellationToken.None);
        var root2 = Path.Combine(_dir, "root2");
        Directory.CreateDirectory(root2);
        var imported = await new ProjectArchive().ImportAsync(zip, root2, null, CancellationToken.None);
        var loaded = new ProjectService().Load(ProjectFolders.ProjectFilesIn(imported)[0]).Project.CapturePoints.Single();
        Assert.Equal(("「保存」をクリックする。", "確認する。", "初期設定"), (loaded.StepText, loaded.Note, loaded.ChapterTitle));
    }

    [Fact]
    public void CaptionResponseWithStepTextIsParsedAndImported()
    {
        var r = AiResponseParser.ParseCaptions("""
            {"captures":[
              {"id":1,"timestamp":"00:00:05.000","caption":"設定を開く","step_text":"「設定」を\nクリックする。"},
              {"id":2,"caption":"保存","stepText":"「保存」をクリックする。"},
              {"id":3,"caption":"確認"},
              {"id":4,"caption":"空","step_text":"  "}
            ]}
            """);
        Assert.True(r.Success);
        Assert.Equal("「設定」を\nクリックする。", r.Items[0].StepText);
        Assert.Equal("「保存」をクリックする。", r.Items[1].StepText);
        Assert.Null(r.Items[2].StepText);

        var ed = EditorWithCaptures(4);
        var ids = ed.Project.CapturePoints.OrderBy(p => p.Sequence).Select(p => p.Id).ToList();
        ed.SetStepText(ids[2], "入力済みの操作文");
        var match = CaptionImporter.Match(r.Items, ed.Project.CapturePoints);
        Assert.Equal("「設定」を クリックする。", match.Rows[0].NewStepText);   // 1 行にする
        Assert.Null(match.Rows[2].NewStepText);
        Assert.Null(match.Rows[3].NewStepText);
        Assert.Equal("入力済みの操作文", match.Rows[2].CurrentStepText);

        var n = ed.ApplyCaptions(match.Rows.Select(x => (x.Id, x.NewCaption, x.NewStepText)), CaptionSource.Ai, "AI キャプションの取り込み");
        Assert.Equal(4, n);
        Assert.Equal("「設定」を クリックする。", ed.Project.CapturePoints.First(p => p.Id == ids[0]).StepText);
        Assert.Equal("入力済みの操作文", ed.Project.CapturePoints.First(p => p.Id == ids[2]).StepText);   // step_text が無ければ変えない
        ed.Undo();   // 1 回で戻る
        Assert.All(ed.Project.CapturePoints.Where(p => p.Id != ids[2]), p => Assert.Equal("", p.StepText));
    }

    [Fact]
    public void CaptureCaptionTemplateAsksForStepTextAndSendsCurrentStepText()
    {
        var t = DefaultPromptTemplates.CaptureCaption;
        Assert.Contains("\"step_text\"", t);
        foreach (var verb in new[] { "クリックする", "選択する", "入力する", "チェックを入れる", "チェックを外す" }) Assert.Contains(verb, t);
        var json = PromptVariables.CapturePointsJson([new CapturePoint { Id = 1, Sequence = 1, Caption = "c", StepText = "s" }]);
        Assert.Contains("\"current_step_text\": \"s\"", json);
        // 既定のテンプレートの出力例が、そのまま取り込める（操作文つき）。
        var r = AiResponseParser.ParseCaptions(t[t.IndexOf("## 出力形式", StringComparison.Ordinal)..]);
        Assert.True(r.Success, r.Error);
        Assert.Equal("「設定」をクリックする。", r.Items.Single().StepText);
    }

    [Fact]
    public void TejunshoRequestTemplateIsStoredOutsideTemplatesDictionary()
    {
        var path = Path.Combine(_dir, "prompt-templates.json");
        var svc = new PromptTemplateService(path);
        Assert.Equal(DefaultPromptTemplates.TejunshoRequest, svc.Get(AiPurpose.TejunshoRequest));
        Assert.False(svc.IsCustomized(AiPurpose.TejunshoRequest));

        svc.Save(AiPurpose.TejunshoRequest, "{{OUTPUT_DIR}} から手順書を作って。題名は {{PROJECT_NAME}}、画像 {{IMAGE_COUNT}} 枚");
        svc.Save(AiPurpose.CutReview, "レビュー {{CUT_SEGMENTS}}");
        Assert.True(svc.IsCustomized(AiPurpose.TejunshoRequest));
        using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
        {
            Assert.StartsWith("{{OUTPUT_DIR}}", doc.RootElement.GetProperty("tejunshoRequest").GetString());
            var keys = doc.RootElement.GetProperty("templates").EnumerateObject().Select(p => p.Name).ToList();
            Assert.Equal(["cutReview"], keys);   // v1.8 以前のアプリが知らない列挙値を templates に入れない
        }
        // v1.8 以前の形式（tejunshoRequest を知らない）として読んでも失敗しない。
        var asOld = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path))!;
        Assert.True(asOld.ContainsKey("templates"));
        Assert.Equal("{{OUTPUT_DIR}} から手順書を作って。題名は {{PROJECT_NAME}}、画像 {{IMAGE_COUNT}} 枚", new PromptTemplateService(path).Get(AiPurpose.TejunshoRequest));

        svc.ResetToDefault(AiPurpose.TejunshoRequest);
        Assert.False(svc.IsCustomized(AiPurpose.TejunshoRequest));
        Assert.Equal("レビュー {{CUT_SEGMENTS}}", svc.Get(AiPurpose.CutReview));
    }

    [Fact]
    public void RequestTextRendersVariablesAndWarnsUnknown()
    {
        var r = TejunshoExport.RenderRequest("{{OUTPUT_DIR}} / {{PROJECT_NAME}} / {{IMAGE_COUNT}} / {{FOO}}", @"C:\p\tejunsho", "ルーター設定", 6);
        Assert.Equal(@"C:\p\tejunsho / ルーター設定 / 6 / {{FOO}}", r.Text);
        Assert.Equal(["FOO"], r.UnknownVariables);
        // 出力先はどちらか一方があればよい（v1.10）。
        Assert.Equal([[PromptVariables.OutputDirHome, PromptVariables.OutputDir]], PromptVariables.Required(AiPurpose.TejunshoRequest));
        Assert.Empty(PromptVariables.MissingRequired(AiPurpose.TejunshoRequest, "{{OUTPUT_DIR}} から"));
        Assert.Empty(PromptVariables.MissingRequired(AiPurpose.TejunshoRequest, "{{OUTPUT_DIR_HOME}} から"));
        Assert.Equal(["{{OUTPUT_DIR_HOME}} または {{OUTPUT_DIR}}"], PromptVariables.MissingRequired(AiPurpose.TejunshoRequest, "{{PROJECT_NAME}} の手順書"));
        Assert.DoesNotContain(PromptVariables.For(AiPurpose.TejunshoRequest), v => v.Name == PromptVariables.CapturePoints);
    }
}
