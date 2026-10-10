using System.Windows.Markup;

namespace ManualVideoStudio;

/// <summary>XAML から文字列リソースを使う: Content="{local:Str Toolbar.Save}"。</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class StrExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}
