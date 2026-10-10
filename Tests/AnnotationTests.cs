using System.Text.Json;
using ManualVideoStudio.Core.Annotations;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;

namespace ManualVideoStudio.Tests;

internal static class AnnotationSamples
{
    public static List<Annotation> All() =>
    [
        new RectangleAnnotation { X = 10, Y = 20, Width = 100, Height = 50, Color = AnnotationColor.Red, StrokeWidth = 4 },
        new ArrowAnnotation { X1 = 200, Y1 = 200, X2 = 300, Y2 = 250, Color = AnnotationColor.Blue, StrokeWidth = 6 },
        new TextAnnotation { X = 50, Y = 300, Text = "ここをクリック", FontSize = 24, Color = AnnotationColor.Black },
        new MosaicAnnotation { X = 400, Y = 100, Width = 120, Height = 40, BlockSize = 24 },
    ];

    /// <summary>文字の大きさを簡易に見積もる（Core のテスト用。実際の描画側は WPF で計測する）。</summary>
    public static (double Width, double Height) Measure(TextAnnotation t, double scale)
        => (t.Text.Length * t.FontSize * scale, t.FontSize * scale * 1.5);
}

public class AnnotationModelTests
{
    [Fact]
    public void SerializesWithTypeDiscriminatorAndReadableEnums()
    {
        var point = new CapturePoint { Id = 1, Annotations = AnnotationSamples.All(), AnnotationImageWidth = 1920, AnnotationImageHeight = 1080 };
        var json = JsonSerializer.Serialize(point, JsonDefaults.Options);
        Assert.Contains("\"type\": \"rectangle\"", json);
        Assert.Contains("\"type\": \"arrow\"", json);
        Assert.Contains("\"type\": \"text\"", json);
        Assert.Contains("\"type\": \"mosaic\"", json);
        Assert.Contains("\"color\": \"red\"", json);
        Assert.Contains("\"color\": \"blue\"", json);
        Assert.Contains("ここをクリック", json);           // 日本語をエスケープしない
        Assert.Contains("\"fontFamily\": \"Meiryo\"", json);
        Assert.DoesNotContain("\"kind\"", json);           // 種類は type だけで表す

        var back = JsonSerializer.Deserialize<CapturePoint>(json, JsonDefaults.Options)!;
        Assert.Equal(4, back.Annotations.Count);
        var r = Assert.IsType<RectangleAnnotation>(back.Annotations[0]);
        Assert.Equal((10d, 20d, 100d, 50d, AnnotationColor.Red, 4d), (r.X, r.Y, r.Width, r.Height, r.Color, r.StrokeWidth));
        var a = Assert.IsType<ArrowAnnotation>(back.Annotations[1]);
        Assert.Equal((200d, 200d, 300d, 250d, AnnotationColor.Blue, 6d), (a.X1, a.Y1, a.X2, a.Y2, a.Color, a.StrokeWidth));
        var t = Assert.IsType<TextAnnotation>(back.Annotations[2]);
        Assert.Equal(("ここをクリック", 24d, AnnotationColor.Black, "Meiryo"), (t.Text, t.FontSize, t.Color, t.FontFamily));
        var m = Assert.IsType<MosaicAnnotation>(back.Annotations[3]);
        Assert.Equal((400d, 100d, 120d, 40d, 24), (m.X, m.Y, m.Width, m.Height, m.BlockSize));
        Assert.Equal((1920, 1080), (back.AnnotationImageWidth, back.AnnotationImageHeight));
        Assert.Equal(point.Annotations[0].Id, back.Annotations[0].Id);
    }

    [Fact]
    public void CapturePointWithoutAnnotationsFieldDeserializesToEmptyList()
    {
        var back = JsonSerializer.Deserialize<CapturePoint>("""{ "id": 3, "sequence": 1, "timestamp": 1.5, "selectedFrameTime": 1.5, "caption": "x" }""", JsonDefaults.Options)!;
        Assert.NotNull(back.Annotations);
        Assert.Empty(back.Annotations);
        Assert.Equal(0, back.AnnotationImageWidth);
    }

    [Fact]
    public void TextWithoutBoldFieldIsNormalWeight()
    {
        var t = (TextAnnotation)JsonSerializer.Deserialize<Annotation>(
            """{ "type": "text", "id": "x", "x": 1, "y": 2, "text": "a", "fontFamily": "Meiryo", "fontSize": 20, "color": "red" }""", JsonDefaults.Options)!;
        Assert.False(t.Bold);
        var bold = JsonSerializer.Serialize<Annotation>(new TextAnnotation { Text = "b", Bold = true }, JsonDefaults.Options);
        Assert.Contains("\"bold\": true", bold);
    }

    [Fact]
    public void CloneIsIndependent()
    {
        var a = new ArrowAnnotation { X1 = 1, Y1 = 2, X2 = 3, Y2 = 4 };
        var c = (ArrowAnnotation)a.Clone();
        c.Offset(10, 10);
        Assert.Equal(1, a.X1);
        Assert.Equal(11, c.X1);
        Assert.Equal(a.Id, c.Id);
    }

    [Fact]
    public void RoundCoordinatesKeepsOneDecimal()
    {
        var r = new RectangleAnnotation { X = 10.1234, Y = 5.06, Width = 99.99, Height = 1.04 };
        r.RoundCoordinates();
        Assert.Equal((10.1, 5.1, 100.0, 1.0), (r.X, r.Y, r.Width, r.Height));
    }
}

