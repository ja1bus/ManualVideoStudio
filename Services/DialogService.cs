using System.Windows;
using Microsoft.Win32;

namespace ManualVideoStudio.Services;

/// <summary>ViewModel からのメッセージ・ファイル選択。テスト時に差し替えられるようインターフェース化する。</summary>
public interface IDialogService
{
    void ShowInfo(string message, string title = "情報");
    void ShowWarning(string message, string title = "注意");
    /// <summary>利用者向けの要約と、開発者向けの詳細（先頭数行のみ表示、全文はログ）。</summary>
    void ShowError(string message, string? detail = null, string title = "エラー");
    bool Confirm(string message, string title = "確認");
    /// <summary>はい=true / いいえ=false / キャンセル=null。</summary>
    bool? ConfirmYesNoCancel(string message, string title = "確認");
    string? OpenFile(string filter, string? initialDirectory = null, string title = "ファイルを開く");
    string? SaveFile(string filter, string defaultFileName, string? initialDirectory = null, string title = "名前を付けて保存");
    string? PickFolder(string? initialDirectory = null, string title = "フォルダーの選択");
}

public sealed class DialogService(LogLocation logLocation) : IDialogService
{
    private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    public void ShowInfo(string message, string title = "情報") => Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowWarning(string message, string title = "注意") => Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void ShowError(string message, string? detail = null, string title = "エラー")
    {
        var text = message;
        if (!string.IsNullOrWhiteSpace(detail))
        {
            // 詳細が長いとダイアログが画面からはみ出して閉じられなくなるため、末尾数行だけ表示する。
            var lines = detail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(6)
                .Select(l => l.Length > 160 ? l[..160] + "…" : l);
            text += Environment.NewLine + Environment.NewLine + "詳細:" + Environment.NewLine + string.Join(Environment.NewLine, lines);
        }
        text += Environment.NewLine + Environment.NewLine + "ログ: " + logLocation.Directory;
        Show(text, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public bool Confirm(string message, string title = "確認")
        => Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    public bool? ConfirmYesNoCancel(string message, string title = "確認")
        => Show(message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };

    public string? OpenFile(string filter, string? initialDirectory = null, string title = "ファイルを開く")
    {
        var dlg = new OpenFileDialog { Filter = filter, Title = title, CheckFileExists = true };
        if (IsDir(initialDirectory)) dlg.InitialDirectory = initialDirectory;
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public string? SaveFile(string filter, string defaultFileName, string? initialDirectory = null, string title = "名前を付けて保存")
    {
        var dlg = new SaveFileDialog { Filter = filter, Title = title, FileName = defaultFileName, OverwritePrompt = true, AddExtension = true };
        if (IsDir(initialDirectory)) dlg.InitialDirectory = initialDirectory;
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public string? PickFolder(string? initialDirectory = null, string title = "フォルダーの選択")
    {
        var dlg = new OpenFolderDialog { Title = title };
        if (IsDir(initialDirectory)) dlg.InitialDirectory = initialDirectory;
        return dlg.ShowDialog(Owner) == true ? dlg.FolderName : null;
    }

    private static bool IsDir(string? path) => !string.IsNullOrWhiteSpace(path) && System.IO.Directory.Exists(path);

    private static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        var owner = Owner;
        return owner is null
            ? MessageBox.Show(message, title, buttons, image)
            : MessageBox.Show(owner, message, title, buttons, image);
    }
}

/// <summary>ログフォルダの場所（エラー表示で案内する）。</summary>
public sealed record LogLocation(string Directory);
