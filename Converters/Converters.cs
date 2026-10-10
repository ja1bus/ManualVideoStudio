using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ManualVideoStudio.Converters;

/// <summary>true → Visible。ConverterParameter="Invert" で反転。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (parameter as string == "Invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>null / 空文字 → Collapsed。</summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool empty = value is null || (value is string s && s.Length == 0) || (value is int i && i == 0);
        if (parameter as string == "Invert") empty = !empty;
        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => !(value is bool b && b);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => !(value is bool b && b);
}

/// <summary>enum ⇔ bool（RadioButton 用）。ConverterParameter に enum 名。</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter as string;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is string s ? Enum.Parse(targetType, s) : Binding.DoNothing;
}

/// <summary>int ⇔ bool（RadioButton 用）。ConverterParameter に整数。</summary>
public sealed class IntEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int v && int.TryParse(parameter as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && v == p;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && int.TryParse(parameter as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : Binding.DoNothing;
}

/// <summary>SettingsTab ⇔ TabControl.SelectedIndex。</summary>
public sealed class EnumToIndexConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is Enum e ? System.Convert.ToInt32(e) : 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && parameter is Type t ? Enum.ToObject(t, i) : Binding.DoNothing;
}

/// <summary>すべての値が等しければ true（MultiBinding。文字サイズボタンの選択表示など）。</summary>
public sealed class ValuesEqualConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        => values.Length > 1 && values.Skip(1).All(v => Equals(v, values[0]));

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}