public class AnnotationStyleTests
{
    [Fact]
    public void ThreeFixedColors()
    {
        Assert.Equal(3, Enum.GetValues<AnnotationColor>().Length);
        Assert.Equal("#000000", AnnotationStyle.Hex(AnnotationColor.Black));
        Assert.Equal("#E60012", AnnotationStyle.Hex(AnnotationColor.Red));
        Assert.Equal("#0050D0", AnnotationStyle.Hex(AnnotationColor.Blue));
        Assert.Equal((230, 0, 18), ((int)AnnotationStyle.Rgb(AnnotationColor.Red).R, (int)AnnotationStyle.Rgb(AnnotationColor.Red).G, (int)AnnotationStyle.Rgb(AnnotationColor.Red).B));
        Assert.Equal("赤", AnnotationStyle.Label(AnnotationColor.Red));
    }

    [Theory]
    [InlineData(StrokeWidthLevel.Thin, 2)]
    [InlineData(StrokeWidthLevel.Medium, 4)]
    [InlineData(StrokeWidthLevel.Thick, 6)]
    public void StrokeLevels(StrokeWidthLevel level, double width)
    {
        Assert.Equal(width, AnnotationStyle.StrokeWidth(level));
        Assert.Equal(level, AnnotationStyle.LevelOf(width));
    }

    [Fact]
    public void DefaultsAreMediumStrokeAndMeiryo()
    {
        Assert.Equal(AnnotationStyle.MediumStroke, new RectangleAnnotation().StrokeWidth);
        Assert.Equal(AnnotationStyle.MediumStroke, new ArrowAnnotation().StrokeWidth);
        Assert.Equal("Meiryo", new TextAnnotation().FontFamily);
        Assert.Equal(20, AnnotationStyle.DefaultFontSize);
        Assert.Equal(20, new TextAnnotation().FontSize);
        Assert.False(new TextAnnotation().Bold);                                     // 既定は普通の太さ
        foreach (var size in new double[] { 12, 16, 20, 24 }) Assert.Contains(size, AnnotationStyle.FontSizes); // 最低限の選択肢
        Assert.True(AnnotationStyle.FontSizes.Count >= 10);
        Assert.Equal(AnnotationStyle.FontSizes.Order(), AnnotationStyle.FontSizes);  // 小さい順
    }

    [Theory]
    [InlineData(1280, 720, 1.0)]
    [InlineData(1920, 1080, 1.0)]
    [InlineData(3840, 2160, 2.0)]
    [InlineData(2560, 1440, 1440.0 / 1080)]
    [InlineData(0, 0, 1.0)]
    public void ScaleGrowsOnlyForLargeImages(int w, int h, double expected)
        => Assert.Equal(expected, AnnotationStyle.Scale(w, h), 6);

    [Theory]
    [InlineData(640, 360, 16)]
    [InlineData(1280, 720, 16)]
    [InlineData(1920, 1080, 24)]
    [InlineData(3840, 2160, 48)]
    public void MosaicBlockSizeIsStrongEnough(int w, int h, int expected)
        => Assert.Equal(expected, AnnotationStyle.DefaultMosaicBlockSize(w, h));
}

public class AnnotationGeometryTests
{
    [Fact]
    public void ViewTransformRoundTrip()
    {
        var t = new ViewTransform(0.5, 20, 10);
        var p = t.ToImage(120, 60);
        Assert.Equal(new ImagePoint(200, 100), p);
        Assert.Equal((120d, 60d), t.ToView(p));
        Assert.Equal(40, t.ToImageLength(20));
    }

    [Fact]
    public void FitCentersAndDoesNotEnlargeByDefault()
    {
        var t = ViewTransform.Fit(1920, 1080, 960, 1000);
        Assert.Equal(0.5, t.Scale, 6);
        Assert.Equal(0, t.OffsetX, 6);
        Assert.Equal((1000 - 540) / 2.0, t.OffsetY, 6);
        // 同じ画像点は表示倍率が変わっても同じ画像座標に戻る
        var small = ViewTransform.Fit(1920, 1080, 480, 270);
        var big = ViewTransform.Fit(1920, 1080, 3840, 2160, maxScale: 2);
        var (sx, sy) = small.ToView(new ImagePoint(640, 360));
        var (bx, by) = big.ToView(new ImagePoint(640, 360));
        Assert.Equal(new ImagePoint(640, 360), small.ToImage(sx, sy));
        Assert.Equal(new ImagePoint(640, 360), big.ToImage(bx, by));

        Assert.Equal(1, ViewTransform.Fit(640, 360, 4000, 4000).Scale); // 小さい画像は 100% まで
    }

