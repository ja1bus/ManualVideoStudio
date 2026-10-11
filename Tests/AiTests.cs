using ManualVideoStudio.Core.Ai;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Tests;

public class PromptRendererTests
{
    [Fact]
    public void ExpandsKnownVariables()
    {
        var r = PromptRenderer.Render("動画 {{VIDEO_FILENAME}} / {{ LANGUAGE }} / {{video_duration}}",
            new Dictionary<string, string> { ["VIDEO_FILENAME"] = "a.mp4", ["LANGUAGE"] = "日本語", ["VIDEO_DURATION"] = "00:01:00.000" });
        Assert.Equal("動画 a.mp4 / 日本語 / 00:01:00.000", r.Text);
        Assert.False(r.HasWarnings);
    }

    [Fact]
    public void UnknownVariablesAreKeptAndReported()
    {
        var r = PromptRenderer.Render("{{FOO}} と {{FOO}} と {{BAR}}", new Dictionary<string, string>());
        Assert.Equal("{{FOO}} と {{FOO}} と {{BAR}}", r.Text);
        Assert.Equal(["FOO", "BAR"], r.UnknownVariables);
        Assert.Contains("{{FOO}}", r.WarningMessage);
    }

    [Fact]
    public void BrokenSyntaxIsWarned()
    {
        var r = PromptRenderer.Render("これは {{VIDEO_FILENAME です", new Dictionary<string, string> { ["VIDEO_FILENAME"] = "x" });
        Assert.Single(r.Warnings);
        Assert.Empty(r.UnknownVariables);
    }

    [Fact]
    public void ValuesContainingBracesAreNotReExpanded()
    {
        var r = PromptRenderer.Render("{{A}}", new Dictionary<string, string> { ["A"] = "{\"x\": 1}", ["B"] = "no" });
        Assert.Equal("{\"x\": 1}", r.Text);
    }

    [Fact]
    public void DefaultTemplatesUseOnlyKnownVariables()
    {
        foreach (var purpose in Enum.GetValues<AiPurpose>())
        {
            var known = PromptVariables.For(purpose).Select(v => v.Name).ToHashSet();
            var vars = PromptRenderer.FindVariables(DefaultPromptTemplates.Get(purpose));
            Assert.All(vars, v => Assert.Contains(v, known));
            Assert.Empty(PromptVariables.MissingRequired(purpose, DefaultPromptTemplates.Get(purpose)));
        }
    }

    [Fact]
    public void BuildVariablesFromProject()
    {
        var ed = new ProjectEditor(TestProjects.Create(90));
        ed.AddCapture(7.12);
        ed.SetCaption(1, "設定を開く");
        ed.AddCut(10, 20, SegmentAction.Remove, 0);
        var v = PromptVariables.Build(ed.Project, "日本語");
        Assert.Equal("manual.mp4", v[PromptVariables.VideoFilename]);
        Assert.Equal("00:01:30.000", v[PromptVariables.VideoDuration]);
        Assert.Equal("00:01:20.000", v[PromptVariables.EditedDuration]);
        Assert.Contains("\"timestamp\": \"00:00:07.120\"", v[PromptVariables.CapturePoints]);
        Assert.Contains("\"current_caption\": \"設定を開く\"", v[PromptVariables.CapturePoints]);
        Assert.Contains("\"action\": \"remove\"", v[PromptVariables.CutSegments]);

        var rendered = PromptRenderer.Render(DefaultPromptTemplates.CaptureCaption, v);
        Assert.False(rendered.HasWarnings, rendered.WarningMessage);
        Assert.Contains("\"id\": 1", rendered.Text);
    }
}

