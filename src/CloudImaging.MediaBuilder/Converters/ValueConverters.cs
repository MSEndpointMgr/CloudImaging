using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace CloudImaging.MediaBuilder.Converters;

/// <summary>Converts bool to a Brush: true → error red, false → muted gray.</summary>
public sealed class BoolToErrorBrushConverter : IValueConverter
{
    /// <summary>Returns error red for <c>true</c>, muted gray for anything else.</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 38, 38))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139));

    /// <summary>Not supported; this converter is one-way.</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>Converts bool to Visibility (true=Visible, false=Collapsed).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>Returns <see cref="System.Windows.Visibility.Visible"/> for <c>true</c>, <see cref="System.Windows.Visibility.Collapsed"/> otherwise.</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    /// <summary>Not supported; this converter is one-way.</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>Converts bool to Visibility inverted (true=Collapsed, false=Visible).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    /// <summary>Returns <see cref="System.Windows.Visibility.Collapsed"/> for <c>true</c>, <see cref="System.Windows.Visibility.Visible"/> otherwise.</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    /// <summary>Not supported; this converter is one-way.</summary>
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
    /// <summary>Returns -1 (mirrored) for <c>true</c>, 1 (normal) otherwise.</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? -1.0 : 1.0;

    /// <summary>Not supported; this converter is one-way.</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