    [Fact]
    public void FitToImageScalesCoordinatesForDifferentImageSize()
    {
        var list = AnnotationSamples.All();
        var fitted = AnnotationGeometry.FitToImage(list, 1280, 720, 1920, 1080);
        var r = (RectangleAnnotation)fitted[0];
        Assert.Equal((15d, 30d, 150d, 75d), (r.X, r.Y, r.Width, r.Height));
        Assert.Equal(4, r.StrokeWidth); // 線幅は基準値のまま（描画時の Scale で調整）
        var a = (ArrowAnnotation)fitted[1];
        Assert.Equal((300d, 300d, 450d, 375d), (a.X1, a.Y1, a.X2, a.Y2));
        var t = (TextAnnotation)fitted[2];
        Assert.Equal((75d, 450d, 24d), (t.X, t.Y, t.FontSize));
        var m = (MosaicAnnotation)fitted[3];
        Assert.Equal((600d, 150d, 180d, 60d, 36), (m.X, m.Y, m.Width, m.Height, m.BlockSize));
        // 元のリストは変わらない
        Assert.Equal(10, ((RectangleAnnotation)list[0]).X);
        // 同じサイズ・基準不明はそのまま
        Assert.Equal(10, ((RectangleAnnotation)AnnotationGeometry.FitToImage(list, 1920, 1080, 1920, 1080)[0]).X);
        Assert.Equal(10, ((RectangleAnnotation)AnnotationGeometry.FitToImage(list, 0, 0, 1920, 1080)[0]).X);
    }

    [Fact]
    public void RectangleHitsOnBorderOnly()
    {
        var r = new RectangleAnnotation { X = 100, Y = 100, Width = 200, Height = 100, StrokeWidth = 4 };
        Assert.True(AnnotationGeometry.HitTest(r, new(100, 150), 3, 1, AnnotationSamples.Measure));
        Assert.True(AnnotationGeometry.HitTest(r, new(200, 203), 3, 1, AnnotationSamples.Measure));
        Assert.False(AnnotationGeometry.HitTest(r, new(200, 150), 3, 1, AnnotationSamples.Measure)); // 内側（塗りなし）
        Assert.False(AnnotationGeometry.HitTest(r, new(90, 150), 3, 1, AnnotationSamples.Measure));
    }

    [Fact]
    public void ArrowMosaicAndTextHitTest()
    {
        var a = new ArrowAnnotation { X1 = 0, Y1 = 0, X2 = 100, Y2 = 0, StrokeWidth = 4 };
        Assert.True(AnnotationGeometry.HitTest(a, new(50, 4), 3, 1, AnnotationSamples.Measure));
        Assert.False(AnnotationGeometry.HitTest(a, new(50, 20), 3, 1, AnnotationSamples.Measure));
        var m = new MosaicAnnotation { X = 10, Y = 10, Width = 50, Height = 20 };
        Assert.True(AnnotationGeometry.HitTest(m, new(30, 20), 0, 1, AnnotationSamples.Measure));
        var t = new TextAnnotation { X = 10, Y = 10, Text = "ab", FontSize = 20 };
        Assert.True(AnnotationGeometry.HitTest(t, new(45, 35), 0, 1, AnnotationSamples.Measure));  // 幅 40・高さ 30
        Assert.False(AnnotationGeometry.HitTest(t, new(55, 35), 0, 1, AnnotationSamples.Measure));
    }

    [Fact]
    public void TopmostIsLastInList()
    {
        var below = new MosaicAnnotation { X = 0, Y = 0, Width = 100, Height = 100 };
        var above = new MosaicAnnotation { X = 50, Y = 50, Width = 100, Height = 100 };
        Assert.Same(above, AnnotationGeometry.HitTestTopmost([below, above], new(75, 75), 0, 1, AnnotationSamples.Measure));
        Assert.Same(below, AnnotationGeometry.HitTestTopmost([below, above], new(25, 25), 0, 1, AnnotationSamples.Measure));
        Assert.Null(AnnotationGeometry.HitTestTopmost([below, above], new(300, 300), 0, 1, AnnotationSamples.Measure));
    }

    [Fact]
    public void DragHandleResizesAndNormalizes()
    {
        var original = new RectangleAnnotation { X = 100, Y = 100, Width = 100, Height = 50 };
        var target = (RectangleAnnotation)original.Clone();
        AnnotationGeometry.DragHandle(original, target, AnnotationHandle.BottomRight, new(250, 180), 1000, 1000);
        Assert.Equal((100d, 100d, 150d, 80d), (target.X, target.Y, target.Width, target.Height));
        // 対角を越えると反転した矩形として正規化
        AnnotationGeometry.DragHandle(original, target, AnnotationHandle.BottomRight, new(50, 60), 1000, 1000);
        Assert.Equal((50d, 60d, 50d, 40d), (target.X, target.Y, target.Width, target.Height));
        // 画像外は画像の端まで
        AnnotationGeometry.DragHandle(original, target, AnnotationHandle.TopLeft, new(-50, -50), 1000, 1000);
        Assert.Equal((0d, 0d, 200d, 150d), (target.X, target.Y, target.Width, target.Height));

        var arrow = new ArrowAnnotation { X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 };
        var at = (ArrowAnnotation)arrow.Clone();
        AnnotationGeometry.DragHandle(arrow, at, AnnotationHandle.End, new(40, 30), 100, 100);
        Assert.Equal((0d, 0d, 40d, 30d), (at.X1, at.Y1, at.X2, at.Y2));
        AnnotationGeometry.DragHandle(arrow, at, AnnotationHandle.Start, new(5, 6), 100, 100);
        Assert.Equal((5d, 6d), (at.X1, at.Y1));
    }

    [Fact]
    public void ClampOffsetKeepsShapeInsideImage()
    {
        var b = new ImageRect(10, 10, 100, 50);
        Assert.Equal((-10d, -10d), AnnotationGeometry.ClampOffset(b, -100, -100, 640, 360));
        Assert.Equal((530d, 300d), AnnotationGeometry.ClampOffset(b, 1000, 1000, 640, 360));
        Assert.Equal((5d, 5d), AnnotationGeometry.ClampOffset(b, 5, 5, 640, 360));
    }