public class PromptTemplateServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-test-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void SaveReloadAndReset()
    {
        var path = Path.Combine(_dir, "prompt-templates.json");
        var s = new PromptTemplateService(path);
        Assert.Equal(DefaultPromptTemplates.CaptureCaption, s.Get(AiPurpose.CaptureCaption));
        Assert.False(s.IsCustomized(AiPurpose.CaptureCaption));

        s.Save(AiPurpose.CaptureCaption, "カスタム {{CAPTURE_POINTS}}");
        var reloaded = new PromptTemplateService(path);
        Assert.Equal("カスタム {{CAPTURE_POINTS}}", reloaded.Get(AiPurpose.CaptureCaption));
        Assert.True(reloaded.IsCustomized(AiPurpose.CaptureCaption));
        Assert.Equal(DefaultPromptTemplates.CutAnalysis, reloaded.Get(AiPurpose.CutAnalysis));

        reloaded.ResetToDefault(AiPurpose.CaptureCaption);
        Assert.Equal(DefaultPromptTemplates.CaptureCaption, new PromptTemplateService(path).Get(AiPurpose.CaptureCaption));
    }

    [Fact]
    public void BrokenFileFallsBackToDefaults()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "prompt-templates.json");
        File.WriteAllText(path, "{ broken");
        var s = new PromptTemplateService(path);
        Assert.Equal(DefaultPromptTemplates.CutReview, s.Get(AiPurpose.CutReview));
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void EmptyTextMeansDefault()
    {
        var s = new PromptTemplateService(Path.Combine(_dir, "p.json"));
        s.Save(AiPurpose.CutAnalysis, "   ");
        Assert.Equal(DefaultPromptTemplates.CutAnalysis, s.Get(AiPurpose.CutAnalysis));
    }
}

public class AiResponseParserTests
{
    private const string CaptionJson = """
        {
          "captures": [
            { "id": 1, "timestamp": "00:00:07.120", "caption": "Windowsの設定画面を開く" },
            { "id": 2, "timestamp": "00:00:12.400", "caption": "ネットワークとインターネットを選択" }
          ]
        }
        """;

    [Fact]
    public void WholeResponseIsJson()
    {
        var r = AiResponseParser.ParseCaptions(CaptionJson);
        Assert.True(r.Success);
        Assert.Equal(JsonExtractionMethod.Whole, r.Extraction!.Method);
        Assert.Equal(2, r.Items.Count);
        Assert.Equal("Windowsの設定画面を開く", r.Items[0].Caption);
    }

    [Fact]
    public void MarkdownCodeBlockWithText()
    {
        var text = "以下が結果です。\n\n```json\n" + CaptionJson + "\n```\n\nご確認ください。{ 余計な括弧 }";
        var r = AiResponseParser.ParseCaptions(text);
        Assert.True(r.Success);
        Assert.Equal(JsonExtractionMethod.CodeBlock, r.Extraction!.Method);
    }

    [Fact]
    public void CodeBlockWithoutLanguage()
    {
        var r = AiResponseParser.ExtractJson("結果:\n```\n{\"cuts\": []}\n```");
        Assert.True(r.Success);
        Assert.Equal(JsonExtractionMethod.CodeBlock, r.Method);
    }

    [Fact]
    public void BraceRangeFallback()
    {
        var r = AiResponseParser.ParseCaptions("はい、作成しました。" + CaptionJson + " 以上です。");
        Assert.True(r.Success);
        Assert.Equal(JsonExtractionMethod.BraceRange, r.Extraction!.Method);
    }

