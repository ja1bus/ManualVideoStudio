using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace ManualVideoStudio.Services;

public interface IClipboardService
{
    /// <summary>テキストをコピーする。他アプリがクリップボードを使用中の場合は数回再試行する。</summary>
    bool SetText(string text);

    /// <summary>クリップボードの文字列を読む（v1.12.1）。文字列が無い・読めないときは false。他アプリが使用中なら数回再試行する。</summary>
    bool TryGetText(out string text);
}

public sealed class ClipboardService : IClipboardService
{
    public bool TryGetText(out string text)
    {
        text = "";
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (!Clipboard.ContainsText()) return false;
                text = Clipboard.GetText();
                return text.Length > 0;
            }
            catch (COMException) { Thread.Sleep(80); }
            catch (ExternalException) { Thread.Sleep(80); }
        }
        return false;
    }

    public bool SetText(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(80);
            }
            catch (ExternalException)
            {
                Thread.Sleep(80);
            }
        }
        return false;
    }
}

/// <summary>エクスプローラー・既定ブラウザの起動（ブラウザの自動操作はしない）。</summary>
public interface IShellService
{
    void OpenFolder(string folder);
    void RevealFile(string file);
    void OpenUrl(string url);
}

public sealed class ShellService : IShellService
{
    public void OpenFolder(string folder)
    {
        if (System.IO.Directory.Exists(folder)) Start("explorer.exe", [folder]);
    }

    public void RevealFile(string file)
    {
        if (System.IO.File.Exists(file)) Start("explorer.exe", ["/select," + file]);
        else OpenFolder(System.IO.Path.GetDirectoryName(file) ?? "");
    }

    public void OpenUrl(string url)
        => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

    private static void Start(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
    }
}
