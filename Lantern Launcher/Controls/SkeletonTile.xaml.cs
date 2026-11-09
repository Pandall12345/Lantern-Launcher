using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LanternLauncher.Common;

namespace LanternLauncher.Controls;

/// <summary>
/// Skeleton block. Byte-for-byte the loading state of AsyncIcon: same fill, same band, same timing.
/// It used to breathe while images swept, which put two rhythms on one screen.
/// </summary>
public partial class SkeletonTile : UserControl
{
    public static readonly DependencyProperty RadiusProperty =
        DependencyProperty.Register(
            nameof(Radius), typeof(double), typeof(SkeletonTile),
            new PropertyMetadata(6d, OnRadiusChanged));

    public SkeletonTile()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ApplyClip();
            LoadingMotion.StartBand(Band, ActualWidth, ActualHeight);
        };
        SizeChanged += (_, _) =>
        {
            ApplyClip();
            LoadingMotion.StartBand(Band, ActualWidth, ActualHeight);
        };
        Unloaded += (_, _) => LoadingMotion.StopBand(Band);
    }

    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SkeletonTile tile) tile.ApplyClip();
    }

    private void ApplyClip()
    {
        if (ActualWidth < 1 || ActualHeight < 1) return;
        var r = Math.Min(Radius, Math.Min(ActualWidth, ActualHeight) / 2);
        Host.Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), r, r);
    }
}