    [Fact]
    public void BrokenJsonIsNotRepaired()
    {
        var r = AiResponseParser.ParseCaptions("{ \"captures\": [ { \"id\": 1, \"caption\": \"途中で切れた");
        Assert.False(r.Success);
        Assert.StartsWith(AiResponseParser.NotJsonMessage, r.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("JSON はありません")]
    public void NoJson(string text) => Assert.False(AiResponseParser.ExtractJson(text).Success);

    [Fact]
    public void TrailingCommaIsTolerated()
        => Assert.True(AiResponseParser.ParseCaptions("{\"captures\":[{\"id\":1,\"caption\":\"a\"},]}").Success);

    [Fact]
    public void MissingCapturesArray()
    {
        var r = AiResponseParser.ParseCaptions("{\"items\": []}");
        Assert.False(r.Success);
        Assert.Contains("captures", r.Error);
    }

    [Fact]
    public void InvalidItemsProduceWarnings()
    {
        var r = AiResponseParser.ParseCaptions("""{"captures":[{"id":"2","caption":"文字列ID"},{"id":"x","caption":"a"},{"id":3},5]}""");
        Assert.True(r.Success);
        Assert.Equal(2, Assert.Single(r.Items).Id);
        Assert.Equal(3, r.Warnings.Count);
    }

    [Fact]
    public void ParseCuts()
    {
        const string text = """
            ```json
            {
              "cuts": [
                { "start": "00:01:20.200", "end": "00:01:27.800", "action": "shorten", "keep_seconds": 1.5, "confidence": 0.91, "reason": "長い待機" },
                { "start": 5, "end": 8.5, "action": "remove", "confidence": 0.5, "reason": "操作ミス" }
              ]
            }
            ```
            """;
        var r = AiResponseParser.ParseCuts(text);
        Assert.True(r.Success);
        Assert.Equal(2, r.Items.Count);
        Assert.Equal("00:01:20.200", r.Items[0].Start);
        Assert.Equal("5", r.Items[1].Start);
        Assert.Equal(1.5, r.Items[0].KeepSeconds);
    }

    [Fact]
    public void EmptyCutsIsValid() => Assert.True(AiResponseParser.ParseCuts("{\"cuts\": []}").Success);

    [Fact]
    public void ParseReview()
    {
        const string text = """
            {"summary":"概ね良好","reviews":[{"target_id":"abc","verdict":"Adjust","comment":"確認画面が消えている"}],
             "cuts":[{"start":"00:00:30.000","end":"00:00:35.000","action":"remove","confidence":0.6,"reason":"迷い"}]}
            """;
        var r = AiResponseParser.ParseReview(text);
        Assert.True(r.Success);
        Assert.Equal("概ね良好", r.Summary);
        Assert.Equal("adjust", r.Reviews[0].Verdict);
        Assert.Single(r.Cuts);
    }
}

public class CaptionImporterTests
{
    private static List<CapturePoint> Points() =>
    [
        new() { Id = 1, Sequence = 1, Timestamp = 7.12, SelectedFrameTime = 7.12 },
        new() { Id = 2, Sequence = 2, Timestamp = 12.4, SelectedFrameTime = 12.6 },
        new() { Id = 5, Sequence = 3, Timestamp = 20, SelectedFrameTime = 20 },
    ];

    [Fact]
    public void MatchesById()
    {
        var parsed = new List<ParsedCaption>
        {
            new(2, "00:00:12.600", "Wi-Fiを選択"),
            new(1, "00:00:07.120", "設定を開く"),
        };
        var r = CaptionImporter.Match(parsed, Points());
        Assert.Equal(2, r.ImportableCount);
        Assert.Equal([5], r.MissingIds);
        Assert.Equal(2, r.Rows.Single(x => x.Id == 2).Sequence);
    }

    [Fact]
    public void TimestampFromAiIsNotTrustedButNoted()
    {
        var r = CaptionImporter.Match([new ParsedCaption(1, "00:00:09.000", "設定を開く")], Points());
        var row = Assert.Single(r.Rows);
        Assert.Equal(CaptionMatchStatus.Ok, row.Status);
        Assert.Equal(7.12, row.AppTime);
        Assert.Contains("画像位置は変更しません", row.Note);
    }

    [Fact]
    public void UnknownDuplicateAndEmpty()
    {
        var parsed = new List<ParsedCaption>
        {
            new(99, null, "存在しない"),
            new(1, null, "一回目"),
            new(1, null, "二回目"),
            new(2, null, "   "),
        };
        var r = CaptionImporter.Match(parsed, Points());
        Assert.Equal([CaptionMatchStatus.UnknownId, CaptionMatchStatus.Ok, CaptionMatchStatus.DuplicateId, CaptionMatchStatus.Empty],
            r.Rows.Select(x => x.Status));
        Assert.Equal(1, r.ImportableCount);
        Assert.False(r.Rows[0].Include);
    }

