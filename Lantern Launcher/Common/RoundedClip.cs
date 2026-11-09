using System.Windows;
using System.Windows.Media;

namespace LanternLauncher.Common;

/// <summary>
/// Gives any FrameworkElement a rounded clip that follows its size.
/// ClipToBounds only clips to a rectangle, and a Border's CornerRadius only paints its own
/// background and border, so an Image inside a rounded container still shows square corners
/// the moment the bitmap loads. This attached property rebuilds a RectangleGeometry on
/// SizeChanged, keeping the clip and the border in sync.
/// </summary>
public static class RoundedClip
{
    public static readonly DependencyProperty RadiusProperty =
        DependencyProperty.RegisterAttached(
            "Radius",
            typeof(double),
            typeof(RoundedClip),
            new PropertyMetadata(0d, OnRadiusChanged));

    public static void SetRadius(DependencyObject element, double value) =>
        element.SetValue(RadiusProperty, value);

    public static double GetRadius(DependencyObject element) =>
        (double)element.GetValue(RadiusProperty);

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        element.SizeChanged -= OnSizeChanged;
        element.SizeChanged += OnSizeChanged;
        Apply(element);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement element)
    {
        var radius = GetRadius(element);
        if (radius <= 0 || element.ActualWidth < 1 || element.ActualHeight < 1)
        {
            element.Clip = null;
            return;
        }

        // Radius above half the short side degenerates the geometry.
        var limit = Math.Min(element.ActualWidth, element.ActualHeight) / 2;
        var r = Math.Min(radius, limit);

        element.Clip = new RectangleGeometry(
            new Rect(0, 0, element.ActualWidth, element.ActualHeight), r, r);
    }
}