    [Fact]
    public void ArrowHeadPointsAtEnd()
    {
        var s = AnnotationGeometry.Arrow(0, 0, 100, 0, 4);
        Assert.Equal(new ImagePoint(100, 0), s.Tip);
        var head = AnnotationGeometry.ArrowHeadLength(4); // 24
        Assert.Equal(100 - head, s.BaseLeft.X, 6);
        Assert.Equal(head / 2, Math.Abs(s.BaseLeft.Y), 6);
        Assert.True(s.ShaftEnd.X < 100 && s.ShaftEnd.X > 100 - head);
        // 太いほど矢じりが大きい
        Assert.True(AnnotationGeometry.ArrowHeadLength(6) > AnnotationGeometry.ArrowHeadLength(2));
        // 短い矢印では矢じりが全長を超えない
        var shortArrow = AnnotationGeometry.Arrow(0, 0, 10, 0, 6);
        Assert.True(shortArrow.BaseLeft.X >= 0);
    }
}

public class AnnotationSessionTests
{
    private static AnnotationSession NewSession(IEnumerable<Annotation>? initial = null) => new(initial ?? [], 640, 360);

    [Fact]
    public void AddUndoRedo()
    {
        var s = NewSession();
        s.Add(new RectangleAnnotation { X = 1, Y = 1, Width = 10, Height = 10 });
        s.Add(new ArrowAnnotation { X1 = 0, Y1 = 0, X2 = 50, Y2 = 50 });
        s.Add(new TextAnnotation { X = 5, Y = 5, Text = "abc" });
        s.Add(new MosaicAnnotation { X = 5, Y = 5, Width = 30, Height = 30 });
        Assert.Equal(4, s.Items.Count);
        Assert.Equal("モザイクの追加", s.History.UndoLabel);
        Assert.Equal(s.Items[3].Id, s.SelectedId); // 追加した注釈を選択

        Assert.True(s.Undo());
        Assert.Equal(3, s.Items.Count);
        Assert.Null(s.SelectedId);                 // 消えた注釈の選択は外れる
        Assert.True(s.Undo());
        Assert.Equal(2, s.Items.Count);
        Assert.True(s.Redo());
        Assert.Equal(3, s.Items.Count);
        Assert.IsType<TextAnnotation>(s.Items[2]);
        Assert.True(s.Redo());
        Assert.False(s.Redo());
        Assert.Equal(4, s.Items.Count);
    }

    [Fact]
    public void NewOperationClearsRedo()
    {
        var s = NewSession();
        s.Add(new RectangleAnnotation { Width = 10, Height = 10 });
        s.Undo();
        Assert.True(s.History.CanRedo);
        s.Add(new ArrowAnnotation { X2 = 20 });
        Assert.False(s.History.CanRedo);
    }

    [Fact]
    public void DeleteSelectedAndClearAreUndoable()
    {
        var s = NewSession(AnnotationSamples.All());
        var id = s.Items[1].Id;
        s.Select(id);
        Assert.True(s.RemoveSelected());
        Assert.Equal(3, s.Items.Count);
        Assert.Equal("矢印の削除", s.History.UndoLabel);
        Assert.Null(s.SelectedId);
        s.Undo();
        Assert.Equal(id, s.Items[1].Id);            // 元の位置（重なり順）に戻る

        Assert.True(s.Clear());
        Assert.Empty(s.Items);
        Assert.False(s.Clear());                    // 空なら何もしない（履歴も増えない）
        Assert.Equal("注釈の全削除", s.History.UndoLabel);
        s.Undo();
        Assert.Equal(4, s.Items.Count);
        Assert.False(s.Remove("unknown"));
    }

    [Fact]
    public void StyleChangesAreUndoable()
    {
        var s = NewSession(AnnotationSamples.All());
        var rect = s.Items[0].Id;
        var text = s.Items[2].Id;
        Assert.True(s.SetColor(rect, AnnotationColor.Black));
        Assert.True(s.SetStrokeWidth(rect, AnnotationStyle.ThickStroke));
        Assert.True(s.SetFontSize(text, 12));
        Assert.True(s.SetColor(text, AnnotationColor.Blue));
        Assert.False(s.SetColor(text, AnnotationColor.Blue));          // 同じ値は履歴に残さない
        Assert.False(s.SetStrokeWidth(text, 2));                        // 文字に線幅は無い
        Assert.False(s.SetColor(s.Items[3].Id, AnnotationColor.Red));   // モザイクに色は無い
        Assert.Equal(AnnotationColor.Black, ((RectangleAnnotation)s.Items[0]).Color);
        Assert.Equal(6, ((RectangleAnnotation)s.Items[0]).StrokeWidth);
        Assert.Equal(12, ((TextAnnotation)s.Items[2]).FontSize);

        s.Undo(); // 色
        Assert.Equal(AnnotationColor.Black, ((TextAnnotation)s.Items[2]).Color);
        s.Undo(); // 文字サイズ
        Assert.Equal(24, ((TextAnnotation)s.Items[2]).FontSize);
        s.Undo(); // 線幅
        Assert.Equal(4, ((RectangleAnnotation)s.Items[0]).StrokeWidth);
        s.Undo(); // 色
        Assert.Equal(AnnotationColor.Red, ((RectangleAnnotation)s.Items[0]).Color);
    }

