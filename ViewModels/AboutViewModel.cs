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
    /// <summary>画面の見出し（アプリ名）。</summary>
    public string AppName => "Manual Video Studio";
    public string VersionText => $"Version {ProjectService.AppVersion}";
    public string CopyrightText => "© ja1bus";
    /// <summary>Web サイト（取扱説明書・プライバシーポリシーの親）。</summary>
    public const string SiteUrl = "https://www.ja1bus.com/manualvideostudio/";
    /// <summary>設定ファイルの保管場所（Store 版では実際の保存先）。</summary>
    public string SettingsPath => AppPaths.Current.SettingsFile;
    public string PrivacyNote => Loc.Get("About.PrivacyNote");
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
    private void OpenRepository() => OpenUrl(LicenseFiles.RepositoryUrl);

    [RelayCommand]
    private void OpenWebsite() => OpenUrl(SiteUrl);

    [RelayCommand]
    private void OpenManual() => OpenUrl(HelpManual.WebUrl);

    [RelayCommand]
    private void OpenPrivacy() => OpenUrl(SiteUrl + "privacy/");

    private void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            dialogs.ShowError(Loc.Get("About.RepositoryFailed") + Environment.NewLine + url, ex.Message);
        }
    }
}
