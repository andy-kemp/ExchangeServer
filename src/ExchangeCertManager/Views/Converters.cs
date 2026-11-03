using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ExchangeCertManager.Views;

public sealed class LogToggleText : IValueConverter
{
    public static LogToggleText Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? "Hide log" : "Show log";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBool : IValueConverter
{
    public static InverseBool Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Visible when the bound value is true / non-empty / non-null.</summary>
public sealed class VisibleWhen : IValueConverter
{
    public static VisibleWhen Instance { get; } = new();
    public static VisibleWhen Not { get; } = new() { Invert = true };
    public bool Invert { get; init; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i > 0,
            _ => true,
        };
        if (Invert) visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class DateText : IValueConverter
{
    public static DateText Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DateTime d ? d.ToString("dd MMM yyyy") : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
