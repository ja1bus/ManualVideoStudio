namespace ManualVideoStudio.Services;

/// <summary>
/// 取扱説明書（v1.17.0 で Web 版に一本化）。［ヘルプ］→［取扱説明書］と F1 は Web 版を既定のブラウザーで開く。
/// exe に説明書は埋め込まない（同梱版は廃止）。保存用 HTML は Web には掲載しない。
/// </summary>
public static class HelpManual
{
    /// <summary>Web 版の取扱説明書（常に最新版の説明）。</summary>
    public const string WebUrl = "https://www.ja1bus.com/manualvideostudio/manual/";
}
