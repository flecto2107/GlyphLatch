using System.Windows;
using System.Windows.Controls;

namespace GlyphLatch.Windows;

public sealed class ActivationLamp : Button
{
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(ActivationLamp));
    public static readonly DependencyProperty IsPartialProperty =
        DependencyProperty.Register(nameof(IsPartial), typeof(bool), typeof(ActivationLamp));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool IsPartial
    {
        get => (bool)GetValue(IsPartialProperty);
        set => SetValue(IsPartialProperty, value);
    }
}