    [Fact]
    public void BoldIsUndoable()
    {
        var s = NewSession([new TextAnnotation { Text = "a" }, new RectangleAnnotation { Width = 10, Height = 10 }]);
        var id = s.Items[0].Id;
        Assert.True(s.SetBold(id, true));
        Assert.False(s.SetBold(id, true));
        Assert.False(s.SetBold(s.Items[1].Id, true));   // 文字以外には無い
        Assert.True(((TextAnnotation)s.Items[0]).Bold);
        Assert.Equal("太さの変更", s.History.UndoLabel);
        s.Undo();
        Assert.False(((TextAnnotation)s.Items[0]).Bold);
    }

    [Fact]
    public void TextEditIsUndoableAndEmptyTextDeletes()
    {
        var s = NewSession([new TextAnnotation { Text = "前" }]);
        var id = s.Items[0].Id;
        Assert.True(s.SetText(id, "  後\r\n2行目  "));
        Assert.Equal("後\n2行目", ((TextAnnotation)s.Items[0]).Text);
        Assert.Equal("文字の編集", s.History.UndoLabel);
        s.Undo();
        Assert.Equal("前", ((TextAnnotation)s.Items[0]).Text);
        Assert.False(s.SetText(id, "前"));
        Assert.True(s.SetText(id, "   "));
        Assert.Empty(s.Items);
        s.Undo();
        Assert.Single(s.Items);
    }

    [Fact]
    public void MoveIsClampedAndUndoable()
    {
        var s = NewSession([new RectangleAnnotation { X = 10, Y = 10, Width = 100, Height = 50 }]);
        var id = s.Items[0].Id;
        Assert.True(s.Move(id, 5, 6, AnnotationSamples.Measure));
        var r = (RectangleAnnotation)s.Items[0];
        Assert.Equal((15d, 16d), (r.X, r.Y));
        Assert.True(s.Move(id, 10_000, 10_000, AnnotationSamples.Measure));
        r = (RectangleAnnotation)s.Items[0];
        Assert.Equal((540d, 310d), (r.X, r.Y));      // 画像（640x360）からはみ出さない
        Assert.False(s.Move(id, 5, 5, AnnotationSamples.Measure)); // もう動けない
        s.Undo();
        s.Undo();
        Assert.Equal((10d, 10d), (((RectangleAnnotation)s.Items[0]).X, ((RectangleAnnotation)s.Items[0]).Y));
    }

    [Fact]
    public void InteractiveDragRecordsOnceAndCanBeCanceled()
    {
        var s = NewSession([new ArrowAnnotation { X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 }]);
        var id = s.Items[0].Id;
        s.BeginInteractive("注釈の移動");
        for (int i = 1; i <= 5; i++)
            s.UpdateInteractive(id, a =>
            {
                var o = (ArrowAnnotation)s.InteractiveOriginal(id)!;
                ((ArrowAnnotation)a).X1 = o.X1 + i;
                ((ArrowAnnotation)a).X2 = o.X2 + i;
            });
        Assert.True(s.EndInteractive());
        Assert.Equal(1, s.History.UndoCount);        // ドラッグ中の途中経過は 1 回分
        Assert.Equal(5, ((ArrowAnnotation)s.Items[0]).X1);
        s.Undo();
        Assert.Equal(0, ((ArrowAnnotation)s.Items[0]).X1);

        // 動かさなかったドラッグは記録しない
        s.BeginInteractive("注釈の移動");
        Assert.False(s.EndInteractive());
        Assert.Equal(0, s.History.UndoCount);

        // Esc で取り消し
        s.BeginInteractive("注釈の変形");
        s.UpdateInteractive(id, a => ((ArrowAnnotation)a).X2 = 99);
        s.CancelInteractive();
        Assert.Equal(10, ((ArrowAnnotation)s.Items[0]).X2);
        Assert.False(s.IsInteractive);
    }

    [Fact]
    public void DirtyTracksSavedState()
    {
        var s = NewSession(AnnotationSamples.All());
        Assert.False(s.IsDirty);
        s.Remove(s.Items[0].Id);
        Assert.True(s.IsDirty);
        s.Undo();
        Assert.False(s.IsDirty);                     // 元に戻せば未変更
        s.SetColor(s.Items[0].Id, AnnotationColor.Blue);
        s.MarkSaved();
        Assert.False(s.IsDirty);
        s.Undo();
        Assert.True(s.IsDirty);
    }

    [Fact]
    public void InitialAnnotationsAreCopied()
    {
        var list = AnnotationSamples.All();
        var s = NewSession(list);
        s.Move(s.Items[0].Id, 5, 0, AnnotationSamples.Measure);
        Assert.Equal(10, ((RectangleAnnotation)list[0]).X);
        var snap = s.Snapshot();
        ((RectangleAnnotation)snap[0]).X = 0;
        Assert.Equal(15, ((RectangleAnnotation)s.Items[0]).X);
    }

    [Fact]
    public void RejectsInvalidImageSize() => Assert.Throws<ArgumentOutOfRangeException>(() => new AnnotationSession([], 0, 100));
}

