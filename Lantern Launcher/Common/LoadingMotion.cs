using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LanternLauncher.Common;

/// <summary>
/// Loading light for skeletons and image slots: a faint ink band sweeping left to right.
/// Displacement only -- no scaling, no jitter, no opacity flicker. The placeholder itself stays still.
/// </summary>
internal static class LoadingMotion
{
    /// <summary>Band width in pixels. Fixed rather than proportional so small and large blocks sweep with the same visual weight.</summary>
    private const double BandRatio = 0.6;

    private const double BandMin = 40;
    private const double BandMax = 180;

    /// <summary>Peak alpha of the band. The fill is #DCE3EA; pushing much higher washes the block out.</summary>
    private const byte GlowAlpha = 120;

    /// <summary>
    /// One duration and one phase for the whole app, so every placeholder on screen pulses on the same beat;
    /// the only difference between blocks is width, never timing.
    /// </summary>
    private const int DurationMs = 1500;

    /// <summary>Shared head start: the first frame is already mid-sweep and every block stays in step.</summary>
    private const int StartOffsetMs = -300;

    public static void StartBand(Border band, double hostWidth, double hostHeight)
    {
        StopBand(band);
        if (ReduceMotion) return;
        if (hostWidth < 1 || hostHeight < 1) return;

        var width = Math.Clamp(hostWidth * BandRatio, BandMin, BandMax);

        band.Width = width;
        band.Height = hostHeight;
        band.Background = new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.0),
                new GradientStop(Color.FromArgb((byte)(GlowAlpha * 0.45), 255, 255, 255), 0.30),
                new GradientStop(Color.FromArgb(GlowAlpha, 255, 255, 255), 0.50),
                new GradientStop(Color.FromArgb((byte)(GlowAlpha * 0.45), 255, 255, 255), 0.70),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0),
            },
            new Point(0, 0), new Point(1, 0));

        // Target the element and a property path. Handing a Freezable to SetTarget fails silently.
        var move = new DoubleAnimation
        {
            // Start and end fully outside the block so the loop point is invisible.
            // SineEase eases both ends, which leaves a short pause between passes instead of constant scraping.
            From = -width,
            To = hostWidth,
            Duration = TimeSpan.FromMilliseconds(DurationMs),
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromMilliseconds(StartOffsetMs),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(move, band);
        Storyboard.SetTargetProperty(move, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.X)"));

        band.Opacity = 1;
        var story = new Storyboard();
        story.Children.Add(move);
        story.Begin();
        band.Tag = story;
    }

    public static void StopBand(Border band)
    {
        if (band.Tag is Storyboard story) story.Stop();
        band.Tag = null;
        band.BeginAnimation(UIElement.OpacityProperty, null);
        band.Opacity = 0;
    }

    public static bool ReduceMotion
    {
        get
        {
            try { return Shell.AppShell.Instance.Settings.ReduceMotion; }
            catch (Exception) { return false; }
        }
    }
}
