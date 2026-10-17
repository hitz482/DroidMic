using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DroidMic.Desktop.Views;

/// <summary>Maps a 0..100 level to the X scale of the cover drawn over the meter gradient.</summary>
public sealed class LevelToCoverScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? Math.Clamp((100 - d) / 100, 0, 1) : 1.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Shows a section when SelectedSection equals the converter parameter.</summary>
public sealed class SectionVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i && int.TryParse(parameter as string, out int want) && i == want ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
