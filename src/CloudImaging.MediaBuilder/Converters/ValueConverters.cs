using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace CloudImaging.MediaBuilder.Converters;

/// <summary>Converts bool to a Brush: true → error red, false → muted gray.</summary>
public sealed class BoolToErrorBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 38, 38))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>Converts bool to Visibility (true=Visible, false=Collapsed).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>Converts bool to Visibility inverted (true=Collapsed, false=Visible).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>
/// Converts bool to a horizontal ScaleTransform factor: true → -1 (mirrored), false → 1
/// (normal). Applied to an indeterminate ProgressRing's RenderTransform, this is a cheap way
/// to make its spin animation read as running in the opposite direction (a horizontal mirror
/// of a rotating arc rotates the opposite way) without needing a custom spinner template.
/// </summary>
public sealed class BoolToScaleXConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? -1.0 : 1.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
