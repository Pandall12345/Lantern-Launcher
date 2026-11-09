using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LanternLauncher.Pages;

/// <summary>
/// Two things this page does on top of the view model:
/// 1) slides the segmented control's white thumb to the selected segment, sizing it to the label
/// 2) darkens the selected label and returns the others to muted
/// </summary>
public partial class LibraryPage : UserControl
{
    private LibraryViewModel? _vm;

    public LibraryPage()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => UpdateSwitcher(animate: false);
        SizeChanged += (_, _) => UpdateSwitcher(animate: false);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;

        _vm = DataContext as LibraryViewModel;

        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;

        Dispatcher.BeginInvoke(() => UpdateSwitcher(animate: false), DispatcherPriority.Loaded);
    }

    /// <summary>Content transition when the library changes: fade in plus a 6px lift, a smaller version of the page transition rather than a second motion language.</summary>
    private void AnimateContentIn()
    {
        try
        {
            if (Shell.AppShell.Instance.Settings.ReduceMotion) return;
        }
        catch (Exception) { }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        ContentRoot.BeginAnimation(UIElement.OpacityProperty, null);
        ContentRoot.Opacity = 0.28;
        ContentRoot.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0.28, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });

        ContentShift.BeginAnimation(TranslateTransform.YProperty, null);
        ContentShift.Y = 10;
        ContentShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(340)) { EasingFunction = ease });

        if (Environment.GetEnvironmentVariable("LANTERN_LIB_TRACE") == "1")
        {
            var path = System.IO.Path.Combine(Services.AppInfo.DataDir, "lib-trace.txt");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                try
                {
                    System.IO.File.AppendAllText(path,
                        $"{sw.ElapsedMilliseconds,4}ms opacity={ContentRoot.Opacity:F2} y={ContentShift.Y:F1}{Environment.NewLine}");
                }
                catch (System.IO.IOException) { }
                if (sw.ElapsedMilliseconds > 260) timer.Stop();
            };
            timer.Start();
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LibraryViewModel.IsLantern))
        {
            // Animate only when the user actually clicked a segment. Entering the page or
            // restoring the last library does not animate; an indirect check like IsLoading misses that.
            if (_vm is { IsUserSwitchingLibrary: true })
                Dispatcher.BeginInvoke(AnimateContentIn, DispatcherPriority.Loaded);
        }

        if (e.PropertyName is nameof(LibraryViewModel.IsLantern)
            or nameof(LibraryViewModel.PathText)
            or nameof(LibraryViewModel.LanternCountText)
            or nameof(LibraryViewModel.MojangCountText)
            or nameof(LibraryViewModel.HasFeatured))
        {
            Dispatcher.BeginInvoke(() => UpdateSwitcher(animate: true), DispatcherPriority.Loaded);
        }
    }

    private void UpdateSwitcher(bool animate)
    {
        var isLantern = _vm?.IsLantern ?? true;
        var target = isLantern ? SegLantern : SegMojang;

        if (!target.IsLoaded || target.ActualWidth < 1) return;

        var x = target.TranslatePoint(new Point(0, 0), Switcher).X;
        var width = target.ActualWidth;

        if (!animate)
        {
            SegShift.BeginAnimation(TranslateTransform.XProperty, null);
            SegShift.X = x;

            SegPill.BeginAnimation(FrameworkElement.WidthProperty, null);
            SegPill.Width = width;
        }
        else
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            SegShift.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(x, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });

            SegPill.BeginAnimation(
                FrameworkElement.WidthProperty,
                new DoubleAnimation(width, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        }

        var selected = (Brush)FindResource("InkBrush");
        var idle = (Brush)FindResource("MuteBrush");

        SegLantern.Foreground = isLantern ? selected : idle;
        SegMojang.Foreground = isLantern ? idle : selected;
    }
}
