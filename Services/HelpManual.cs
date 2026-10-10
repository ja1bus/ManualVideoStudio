using System.IO;

namespace ManualVideoStudio.Services;

/// <summary>
/// 取扱説明書（HTML）。exe に埋め込んだ manual.html を %LOCALAPPDATA%\ManualVideoStudio\help に書き出して既定のブラウザーで開く。
/// 毎回上書きするため、常に起動中のバージョンの説明書になる。
/// </summary>
public static class HelpManual
{
    public const string ResourceName = "ManualVideoStudio.Help.manual.html";
    public const string FileName = "manual.html";

    /// <summary>説明書を書き出し、そのパスを返す。</summary>
    public static string Extract(string localDirectory)
    {
        using var stream = typeof(HelpManual).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("取扱説明書が exe に含まれていません。");
        var dir = Path.Combine(localDirectory, "help");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FileName);
        using (var file = File.Create(path)) stream.CopyTo(file);
        return path;
    }
}
