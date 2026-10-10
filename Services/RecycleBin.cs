using System.IO;
using System.Runtime.InteropServices;
using ManualVideoStudio.Core.Persistence;
using Microsoft.VisualBasic.FileIO;

namespace ManualVideoStudio.Services;

/// <summary>Windows のごみ箱へ移す（v1.7。プロジェクト一覧の削除）。</summary>
public sealed class RecycleBin : IRecycleBin
{
    public void MoveToRecycleBin(string path)
    {
        if (Directory.Exists(path))
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
        else if (File.Exists(path))
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
        else
            throw new FileNotFoundException("ファイル・フォルダーが見つかりません。", path);
    }
}

/// <summary>既知のフォルダー（ダウンロード）。</summary>
public static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>ダウンロードフォルダー。取得できなければ「ユーザー\Downloads」。</summary>
    public static string Downloads
    {
        get
        {
            try
            {
                if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var p) == 0 && !string.IsNullOrEmpty(p)) return p;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException) { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out string path);
}