public class MosaicProcessorTests
{
    /// <summary>1px ごとに白黒が入れ替わる市松模様（文字の細部に相当）。</summary>
    private static byte[] Checker(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte v = (byte)((x + y) % 2 == 0 ? 255 : 0);
                int i = (y * w + x) * 4;
                px[i] = px[i + 1] = px[i + 2] = v;
                px[i + 3] = 255;
            }
        return px;
    }

    [Fact]
    public void BlocksAreUniformAndOutsideIsUntouched()
    {
        int w = 64, h = 64;
        var px = Checker(w, h);
        var original = (byte[])px.Clone();
        MosaicProcessor.Apply(px, w, h, w * 4, new MosaicAnnotation { X = 8, Y = 8, Width = 32, Height = 32, BlockSize = 16 });

        // 範囲内: 16x16 のマスごとに 1 色（元の白黒の細部は残らない）
        for (int by = 8; by < 40; by += 16)
            for (int bx = 8; bx < 40; bx += 16)
            {
                int first = (by * w + bx) * 4;
                for (int y = by; y < by + 16; y++)
                    for (int x = bx; x < bx + 16; x++)
                        Assert.Equal(px[first], px[(y * w + x) * 4]);
                Assert.InRange(px[first], 120, 135); // 白黒の平均（灰色）
            }
        // 範囲外は変わらない
        Assert.Equal(original[(0 * w + 0) * 4], px[0]);
        Assert.Equal(original[(50 * w + 50) * 4], px[(50 * w + 50) * 4]);
        Assert.Equal(original[(7 * w + 20) * 4], px[(7 * w + 20) * 4]);
    }

    [Fact]
    public void BlockSizeHasMinimum()
    {
        int w = 32, h = 32;
        var px = Checker(w, h);
        // BlockSize 2 を指定しても最小 16px で処理する（弱いモザイクにしない）
        MosaicProcessor.Apply(px, w, h, w * 4, new MosaicAnnotation { X = 0, Y = 0, Width = 32, Height = 32, BlockSize = 2 });
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                Assert.Equal(px[0], px[(y * w + x) * 4]);
    }

    [Fact]
    public void RegionIsClippedToImageAndRoundedOutward()
    {
        Assert.Equal((0, 0, 50, 40), MosaicProcessor.PixelRegion(new MosaicAnnotation { X = -10, Y = -10, Width = 60, Height = 50 }, 100, 100));
        Assert.Equal((10, 10, 21, 21), MosaicProcessor.PixelRegion(new MosaicAnnotation { X = 10.4, Y = 10.6, Width = 20.2, Height = 20 }, 100, 100));
        Assert.Equal((90, 90, 10, 10), MosaicProcessor.PixelRegion(new MosaicAnnotation { X = 90, Y = 90, Width = 50, Height = 50 }, 100, 100));
        Assert.Null(MosaicProcessor.PixelRegion(new MosaicAnnotation { X = 200, Y = 200, Width = 10, Height = 10 }, 100, 100));
    }

    [Fact]
    public void ApplyAllIgnoresOtherAnnotations()
    {
        int w = 16, h = 16;
        var px = Checker(w, h);
        var original = (byte[])px.Clone();
        MosaicProcessor.ApplyAll(px, w, h, w * 4, [new RectangleAnnotation { Width = 16, Height = 16 }]);
        Assert.Equal(original, px);
    }
}

public class AnnotatedImageFilesTests
{
    [Fact]
    public void OutputPathUsesImageOutputDirectory()
    {
        var p = new CapturePoint { OutputFilename = "001_設定を開く.png" };
        Assert.Equal(@"C:\v\images\001_設定を開く.png", AnnotatedImageFiles.OutputPath(p, @"C:\v\manual.mp4", new ExportSettings()));
        Assert.Equal(@"D:\out\001_設定を開く.png", AnnotatedImageFiles.OutputPath(p, @"C:\v\manual.mp4", new ExportSettings { ImageOutputDirectory = @"D:\out" }));
        Assert.Null(AnnotatedImageFiles.OutputPath(new CapturePoint(), @"C:\v\manual.mp4", new ExportSettings()));
    }

    [Theory]
    [InlineData("a.png", ImageFormat.Png)]
    [InlineData("a.PNG", ImageFormat.Png)]
    [InlineData("a.jpg", ImageFormat.Jpeg)]
    [InlineData("a.jpeg", ImageFormat.Jpeg)]
    [InlineData("a.bmp", null)]
    public void FormatFollowsExtension(string path, ImageFormat? expected) => Assert.Equal(expected, AnnotatedImageFiles.FormatOf(path));

    [Theory]
    [InlineData(2, 95)]
    [InlineData(4, 90)]
    [InlineData(10, 75)]
    [InlineData(31, 50)]
    public void JpegQuality(int q, int expected) => Assert.Equal(expected, AnnotatedImageFiles.JpegQualityLevel(q));

