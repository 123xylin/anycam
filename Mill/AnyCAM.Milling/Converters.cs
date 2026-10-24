using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AnyCAM.Milling
{
    /// <summary>false → Visible，true → Collapsed。面板正文随选中对象类型切换时用。</summary>
    public sealed class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is true ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
