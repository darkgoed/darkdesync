using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DarkDesync.Core;

namespace DarkDesync;

public sealed class EffectRatingConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 2 || values[0] is not string label || values[1] is not int duration)
            return DependencyProperty.UnsetValue;
        var rating = EffectExperienceCatalog.Rating(ModeCatalog.Parse(label), duration);
        return (parameter as string) switch
        {
            "Background" => new SolidColorBrush((Color)ColorConverter.ConvertFromString(rating.Background)),
            "Foreground" => new SolidColorBrush((Color)ColorConverter.ConvertFromString(rating.Foreground)),
            "Note" => rating.Note,
            _ => rating.Label
        };
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
