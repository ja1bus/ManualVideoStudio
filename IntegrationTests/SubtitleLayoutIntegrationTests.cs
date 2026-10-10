using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.IntegrationTests;

/// <summary>
/// 字幕の配置・折り返し・帯（v1.5）を実 ffmpeg（libass）で確認する。確認画像（SubtitleSampleRenderer）を灰色の画面（0x606060）に描いて画素を調べる。
/// </summary>
public sealed class SubtitleLayoutIntegrationTests(MediaFixture fx) : IClassFixture<MediaFixture>
{
    private string Ffmpeg => fx.Location.Ffmpeg.Path!;

    private sealed record Pixels(int Width, int Height, byte[] Bgra)
    {
        public int Brightness(int x, int y)
        {
            int i = (y * Width + x) * 4;
            return (Bgra[i] + Bgra[i + 1] + Bgra[i + 2]) / 3;
        }
    }

    private async Task<(Pixels Image, SubtitleSampleResult Result)> RenderAsync(SubtitleSegment seg, SubtitleStyle style, string? video = null, double time = 0,
        int width = 1280, int height = 720)
    {
        // 確認画像と同じく拡張子なしの出力（中身は PNG）。
        var output = Path.Combine(fx.Dir, $"sample-{Guid.NewGuid():N}");
        var r = await new SubtitleSampleRenderer(fx.Runner).RenderAsync(Ffmpeg, video, time, width, height, seg, style, null, output, CancellationToken.None);
        Assert.True(r.Succeeded, r.Error);
        using var stream = File.OpenRead(output);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var bmp = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var px = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
        bmp.CopyPixels(px, bmp.PixelWidth * 4, 0);
        return (new Pixels(bmp.PixelWidth, bmp.PixelHeight, px), r);
    }

    /// <summary>条件に合う画素がある行の範囲（上端・下端）。無ければ (-1, -1)。</summary>
    private static (int Top, int Bottom) Rows(Pixels p, Func<int, bool> match)
    {
        int top = -1, bottom = -1;
        for (int y = 0; y < p.Height; y++)
            for (int x = 0; x < p.Width; x++)
                if (match(p.Brightness(x, y)))
                {
                    if (top < 0) top = y;
                    bottom = y;
                    break;
                }
        return (top, bottom);
    }

    [Fact]
    public async Task LongJapaneseSubtitleIsWrappedInsideTheFrame()
    {
        // libass（gyan.dev 9.0.1）は空白の無い日本語を折り返さないため、アプリで改行を入れる。画面の左右の端に文字がはみ出さないこと。
        var text = "ここでは設定画面を開いて、ネットワークの項目からIPアドレスを入力し、Wi-Fiの接続先を選んでから保存ボタンを押してください。";
        var (p, _) = await RenderAsync(new SubtitleSegment { Text = text }, new SubtitleStyle { Preset = SubtitlePreset.Outline });
        int edge = 0;
        for (int y = 0; y < p.Height; y++)
            for (int x = 0; x < 12; x++)
                if (p.Brightness(x, y) > 200 || p.Brightness(p.Width - 1 - x, y) > 200) edge++;
        Assert.Equal(0, edge);
        var (top, bottom) = Rows(p, b => b > 200);
        // 文字サイズ 32px（48 × 720 / 1080）。1 行なら字の高さは 32px 未満、2 行なら行送り（libass はおよそ文字サイズ）ぶん高くなる。
        Assert.True(bottom - top > 32 * 1.3, $"2 行以上になっていない（文字の高さ {bottom - top}px）");
    }

    [Fact]
    public async Task TranslucentBandIsOneBoxWithoutDarkOverlap()
    {
        // 半透明の帯で 2 行: BorderStyle 4 は 1 枚の箱なので、行の重なりで濃くなる部分（灰 96 × 0.38² ≒ 14）が無い。
        var (p, _) = await RenderAsync(new SubtitleSegment { Text = "1 行目の字幕です\n2 行目の字幕です", FontSize = 72 },
            new SubtitleStyle { Preset = SubtitlePreset.TranslucentBand });
        int band = 0, overlap = 0;
        for (int y = 0; y < p.Height; y++)
            for (int x = 0; x < p.Width; x++)
            {
                var b = p.Brightness(x, y);
                if (b is >= 30 and <= 44) band++;
                else if (b < 24) overlap++;
            }
        Assert.True(band > 5000, $"帯の画素 {band}");
        Assert.True(overlap < 50, $"濃くなった画素 {overlap}");
    }

    [Theory]
    [InlineData(SubtitlePosition.Bottom)]
    [InlineData(SubtitlePosition.Middle)]
    [InlineData(SubtitlePosition.Top)]
    public async Task VerticalOffsetMovesTheSubtitle(SubtitlePosition position)
    {
        // 余白を 15%（720 の画面で 108px）にして、上へ 4 段ずらしても画面の端で止まらないようにする。
        var style = new SubtitleStyle { Preset = SubtitlePreset.Outline, MarginVertical = 162 };
        var (a, _) = await RenderAsync(new SubtitleSegment { Text = "位置の確認", Position = position }, style);
        var (b, _) = await RenderAsync(new SubtitleSegment { Text = "位置の確認", Position = position, VerticalOffset = SubtitleStyle.OffsetStep * 4 }, style);
        var ta = Rows(a, v => v > 200).Top;
        var tb = Rows(b, v => v > 200).Top;
        // 4 段 = 108px（1080 換算）→ 720 の画面では 72px 上へ。
        Assert.InRange(ta - tb, 68, 76);
    }

    [Fact]
    public async Task MarginSettingMovesBottomSubtitles()
    {
        var (a, _) = await RenderAsync(new SubtitleSegment { Text = "余白" }, new SubtitleStyle { Preset = SubtitlePreset.Outline, MarginVertical = 0 });
        var (b, _) = await RenderAsync(new SubtitleSegment { Text = "余白" }, new SubtitleStyle { Preset = SubtitlePreset.Outline, MarginVertical = 108 });
        Assert.InRange(Rows(a, v => v > 200).Bottom - Rows(b, v => v > 200).Bottom, 68, 76);
    }

    [Fact]
    public async Task SampleOnVideoFrameReportsSelectedFont()
    {
        // テスト動画の 4 秒は紺一色。動画の大きさで描き、libass が選んだフォントを返す。
        var (p, r) = await RenderAsync(new SubtitleSegment { Text = "確認 ABC", FontSize = 96 }, new SubtitleStyle(), fx.VideoPath, 4,
            fx.Metadata.Width, fx.Metadata.Height);
        Assert.Equal((fx.Metadata.Width, fx.Metadata.Height), (p.Width, p.Height));
        Assert.Equal("Meiryo", r.SelectedFont);
        Assert.True(SubtitleSampleRenderer.IsExpectedFont("Meiryo", r.SelectedFont!, null));
    }
}
