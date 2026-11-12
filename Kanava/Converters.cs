using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Kanava;

/// <summary>bool → Visibility. ConverterParameter="!" kääntää.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is true;
        if (parameter as string == "!") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>null/tyhjä → Collapsed. ConverterParameter="!" kääntää.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value is not null && !(value is string s && string.IsNullOrWhiteSpace(s));
        if (parameter as string == "!") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Vertaa arvoa parametriin (esim. enum) → bool tai Visibility.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var eq = value is not null && parameter is not null &&
                 string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
        if (targetType == typeof(Visibility)) return eq ? Visibility.Visible : Visibility.Collapsed;
        return eq;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null) return Binding.DoNothing;
        if (targetType.IsEnum) return Enum.Parse(targetType, parameter.ToString()!);
        if (targetType == typeof(int)) return int.Parse(parameter.ToString()!, CultureInfo.InvariantCulture);
        return parameter;
    }
}

/// <summary>Korkeus → 16:9-leveys.</summary>
public sealed class AspectWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double h && h > 0 ? h * 16 / 9 : 0d;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Suosikkitila → sydänikoni (täysi / ääriviiva).</summary>
public sealed class HeartGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "" : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>0..1 → leveys (ConverterParameter = kokonaisleveys tai MultiBinding).</summary>
public sealed class ProgressWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double p || values[1] is not double w) return 0d;
        return Math.Max(0, Math.Min(1, p)) * w;
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
