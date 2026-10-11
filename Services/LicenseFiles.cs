using System.Diagnostics;
using System.IO;

namespace ManualVideoStudio.Services;

/// <summary>
/// 同梱したライセンス表記（v1.17.0）。インストール先（Inno Setup 版）・パッケージ（Store 版）の exe と同じフォルダーにある
/// <c>LICENSE</c>（MIT License の全文）と <c>THIRD-PARTY-NOTICES.md</c>（第三者のソフトウェアの表記）を、アプリ内から確かめられるようにする。
/// 拡張子の無い <c>LICENSE</c> は、暗号化される拡張子（.txt など）を避けるため。読み込みに失敗したらフルパスを示す。
/// </summary>
public static class LicenseFiles
{
    public const string RepositoryUrl = "https://github.com/ja1bus/ManualVideoStudio";
    public const string LicenseName = "LICENSE";
    public const string NoticesName = "THIRD-PARTY-NOTICES.md";

    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, name);

    /// <summary>ファイルの内容。読めなければ null と、フルパスを含む理由。</summary>
    public static string? Read(string name, out string problem)
    {
        var path = PathOf(name);
        problem = "";
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"{path}{Environment.NewLine}{ex.Message}";
            return null;
        }
    }

    /// <summary>メモ帳で開く（.md には関連付けが無いことがあるため。暗号化された場合に備えて、失敗時はフルパスを示す）。</summary>
    public static string? OpenInNotepad(string name)
    {
        var path = PathOf(name);
        if (!File.Exists(path)) return $"{path}{Environment.NewLine}ファイルが見つかりません。";
        try
        {
            var psi = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
            psi.ArgumentList.Add(path);
            Process.Start(psi)?.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"{path}{Environment.NewLine}{ex.Message}";
        }
    }
}
