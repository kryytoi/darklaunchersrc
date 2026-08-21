using System;
using System.Globalization;
using System.Windows.Data;

namespace DarkVisualsLauncher1
{
    /// <summary>
    /// Tag кнопки меню хранит "путь.png" или "путь.png|размер" (например "/home.png|32").
    /// Этот конвертер отдаёт чистый путь к картинке для Image.Source.
    /// </summary>
    public class IconPathConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string tag = value as string ?? string.Empty;
            int sep = tag.IndexOf('|');
            return sep >= 0 ? tag.Substring(0, sep) : tag;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Отдаёт размер иконки (ширину/высоту), указанный после "|" в Tag.
    /// Если размер не указан — используется значение по умолчанию (22).
    /// </summary>
    public class IconSizeConverter : IValueConverter
    {
        private const double DefaultSize = 22.0;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string tag = value as string ?? string.Empty;
            int sep = tag.IndexOf('|');
            if (sep >= 0 && double.TryParse(tag.Substring(sep + 1), NumberStyles.Any, CultureInfo.InvariantCulture, out double size))
                return size;

            return DefaultSize;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}