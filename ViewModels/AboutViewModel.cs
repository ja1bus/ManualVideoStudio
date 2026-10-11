using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Services;

namespace ManualVideoStudio.ViewModels;

/// <summary>
/// バージョン情報の画面（v1.17.0）。版と <c>© ja1bus</c>、MIT License の説明、公開リポジトリ、同梱した LICENSE の全文（アプリ内で表示）、
/// 第三者のソフトウェアの表記（メモ帳で開く）。読み込み・起動に失敗したらフルパスを示す。
/// </summary>
public sealed partial class AboutViewModel(IDialogService dialogs)
{
    public string VersionText => $"Manual Video Studio v{ProjectService.AppVersion}　© ja1bus";
    public string LicenseText => Loc.Get("About.License");
    public string RepositoryUrl => LicenseFiles.RepositoryUrl;

    [RelayCommand]
    private void ShowLicense()
    {
        var text = LicenseFiles.Read(LicenseFiles.LicenseName, out var problem);
        if (text is null) dialogs.ShowError(Loc.Get("About.LicenseUnreadable"), problem);
        else dialogs.ShowInfo(text, Loc.Get("About.LicenseTitle"));
    }

    [RelayCommand]
    private void OpenNotices()
    {
        if (LicenseFiles.OpenInNotepad(LicenseFiles.NoticesName) is { } problem)
            dialogs.ShowError(Loc.Get("About.NoticesUnreadable"), problem);
    }

    [RelayCommand]
    private void OpenRepository()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LicenseFiles.RepositoryUrl) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            dialogs.ShowError(Loc.Get("About.RepositoryFailed") + Environment.NewLine + LicenseFiles.RepositoryUrl, ex.Message);
        }
    }
}
