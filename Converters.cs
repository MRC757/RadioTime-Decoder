using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WwvDecoder.ViewModels;

namespace WwvDecoder;

public class BoolToStartStopConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "Stop Listening" : "Start Listening";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToStartStopColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true
            ? new SolidColorBrush(Color.FromRgb(243, 139, 168))  // red
            : new SolidColorBrush(Color.FromRgb(137, 180, 250)); // blue

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class LockStateToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is LockState state
            ? state switch
            {
                LockState.Locked   => new SolidColorBrush(Color.FromRgb(166, 227, 161)), // green
                LockState.Syncing  => new SolidColorBrush(Color.FromRgb(249, 226, 175)), // yellow
                _                  => new SolidColorBrush(Color.FromRgb(108, 112, 134))  // grey
            }
            : new SolidColorBrush(Color.FromRgb(108, 112, 134));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class LockStateToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is LockState state
            ? state switch
            {
                LockState.Locked  => "LOCKED",
                LockState.Syncing => "SYNCING",
                _                 => "SEARCHING"
            }
            : "---";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class NonEmptyToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && s.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
