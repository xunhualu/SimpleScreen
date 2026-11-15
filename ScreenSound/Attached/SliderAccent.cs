using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace ScreenSound.Attached;

/// <summary>
/// Lets an individual Slider opt out of the theme accent: WPF UI's slider
/// template paints its thumb with &lt;SolidColorBrush Color="{DynamicResource
/// SystemAccentColorSecondary}"/&gt;, so overriding that Color resource inside
/// the slider's own Resources recolors just this one slider.
/// </summary>
public static class SliderAccent
{
    public static readonly DependencyProperty AccentProperty =
        DependencyProperty.RegisterAttached("Accent", typeof(Color?),
            typeof(SliderAccent), new PropertyMetadata(null, OnAccentChanged));

    public static void SetAccent(DependencyObject d, Color? value) => d.SetValue(AccentProperty, value);
    public static Color? GetAccent(DependencyObject d) => (Color?)d.GetValue(AccentProperty);

    private static void OnAccentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not System.Windows.Controls.Slider slider) return;

        if (e.NewValue is Color color)
            slider.Resources["SystemAccentColorSecondary"] = color;
        else
            slider.Resources.Remove("SystemAccentColorSecondary");
    }
}
