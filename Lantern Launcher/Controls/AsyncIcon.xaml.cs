using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LanternLauncher.Controls;

/// <summary>
/// Two-stage loading for remote images.
/// Stage one (no image yet): the same ink band every skeleton uses.
/// Stage two (image arrived): 200 ms cross-fade.
/// Failure or timeout: the block keeps the band instead of freezing white -- "unavailable" is not "broken".
/// </summary>
public partial class AsyncIcon : UserControl
{
    private const int TimeoutMs = 12000;

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(
            nameof(Source), typeof(string), typeof(AsyncIcon),
            new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty RadiusProperty =
        DependencyProperty.Register(
            nameof(Radius), typeof(double), typeof(AsyncIcon),
            new PropertyMetadata(10d, OnRadiusChanged));

    private DispatcherTimer? _timeout;
    private BitmapImage? _bitmap;

    public string? Source
    {
        get => (string?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    public AsyncIcon()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            ApplyRadius();
            if (ImageHost.Opacity < 1) StartSweep();
        };
        SizeChanged += (_, _) =>
        {
            ApplyRadius();
            if (Sweep.Opacity > 0) StartSweep();
        };
        Unloaded += (_, _) =>
        {
            StopTimeout();
            StopSweep();
            StopIdle();
        };
    }

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AsyncIcon icon) icon.ApplyRadius();
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AsyncIcon icon) icon.Load(e.NewValue as string);
    }

    private void ApplyRadius()
    {
        if (ActualWidth < 1 || ActualHeight < 1) return;
        var limit = Math.Min(ActualWidth, ActualHeight) / 2;
        var r = Math.Min(Radius, limit);
        Host.Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), r, r);
        Edge.CornerRadius = new CornerRadius(Math.Max(0, r - 1));

        // A Border with no children measures to zero height, so set it explicitly or the sweep is invisible.
        Sweep.Height = ActualHeight;
    }

    // Loading: the exact band the skeleton tiles use.

    private void StartSweep()
    {
        StopIdle();
        Common.LoadingMotion.StartBand(Sweep, ActualWidth, ActualHeight);
    }

    private void StopSweep()
    {
        Common.LoadingMotion.StopBand(Sweep);
    }

    /// <summary>
    /// Failure and timeout keep this band. Switching to a different rhythm here
    /// would put two animations on one screen, which is what made it look noisy before.
    /// </summary>
    private void StartIdle()
    {
        StartSweep();
    }

    private void StopIdle()
    {
        StopSweep();
    }

    // Load pipeline

    /// <summary>Dev switch: pretend the download never finishes. LANTERN_ICON_OFFLINE=1</summary>
    private static readonly bool ForceOffline =
        Environment.GetEnvironmentVariable("LANTERN_ICON_OFFLINE") == "1";

    /// <summary>Dev switch: stay in the loading state so the sweep is easy to inspect. LANTERN_ICON_SLOW=1</summary>
    private static readonly bool ForceSlow =
        Environment.GetEnvironmentVariable("LANTERN_ICON_SLOW") == "1";

    private void Load(string? url)
    {
        StopTimeout();

        ImageHost.BeginAnimation(OpacityProperty, null);
        ImageHost.Opacity = 0;
        ImageHost.Source = null;
        _bitmap = null;

        StartSweep();

            if (ForceSlow) return;   // stay in loading: no download, no fallback

        if (ForceOffline)
        {
            GiveUp();
            return;
        }

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            GiveUp();
            return;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = uri;
        bitmap.CacheOption = BitmapCacheOption.OnDemand;

        // Must be attached before EndInit: with a warm cache DownloadCompleted fires synchronously.
        bitmap.DownloadCompleted += (_, _) => OnReady(bitmap);
        bitmap.DownloadFailed += (_, _) => GiveUp();
        bitmap.DecodeFailed += (_, _) => GiveUp();

        bitmap.EndInit();
        _bitmap = bitmap;

        ImageHost.Source = bitmap;
        StartTimeout();
    }

    private void OnReady(BitmapImage bitmap)
    {
        if (!ReferenceEquals(bitmap, _bitmap)) return;

        ImageHost.Source = bitmap;
        StopTimeout();
        StopSweep();

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        ImageHost.BeginAnimation(OpacityProperty, fade);
    }

    private void GiveUp()
    {
        StopTimeout();
        StartIdle();
    }

    private void StartTimeout()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TimeoutMs) };
        timer.Tick += (_, _) =>
        {
            StopTimeout();
            if (_bitmap is { IsDownloading: true }) return;

            if (_bitmap is { PixelWidth: > 0 }) OnReady(_bitmap);
            else GiveUp();
        };
        _timeout = timer;
        timer.Start();
    }

    private void StopTimeout()
    {
        _timeout?.Stop();
        _timeout = null;
    }

    private bool ReduceMotion
    {
        get
        {
            try { return Shell.AppShell.Instance.Settings.ReduceMotion; }
            catch (Exception) { return false; }
        }
    }
}
