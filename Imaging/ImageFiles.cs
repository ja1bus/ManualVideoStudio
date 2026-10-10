using System.IO;
using System.Windows.Media.Imaging;
using ManualVideoStudio.Core.Annotations;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Imaging;

/// <summary>画像ファイルの読み込み（ファイルをロックしない）と、一時ファイル経由の安全な上書き保存。</summary>
public static class ImageFiles
{
    /// <summary>画像を読み込む。読み込み後はファイルを開いたままにしない（上書き保存・削除を妨げない）。</summary>
    public static BitmapSource Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    /// <summary>
    /// 画像を path へ保存する。形式は format（元の拡張子の形式を渡す）。
    /// 1. 同じフォルダーの一時ファイルへエンコード 2. 読み直して寸法を確認 3. 元ファイルを置き換え。失敗時は一時ファイルを消し、元ファイルは変えない。
    /// バックアップは作らない。
    /// </summary>
    public static void SaveAtomic(BitmapSource image, string path, ImageFormat format, int jpegQualityLevel = 95)
    {
        var temp = AnnotatedImageFiles.TempPathFor(path);
        try
        {
            BitmapEncoder encoder = format == ImageFormat.Jpeg
                ? new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQualityLevel, 1, 100) }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                encoder.Save(fs);
                fs.Flush(flushToDisk: true);
            }

            var check = Load(temp);
            if (check.PixelWidth != image.PixelWidth || check.PixelHeight != image.PixelHeight)
                throw new IOException("保存した画像を正しく読み戻せませんでした。");

            // 同じボリューム内の置き換え（MoveFileEx REPLACE_EXISTING）。途中で失敗しても元ファイルは壊れない。
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
