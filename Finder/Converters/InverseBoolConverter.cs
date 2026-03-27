using System;
using System.Globalization;
using Xamarin.Forms;

namespace Finder.Converters
{
    /// <summary>
    /// Converts a boolean to its inverse.
    /// Used for enabling/disabling UI elements based on opposite conditions.
    /// </summary>
    public class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
                return !boolValue;
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
                return !boolValue;
            return value;
        }
    }
}