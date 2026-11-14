using System.Text.Json;
using System.Text.Json.Nodes;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;

namespace ManualVideoStudio.Tests;

/// <summary>v1.21（schemaVersion 9）のデータ形式: 拡大・クリック位置の強調・区間表・録画のショートカット。</summary>
public sealed class Release121FormatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-test121-" + Guid.NewGuid().ToString("N"));

    public Release121FormatTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private string SaveAndRead(ProjectDocument project)
    {
        var path = ProjectService.NormalizePath(Path.Combine(_dir, "p"));
        new ProjectService().Save(project, path);
        return path;
    }

    [Fact]
    public void SchemaVersionIs9()
    {
        Assert.Equal(9, ProjectDocument.CurrentSchemaVersion);
    }

    [Fact]
    public void ZoomClickHighlightAndSegmentsRoundTrip()
    {
        var project = TestProjects.Create();
        project.EditPhase = EditPhase.Subtitle;
        project.ShapeGroups.Add(new ShapeGroup
        {
            Start = 1, End = 4, ImageWidth = 1280, ImageHeight = 720,
            Shapes = [new RectangleAnnotation { X = 10, Y = 10, Width = 50, Height = 30 }, new ZoomAnnotation { X = 640, Y = 360, Width = 640, Height = 360 }],
        });
        project.ClickHighlight = new ClickHighlightSettings { Enabled = true, Size = ClickHighlightSize.Large };
        project.AiHistory.Add(new AiHistoryEntry { Purpose = AiPurpose.CutAnalysis, Segments = [new AiSegment { Id = 1, Start = 0, End = 5.5 }] });
        project.CutCandidates.Add(new CutCandidate { Start = 10, End = 20, Kind = CandidateKind.IdleAndFreeze });

        var path = SaveAndRead(project);
        var text = File.ReadAllText(path);
        Assert.Contains("\"type\": \"zoom\"", text);
        Assert.Contains("\"schemaVersion\": 9", text);
        Assert.Contains("\"idleAndFreeze\"", text);

        var p = new ProjectService().Load(path).Project;
        var g = Assert.Single(p.ShapeGroups);
        Assert.NotNull(g.Zoom);
        Assert.Equal(2.0, g.Zoom!.FactorFor(g.ImageWidth), 6);
        Assert.Equal("四角形・拡大", g.Summary);
        Assert.True(p.ClickHighlight.Enabled);
        Assert.Equal(ClickHighlightSize.Large, p.ClickHighlight.Size);
        Assert.Equal(5.5, Assert.Single(Assert.Single(p.AiHistory).Segments).End);
        Assert.Equal(CandidateKind.IdleAndFreeze, Assert.Single(p.CutCandidates).Kind);
    }

    [Fact]
    public void Version8FileLoadsWithoutNewItems()
    {
        var project = TestProjects.Create();
        project.AiHistory.Add(new AiHistoryEntry { Purpose = AiPurpose.CutAnalysis });
        var path = SaveAndRead(project);
        // 1.20 の形式に戻す（新しい項目を消す）。
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["schemaVersion"] = 8;
        node.Remove("clickHighlight");
        foreach (var h in node["aiHistory"]!.AsArray()) h!.AsObject().Remove("segments");
        File.WriteAllText(path, node.ToJsonString());

        var p = new ProjectService().Load(path).Project;
        Assert.False(p.ClickHighlight.Enabled);
        Assert.Equal(ClickHighlightSize.Medium, p.ClickHighlight.Size);
        Assert.Empty(Assert.Single(p.AiHistory).Segments);
    }

    [Fact]
    public void ZoomInCaptureAnnotationsAndExtraZoomsAreDropped()
    {
        var project = TestProjects.Create();
        project.EditPhase = EditPhase.Subtitle;
        project.CapturePoints.Add(new CapturePoint
        {
            Id = 1, Sequence = 1, Timestamp = 1, SelectedFrameTime = 1, AnnotationImageWidth = 100, AnnotationImageHeight = 100,
            Annotations = [new ZoomAnnotation { Width = 50, Height = 50 }, new RectangleAnnotation { Width = 5, Height = 5 }],
        });
        project.ShapeGroups.Add(new ShapeGroup
        {
            Start = 1, End = 2, ImageWidth = 100, ImageHeight = 100,
            Shapes = [new ZoomAnnotation { Width = 0, Height = 0 }, new ZoomAnnotation { X = 1, Width = 50, Height = 50 }, new ZoomAnnotation { X = 2, Width = 60, Height = 60 }],
        });
        var p = new ProjectService().Load(SaveAndRead(project)).Project;
        Assert.IsType<RectangleAnnotation>(Assert.Single(p.CapturePoints[0].Annotations));
        var zoom = Assert.IsType<ZoomAnnotation>(Assert.Single(p.ShapeGroups[0].Shapes));
        Assert.Equal(1, zoom.X);
    }

    [Fact]
    public void UndoRestoresClickHighlight()
    {
        var project = TestProjects.Create();
        var ed = new ProjectEditor(project);
        // EditState に含まれることを、別の編集の Undo で確認する（強調の設定を変える操作は v1.21 の画面で追加）。
        project.ClickHighlight.Enabled = true;
        ed.AddCut(1, 2, SegmentAction.Remove, 0);
        project.ClickHighlight.Enabled = false;
        ed.Undo();
        Assert.True(project.ClickHighlight.Enabled);
    }

    [Fact]
    public void HotkeysDefaultAndDisplayText()
    {
        var h = new RecordingHotkeys();
        Assert.Equal("Ctrl+Shift+F9", h.StartStop.DisplayText);
        Assert.Equal("Ctrl+Shift+F8", h.Pause.DisplayText);
        Assert.Equal("Ctrl+Shift+F7", h.Marker.DisplayText);
        Assert.Equal("Ctrl+Alt+5", new RecordingHotkey(HotkeyModifiers.CtrlAlt, HotkeyKey.D5).DisplayText);
        Assert.Equal("Ctrl+Alt+Shift+M", new RecordingHotkey(HotkeyModifiers.CtrlAltShift, HotkeyKey.M).DisplayText);
    }

    [Fact]
    public void HotkeysDuplicateOrInvalidFallBackToDefaults()
    {
        var s = new RecordingSettings();
        s.Hotkeys.Marker = s.Hotkeys.Pause;
        s.NormalizeOnLoad();
        Assert.Equal(RecordingHotkeys.DefaultMarker, s.Hotkeys.Marker);

        var t = new RecordingSettings { Hotkeys = null! };
        t.NormalizeOnLoad();
        Assert.Equal(RecordingHotkeys.DefaultStartStop, t.Hotkeys.StartStop);

        var u = new RecordingSettings();
        u.Hotkeys.Pause = new RecordingHotkey((HotkeyModifiers)99, HotkeyKey.A);
        u.NormalizeOnLoad();
        Assert.Equal(RecordingHotkeys.DefaultPause, u.Hotkeys.Pause);
    }

    [Fact]
    public void HotkeysRoundTripThroughSettingsJson()
    {
        var app = new AppSettings();
        app.Recording.Hotkeys.Marker = new RecordingHotkey(HotkeyModifiers.CtrlAlt, HotkeyKey.M);
        app.Recording.LogInput = false;
        var json = JsonSerializer.Serialize(app, JsonDefaults.Options);
        var back = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Options)!;
        back.Recording.NormalizeOnLoad();
        Assert.Equal(new RecordingHotkey(HotkeyModifiers.CtrlAlt, HotkeyKey.M), back.Recording.Hotkeys.Marker);
        Assert.False(back.Recording.LogInput);

        // 旧設定（hotkeys・logInput なし）は既定値。
        var node = JsonNode.Parse(json)!.AsObject();
        node["recording"]!.AsObject().Remove("hotkeys");
        node["recording"]!.AsObject().Remove("logInput");
        var old = JsonSerializer.Deserialize<AppSettings>(node.ToJsonString(), JsonDefaults.Options)!;
        old.Recording.NormalizeOnLoad();
        Assert.Equal(RecordingHotkeys.DefaultMarker, old.Recording.Hotkeys.Marker);
        Assert.False(old.Recording.LogInput);   // 既定 OFF（利用者が選んで ON にする）
        Assert.False(new RecordingSettings().LogInput);
    }

    [Fact]
    public void CloneCopiesHotkeysIndependently()
    {
        var s = new RecordingSettings();
        var c = s.Clone();
        c.Hotkeys.Marker = new RecordingHotkey(HotkeyModifiers.CtrlAlt, HotkeyKey.Q);
        Assert.Equal(RecordingHotkeys.DefaultMarker, s.Hotkeys.Marker);
    }
}