    [Fact]
    public void TempFileIsInSameFolderAndNotASequencedImage()
    {
        var temp = AnnotatedImageFiles.TempPathFor(@"C:\out\001_設定.png");
        Assert.Equal(@"C:\out", Path.GetDirectoryName(temp));
        Assert.StartsWith("~mvs-", Path.GetFileName(temp));
        Assert.EndsWith(".tmp", temp);
        // 古い画像の検出（連番画像のパターン）に一時ファイルが混ざらない
        var dir = Path.Combine(Path.GetTempPath(), "mvs-temp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, Path.GetFileName(temp)), "");
            Assert.Empty(ImageExportService.Plan([], dir, ImageFormat.Png).StaleFiles);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class ProjectAnnotationTests
{
    [Fact]
    public void SetAnnotationsStoresRoundedCopyWithImageSize()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        ed.AddCapture(5);
        var list = new List<Annotation> { new RectangleAnnotation { X = 1.26, Y = 2, Width = 3, Height = 4 } };
        Assert.True(ed.SetAnnotations(1, list, 1920, 1080).Success);
        var p = ed.Project.CapturePoints[0];
        Assert.Equal(1.3, ((RectangleAnnotation)p.Annotations[0]).X);
        Assert.Equal(1.26, ((RectangleAnnotation)list[0]).X); // 渡したリストは変えない
        Assert.Equal((1920, 1080), (p.AnnotationImageWidth, p.AnnotationImageHeight));
        Assert.True(ed.IsDirty);

        Assert.True(ed.SetAnnotations(1, [], 1920, 1080).Success);
        Assert.Equal((0, 0), (p.AnnotationImageWidth, p.AnnotationImageHeight));
        Assert.False(ed.SetAnnotations(99, [], 1, 1).Success);
    }

    [Fact]
    public void MainUndoDoesNotRevertSavedAnnotations()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        ed.AddCapture(5);
        ed.SetCaption(1, "キャプション");
        ed.SetAnnotations(1, AnnotationSamples.All(), 1920, 1080);   // 画像ファイルは上書き済みの想定
        ed.Undo();                                                   // キャプションの変更を戻す
        var p = ed.Project.CapturePoints.Single();
        Assert.Equal("", p.Caption);
        Assert.Equal(4, p.Annotations.Count);                        // 注釈は画像ファイルと一致したまま
        Assert.Equal(1920, p.AnnotationImageWidth);
        ed.Redo();
        Assert.Equal(4, ed.Project.CapturePoints.Single().Annotations.Count);
    }

    [Fact]
    public void MetadataJsonHasAnnotationCount()
    {
        var p = new CapturePoint { Id = 1, Sequence = 1, Caption = "a", Annotations = AnnotationSamples.All() };
        var plan = ImageExportService.Plan([p], @"C:\nonexistent-dir-mvs", ImageFormat.Png);
        var json = ImageExportService.BuildMetadataJson(@"C:\v\manual.mp4", plan, null);
        Assert.Contains("\"annotationCount\": 4", json);
    }
}

/// <summary>画像書き出し時の注釈の焼き込み（ffmpeg と描画は偽物で確認）。</summary>
public sealed class ImageExportAnnotationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-export-" + Guid.NewGuid().ToString("N"));

