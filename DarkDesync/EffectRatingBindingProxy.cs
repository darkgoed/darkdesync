using System.Windows;

namespace DarkDesync;

// Inherits the window's DataContext even inside a ComboBox popup's separate tree.
public sealed class EffectRatingBindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(object), typeof(EffectRatingBindingProxy));
    public object Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    protected override Freezable CreateInstanceCore() => new EffectRatingBindingProxy();
}
