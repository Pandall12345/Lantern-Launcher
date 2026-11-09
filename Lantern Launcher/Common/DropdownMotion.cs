using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Shapes;
using System.ComponentModel;

namespace LanternLauncher.Common;

/// <summary>
/// Open and close animation for drop-downs.
/// WPF only snaps a ComboBox open and shut, and PopupAnimation covers opening alone --
/// so opening is animated here (offset plus fade) and closing fades out a snapshot instead, which
/// lets the menu appear to glide away even though the Popup itself is already gone.
/// </summary>
internal static class DropdownMotion
{
    private const int OpenMs = 150;
    private const int CloseMs = 120;

    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        // DropDownOpened/DropDownClosed are plain CLR events with no routed counterpart,
        // so this hooks the dependency property: IsDropDownOpen tells us which way it went.
        EventManager.RegisterClassHandler(
            typeof(ComboBox), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => Attach(s as ComboBox)));

    }

    private static void Attach(ComboBox? combo)
    {
        if (combo is null) return;
        if (combo.GetValue(HookedProperty) is true) return;
        combo.SetValue(HookedProperty, true);

        var descriptor = DependencyPropertyDescriptor.FromProperty(
            ComboBox.IsDropDownOpenProperty, typeof(ComboBox));
        descriptor?.AddValueChanged(combo, (s, _) =>
        {
            if (s is not ComboBox c) return;
            if (c.IsDropDownOpen) AnimateOpen(c);
            else FadeOutGhost(c);
        });
    }

    private static readonly DependencyProperty HookedProperty =
        DependencyProperty.RegisterAttached(
            "Hooked", typeof(bool), typeof(DropdownMotion), new PropertyMetadata(false));

    /// <summary>Set Hook="True" in the style so every instance wires itself up.</summary>
    public static readonly DependencyProperty HookProperty =
        DependencyProperty.RegisterAttached(
            "Hook", typeof(bool), typeof(DropdownMotion),
            new PropertyMetadata(false, OnHookChanged));

    public static void SetHook(DependencyObject d, bool value) => d.SetValue(HookProperty, value);

    public static bool GetHook(DependencyObject d) => (bool)d.GetValue(HookProperty);

    private static void OnHookChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) Attach(d as ComboBox);
    }

    /// <summary>
    /// Menu popups: start hidden and low, open, then fade and lift once the child has been
    /// laid out. Animating before layout gives a menu that snaps into place afterwards, and
    /// the popup's own PopupAnimation would fade a second time on top of this.
    /// </summary>
    public static void OpenMenu(Popup popup)
    {
        if (popup.Child is not FrameworkElement child)
        {
            popup.IsOpen = true;
            return;
        }

        var shift = EnsureShift(child);

        if (ReduceMotion)
        {
            child.Opacity = 1;
            shift.Y = 0;
            popup.IsOpen = true;
            return;
        }

        child.Opacity = 0;
        shift.Y = MenuRise;
        popup.IsOpen = true;

        Trace("menu open requested");
        child.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            Trace($"menu animating from opacity {child.Opacity:0.00} y {shift.Y:0.0}");
            if (child.Opacity > 0.01) return; // already animated by a second open

            Run(child, UIElement.OpacityProperty, 0, 1, OpenMs, () => child.Opacity = 1);
            Run(shift, TranslateTransform.YProperty, MenuRise, 0, OpenMs + 40, () => shift.Y = 0);
        }));
    }

    /// <summary>Capture the snapshot first, then actually close.</summary>
    public static void CloseMenu(Popup popup)
    {
        FadeOutPopup(popup);
        popup.IsOpen = false;
    }

    private static TranslateTransform EnsureShift(FrameworkElement child)
    {
        if (child.RenderTransform is TranslateTransform shift) return shift;

        shift = new TranslateTransform();
        child.RenderTransform = shift;
        return shift;
    }

    /// <summary>Animate to the target, then clear the animation and leave the value behind.</summary>
    private static void Run(IAnimatable target, DependencyProperty property,
        double from, double to, int milliseconds, Action settle)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };

        animation.Completed += (_, _) =>
        {
            target.BeginAnimation(property, null);
            settle();
            Trace("menu anim step done");
        };

        target.BeginAnimation(property, animation);
    }

    private static void FadeOutPopup(Popup popup)
    {
        if (popup.Child is FrameworkElement child) FadeOutElement(popup, child);
    }

    private const double MenuRise = 8;

    private static void AnimateOpen(ComboBox? combo)
    {
        if (combo is null || ReduceMotion) return;
        if (combo.Template?.FindName("PART_Popup", combo) is not Popup popup) return;
        if (popup.Child is not FrameworkElement child) return;

        child.BeginAnimation(UIElement.OpacityProperty, null);

        if (child.RenderTransform is not TranslateTransform shift)
        {
            shift = new TranslateTransform();
            child.RenderTransform = shift;
        }

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(OpenMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        child.BeginAnimation(UIElement.OpacityProperty, fade);

        var rise = new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(OpenMs + 40))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        shift.BeginAnimation(TranslateTransform.YProperty, rise);
    }

    /// <summary>Close: capture the menu that just vanished and fade it out over the window.</summary>
    private static void FadeOutGhost(ComboBox? combo)
    {
        if (combo is null || ReduceMotion) return;
        if (combo.Template?.FindName("PART_Popup", combo) is not Popup popup) { Trace("no popup"); return; }
        if (popup.Child is not FrameworkElement child) { Trace("no child"); return; }
        FadeOutElement(popup, child);
    }

    private static void FadeOutElement(Popup popup, FrameworkElement child)
    {
        if (ReduceMotion) return;
        if (child.ActualWidth < 1 || child.ActualHeight < 1) { Trace("child not sized"); return; }

        try
        {
            var window = popup.PlacementTarget is null
                ? Application.Current?.MainWindow
                : Window.GetWindow(popup.PlacementTarget);
            if (window is null) return;

            var layer = FindLayer(window);
            if (layer is null) { Trace("no FadeLayer"); return; }

            var dpi = VisualTreeHelper.GetDpi(child);
            var pxW = (int)Math.Ceiling(child.ActualWidth * dpi.DpiScaleX);
            var pxH = (int)Math.Ceiling(child.ActualHeight * dpi.DpiScaleY);
            if (pxW < 1 || pxH < 1) return;

            var rtb = new RenderTargetBitmap(pxW, pxH, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            rtb.Render(child);

            var origin = child.PointToScreen(new Point(0, 0));
            var winOrigin = window.PointToScreen(new Point(0, 0));
            var left = (origin.X - winOrigin.X) / dpi.DpiScaleX;
            var top = (origin.Y - winOrigin.Y) / dpi.DpiScaleY;

            var ghost = new Image
            {
                Source = rtb,
                Width = child.ActualWidth,
                Height = child.ActualHeight,
                IsHitTestVisible = false,
                RenderTransform = new TranslateTransform(),
            };

            Canvas.SetLeft(ghost, left);
            Canvas.SetTop(ghost, top);
            layer.Children.Add(ghost);

            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(CloseMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            fade.Completed += (_, _) => layer.Children.Remove(ghost);
            ghost.BeginAnimation(UIElement.OpacityProperty, fade);

            if (ghost.RenderTransform is TranslateTransform shift)
            {
                shift.BeginAnimation(
                    TranslateTransform.YProperty,
                    new DoubleAnimation(0, -4, TimeSpan.FromMilliseconds(CloseMs))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    });
            }

            Trace($"ghost {left:F0},{top:F0} {child.ActualWidth:F0}x{child.ActualHeight:F0}");
        }
        catch (Exception ex)
        {
            Trace("ghost failed: " + ex.GetType().Name);
        }
    }

    private static Canvas? FindLayer(Window window)
    {
        if (window.Content is FrameworkElement root)
        {
            var found = FindByName(root, "FadeLayer") as Canvas;
            if (found is not null) return found;
        }

        return null;
    }

    private static FrameworkElement? FindByName(DependencyObject parent, string name)
    {
        if (parent is FrameworkElement fe && fe.Name == name) return fe;

        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            var hit = FindByName(child, name);
            if (hit is not null) return hit;
        }

        return null;
    }

    private static void Trace(string line)
    {
        if (Environment.GetEnvironmentVariable("LANTERN_MENU_TRACE") != "1") return;
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(Services.AppInfo.DataDir, "menu-trace.txt"),
                $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch (System.IO.IOException) { }
    }

    private static bool ReduceMotion
    {
        get
        {
            try { return Shell.AppShell.Instance.Settings.ReduceMotion; }
            catch (Exception) { return false; }
        }
    }
}