    public ImageExportAnnotationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private sealed class FakeFrames : IFrameExtractionService
    {
        public List<(double Time, string Path, ImageFormat Format)> Calls { get; } = [];

        public Task ExtractAsync(string ffmpegPath, string videoPath, double time, string outputPath, ImageFormat format, int jpegQuality, int maxWidth, CancellationToken ct)
        {
            Calls.Add((time, outputPath, format));
            File.WriteAllText(outputPath, "frame");
            return Task.CompletedTask;
        }

        public Task<string> GetThumbnailAsync(string ffmpegPath, string videoPath, double time, int width, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> GetFullFrameAsync(string ffmpegPath, string videoPath, double time, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeRenderer : IAnnotatedImageRenderer
    {
        public List<(string Base, int Count, int W, int H, string Output, ImageFormat Format)> Calls { get; } = [];

        public Task RenderToFileAsync(string baseImagePath, IReadOnlyList<Annotation> annotations, int annotationWidth, int annotationHeight,
            string outputPath, ImageFormat format, int jpegQuality, CancellationToken ct)
        {
            Assert.True(File.Exists(baseImagePath));
            Calls.Add((baseImagePath, annotations.Count, annotationWidth, annotationHeight, outputPath, format));
            File.WriteAllText(outputPath, "annotated");
            return Task.CompletedTask;
        }
    }

    private static List<CapturePoint> Points() =>
    [
        new CapturePoint { Id = 1, Sequence = 1, SelectedFrameTime = 1, Caption = "注釈なし" },
        new CapturePoint { Id = 2, Sequence = 2, SelectedFrameTime = 2, Caption = "注釈あり", Annotations = AnnotationSamples.All(), AnnotationImageWidth = 1280, AnnotationImageHeight = 720 },
    ];

    [Fact]
    public async Task AnnotatedPointsAreRenderedFromLosslessFrame()
    {
        var frames = new FakeFrames();
        var renderer = new FakeRenderer();
        var plan = ImageExportService.Plan(Points(), _dir, ImageFormat.Jpeg);
        var result = await new ImageExportService(frames, renderer).ExportAsync("ffmpeg.exe", "v.mp4", plan, true, 2, false, null, null, default);

        Assert.Equal(2, result.Written);
        Assert.Equal("frame", File.ReadAllText(Path.Combine(_dir, "001_注釈なし.jpg")));
        Assert.Equal("annotated", File.ReadAllText(Path.Combine(_dir, "002_注釈あり.jpg")));
        var call = Assert.Single(renderer.Calls);
        Assert.Equal((4, 1280, 720, ImageFormat.Jpeg), (call.Count, call.W, call.H, call.Format));
        Assert.Equal(Path.Combine(_dir, "002_注釈あり.jpg"), call.Output);
        // 注釈用の元画像は PNG（可逆）で出力フォルダーの外に切り出し、終了後に消す
        var baseCall = frames.Calls.Single(c => c.Time == 2);
        Assert.Equal(ImageFormat.Png, baseCall.Format);
        Assert.NotEqual(_dir, Path.GetDirectoryName(baseCall.Path));
        Assert.False(File.Exists(baseCall.Path));
        Assert.Equal(2, Directory.GetFiles(_dir).Length);
    }

    [Fact]
    public async Task AnnotationsWithoutRendererFailInsteadOfLeakingMosaicArea()
    {
        var plan = ImageExportService.Plan(Points(), _dir, ImageFormat.Png);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ImageExportService(new FakeFrames()).ExportAsync("ffmpeg.exe", "v.mp4", plan, true, 2, false, null, null, default));
        Assert.False(File.Exists(Path.Combine(_dir, "002_注釈あり.png")));
    }
}

public sealed class ProjectAnnotationPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-annproj-" + Guid.NewGuid().ToString("N"));

    public ProjectAnnotationPersistenceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void SaveAndLoadKeepsAnnotations()
    {
        var project = TestProjects.Create();
        var ed = new ProjectEditor(project);
        ed.AddCapture(3);
        ed.SetCaption(1, "設定を開く");
        ed.SetAnnotations(1, AnnotationSamples.All(), 1920, 1080);
        var path = Path.Combine(_dir, "a.mvsproj.json");
        var svc = new ProjectService();
        svc.Save(project, path);

        var text = File.ReadAllText(path);
        Assert.Contains($"\"schemaVersion\": {ProjectDocument.CurrentSchemaVersion}", text);
        Assert.Contains("\"annotations\": [", text);
        Assert.Contains("\"annotationImageWidth\": 1920", text);

        var p = svc.Load(path).Project.CapturePoints.Single();
        Assert.Equal(4, p.Annotations.Count);
        Assert.Equal([AnnotationKind.Rectangle, AnnotationKind.Arrow, AnnotationKind.Text, AnnotationKind.Mosaic], p.Annotations.Select(a => a.Kind));
        Assert.Equal("ここをクリック", ((TextAnnotation)p.Annotations[2]).Text);
        Assert.Equal((1920, 1080), (p.AnnotationImageWidth, p.AnnotationImageHeight));
        Assert.Equal("設定を開く", p.Caption);
    }

    [Fact]
    public void LoadsVersion1ProjectWithoutAnnotations()
    {
        // 注釈機能の追加前（v1.0 / schemaVersion 1）の形式
        const string v1 = """
        {
          "schemaVersion": 1,
          "appVersion": "1.0.0",
          "sourceVideo": { "path": "C:/nothing/manual.mp4" },
          "capturePoints": [
            { "id": 1, "sequence": 1, "timestamp": 2.2, "selectedFrameTime": 2.2, "caption": "設定を開く", "captionSource": "ai", "outputFilename": "001_設定を開く.png" }
          ],
          "nextCaptureId": 2
        }
        """;
        var path = Path.Combine(_dir, "old.mvsproj.json");
        File.WriteAllText(path, v1);
        var p = new ProjectService().Load(path).Project;
        var c = p.CapturePoints.Single();
        Assert.Empty(c.Annotations);
        Assert.Equal((0, 0), (c.AnnotationImageWidth, c.AnnotationImageHeight));
        Assert.Equal("001_設定を開く.png", c.OutputFilename);
        Assert.Equal(1, p.SchemaVersion);            // 読み込み時は変えない（保存時に 2 になる）
    }

    [Fact]
    public void NullAnnotationsAreRepaired()
    {
        const string json = """
        { "schemaVersion": 2, "capturePoints": [ { "id": 1, "annotations": null, "annotationImageWidth": 100, "annotationImageHeight": 0 } ] }
        """;
        var path = Path.Combine(_dir, "null.mvsproj.json");
        File.WriteAllText(path, json);
        var c = new ProjectService().Load(path).Project.CapturePoints.Single();
        Assert.NotNull(c.Annotations);
        Assert.Empty(c.Annotations);
        Assert.Equal((0, 0), (c.AnnotationImageWidth, c.AnnotationImageHeight));
    }

    [Fact]
    public void NewerSchemaIsRejected()
    {
        var path = Path.Combine(_dir, "new.mvsproj.json");
        File.WriteAllText(path, $$"""{ "schemaVersion": {{ProjectDocument.CurrentSchemaVersion + 1}} }""");
        Assert.Throws<InvalidOperationException>(() => new ProjectService().Load(path));
    }
}

public class AnnotationJsonOrderTests
{
    [Fact]
    public void KeysAreInReadableOrder()
    {
        var json = JsonSerializer.Serialize(AnnotationSamples.All(), JsonDefaults.Options);
        using var doc = JsonDocument.Parse(json);
        string Keys(int i) => string.Join(",", doc.RootElement[i].EnumerateObject().Select(p => p.Name));
        Assert.Equal("type,id,x,y,width,height,color,strokeWidth", Keys(0));
        Assert.Equal("type,id,x1,y1,x2,y2,color,strokeWidth", Keys(1));
        Assert.Equal("type,id,x,y,text,fontFamily,fontSize,bold,color", Keys(2));
        Assert.Equal("type,id,x,y,width,height,blockSize", Keys(3));
    }
}
