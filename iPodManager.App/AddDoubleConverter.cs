using System;
using System.Globalization;
using System.Windows.Data;

namespace iPodManager
{
    public sealed class AddDoubleConverter : IValueConverter
    {
        public object Convert(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture)
        {
            double source = value is double number ? number : 0;
            double addition = double.TryParse(
                parameter?.ToString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsedAddition)
                ? parsedAddition
                : 0;
            return source + addition;
        }

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
