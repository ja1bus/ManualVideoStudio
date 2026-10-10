using System.Globalization;
using System.Resources;

namespace ManualVideoStudio;

/// <summary>
/// 画面に出す文言の取得・書式化（v1.13）。文言は Resources/Strings.resx（日本語。既定の言語）に持ち、キーで取り出す。
/// 将来の英語対応では、同じキーの Strings.en.resx を足し、表示言語を決める処理を加える（今回は日本語だけ。表示言語の設定・OSによる切り替えはない）。
/// 文章は断片を連結せず、書式付き文字列（{0} など）で 1 つの文として持つ（語順が違う言語でも翻訳できるように）。
/// </summary>
public static class Loc
{
    private static readonly ResourceManager Manager = new("ManualVideoStudio.Resources.Strings", typeof(Loc).Assembly);

    /// <summary>キーの文言。キーが無ければ、見つけやすいよう「!キー!」を返す（テストが全キーの存在を確かめる）。</summary>
    public static string Get(string key) => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? $"!{key}!";

    /// <summary>書式付きの文言。数値・日時は現在のカルチャーで整形する。</summary>
    public static string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
