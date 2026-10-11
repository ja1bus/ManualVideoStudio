namespace ManualVideoStudio.Core.Export;

/// <summary>
/// 書き出しのどの段階で、どのパスが原因で失敗したかを示す例外（v1.17.0）。
/// 保存先の作成・書き込み、入力動画の読み取り、一時ファイルから完成名への移動、生成後の存在確認などで使う。
/// フルパスを必ず含め、暗号化されたファイル・振り替えられた保存先などの原因をたどれるようにする。
/// </summary>
public sealed class PathStageException(string stage, string path, string reason, Exception? inner = null)
    : IOException($"{stage}に失敗しました。{Environment.NewLine}{path}{Environment.NewLine}{reason}", inner)
{
    /// <summary>失敗した段階（例: 「保存先フォルダーの作成」）。</summary>
    public string Stage { get; } = stage;

    /// <summary>対象のフルパス。</summary>
    public string FilePath { get; } = path;

    /// <summary>理由（OS のメッセージなど）。</summary>
    public string Reason { get; } = reason;
}