    [Fact]
    public void ApplyingImportDoesNotChangeTimes()
    {
        var project = TestProjects.Create();
        project.CapturePoints.AddRange(Points());
        project.NextCaptureId = 6;
        var ed = new ProjectEditor(project);
        var r = CaptionImporter.Match([new ParsedCaption(2, "00:00:30.000", "Wi-Fiを選択")], project.CapturePoints);
        ed.ApplyCaptions(r.Rows.Where(x => x.Include).Select(x => (x.Id, x.NewCaption)), CaptionSource.Ai);
        var p = project.CapturePoints.Single(x => x.Id == 2);
        Assert.Equal("Wi-Fiを選択", p.Caption);
        Assert.Equal(12.6, p.SelectedFrameTime);
        Assert.Equal(12.4, p.Timestamp);
        Assert.Equal(CaptionSource.Ai, p.CaptionSource);
    }

    [Fact]
    public void NewlinesInCaptionAreFlattened()
    {
        var r = CaptionImporter.Match([new ParsedCaption(1, null, "設定を\n開く")], Points());
        Assert.Equal("設定を 開く", r.Rows[0].NewCaption);
    }
}

public class AiCutImporterTests
{
    [Fact]
    public void ValidatesAndConverts()
    {
        var parsed = new List<ParsedCut>
        {
            new("00:01:20.200", "00:01:27.800", "shorten", 1.5, 0.91, "待機"),
            new("5", "8.5", "remove", null, 91, "ミス"),
            new("00:00:10", "00:00:09", "remove", null, null, "逆転"),
            new("00:00:10", "00:00:20", "speedup", null, null, "未対応"),
            new("00:00:30", "00:00:32", "shorten", 3, null, "長すぎ"),
            new("00:01:58", "00:02:00.5", "shorten", null, 0.5, "末尾"),
        };
        var rows = AiCutImporter.Validate(parsed, 120, 1.5);
        Assert.True(rows[0].IsValid);
        // 古い shorten は、先頭を残して以降を削除する区間に変換する（v1.17.0）。
        Assert.Equal(SegmentAction.Remove, rows[0].Action);
        Assert.Equal(81.7, rows[0].Start, 3);
        Assert.Contains(rows[0].Warnings, w => w.Contains("変換"));
        Assert.True(rows[1].IsValid);
        Assert.Equal(0.91, rows[1].Confidence, 3);
        Assert.False(rows[2].IsValid);
        Assert.False(rows[3].IsValid);
        Assert.False(rows[4].IsValid);
        Assert.True(rows[5].IsValid);
        Assert.Equal(120, rows[5].End);
        Assert.Equal(119.5, rows[5].Start, 3);
        Assert.Equal(SegmentAction.Remove, rows[5].Action);

        var cands = AiCutImporter.ToCandidates(rows, "hist1");
        Assert.Equal(3, cands.Count);
        Assert.All(cands, c =>
        {
            Assert.Equal(SegmentSource.Ai, c.Source);
            Assert.Equal(CandidateStatus.Pending, c.Status);
            Assert.Equal("hist1", c.AiHistoryId);
        });
    }

    [Fact]
    public void ExcludedRowsAreNotImported()
    {
        var rows = AiCutImporter.Validate([new ParsedCut("1", "5", "remove", null, 0.5, "a")], 60, 1.5);
        rows[0].Include = false;
        Assert.Empty(AiCutImporter.ToCandidates(rows, null));
    }

    [Fact]
    public void AiCandidatesRequireUserAcceptance()
    {
        var ed = new ProjectEditor(TestProjects.Create(120));
        var rows = AiCutImporter.Validate([new ParsedCut("1", "5", "remove", null, 0.9, "a")], 120, 1.5);
        ed.AddCandidates(AiCutImporter.ToCandidates(rows, null), "AI候補の取り込み");
        Assert.Empty(ed.Project.CutSegments);
        Assert.True(ed.AcceptCandidate(ed.Project.CutCandidates[0].Id).Success);
        Assert.Equal(SegmentSource.Ai, Assert.Single(ed.Project.CutSegments).Source);
    }
}
