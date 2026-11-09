using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LanternLauncher.Common;
using LanternLauncher.Shell;

namespace LanternLauncher;

public partial class MainWindow : Window
{
    private readonly AppShell _shell = AppShell.Instance;
    private readonly TranslateTransform _pageShift = new();
    private readonly ScaleTransform _pageScale = new(1, 1);

    private bool _isAnimating;
    private (Route? Route, NavKind Kind)? _pendingRequest;
    private bool _syncNav;
    private bool _indicatorRetry;
    private PageKey _displayedKey = PageKey.Home;

    // Rounded corners and the DWM shadow: with WindowStyle=None Windows hands out square corners and
    // paints no shadow, so request DWMWA_WINDOW_CORNER_PREFERENCE and leave 1px of glass frame to cast into.
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int WmAppCommand = 0x0319;   // some mice send back/forward as an app command
    private const int AppCommandBrowserBackward = 1;
    private const int AppCommandBrowserForward = 2;
    private const int XButton1 = 1;   // mouse button 4 (rear thumb): back
    private const int XButton2 = 2;   // mouse button 5 (front thumb): forward

    private const int DwmwaWindowCornerPreference = 33;
    private const int GwlStyle = -16;
    private const int WsCaption = 0x00C00000;

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    /// <summary>
    /// Windows 11 only rounds and shadows top-level windows that own a caption.
    /// A WindowStyle=None window built on WindowChrome has no WS_CAPTION, so DWM keeps drawing
    /// square corners and no shadow. Put WS_CAPTION back, then request the corner preference;
    /// CaptionHeight=0 on WindowChrome keeps the system title bar from reappearing.
    /// </summary>
    private void ApplyWindowFrame()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var style = GetWindowLong(hwnd, GwlStyle);
        if ((style & WsCaption) != WsCaption)
            SetWindowLong(hwnd, GwlStyle, style | WsCaption);

        // 2 = rounded, 1 = square (maximised windows sit flush, and rounded corners would leak the desktop)
        var preference = WindowState == WindowState.Maximized ? 1 : 2;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }

    /// <summary>
    /// Mouse thumb buttons drive the shell's back/forward.
    /// The high word of WM_XBUTTONDOWN's wParam says which XBUTTON it was; WM_XBUTTONUP is swallowed
    /// as well, otherwise Windows plays the navigation sound on every press.
    /// </summary>
    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmXButtonDown || msg == WmXButtonUp)
        {
            var button = (int)(((long)wParam >> 16) & 0xFFFF);
            if (msg == WmXButtonDown)
            {
                if (button == XButton1) _shell.Request(null, NavKind.Back);
                else if (button == XButton2) _shell.Request(null, NavKind.Forward);
            }

            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WmAppCommand)
        {
            var command = (int)(((long)lParam >> 16) & 0x0FFF);
            if (command == AppCommandBrowserBackward)
            {
                _shell.Request(null, NavKind.Back);
                handled = true;
            }
            else if (command == AppCommandBrowserForward)
            {
                _shell.Request(null, NavKind.Forward);
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _shell;

        PageHost.RenderTransform = new TransformGroup
        {
            Children = { _pageScale, _pageShift }
        };
        PageHost.RenderTransformOrigin = new Point(0.5, 0.5);

        _shell.NavigationRequested += OnNavigationRequested;

        // A menu held open by StaysOpen must not survive the window losing focus.
        Deactivated += (_, _) =>
        {
            if (AccountPopup.IsOpen) Common.DropdownMotion.CloseMenu(AccountPopup);
        };

        // Quitting with downloads in flight: ask first, because closing kills them.
        Closing += (_, e) =>
        {
            if (!_shell.Downloads.PrepareExit()) e.Cancel = true;
        };

        SourceInitialized += (_, _) =>
        {
            ApplyWindowFrame();

            // Thumb buttons never reach WPF routed events, so the window message is handled directly.
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(OnWindowMessage);
        };

        PreviewKeyDown += OnPreviewKeyDown;

        Loaded += (_, _) =>
        {
            SyncNavSelection(_shell.CurrentRoute.Key, animate: false);
            _shell.SetDisplayed(_shell.CurrentRoute);
            UpdateIndicator();
        };
    }

    // Navigation - single crossfade (no black frame, chainable requests)

    private void OnNavigationRequested(Route? route, NavKind kind)
    {
        if (_isAnimating)
        {
            _pendingRequest = (route, kind);   // keep only the latest request
            return;
        }
        Execute(route, kind);
    }

    private void Execute(Route? route, NavKind kind)
    {
        switch (kind)
        {
            case NavKind.Normal:
                if (route is not { } r) return;
                if (r == _shell.Navigation.Current) { SyncNavSelection(r.Key, true); return; }
                _shell.Navigation.Navigate(r);
                break;
            case NavKind.Back:
                if (!_shell.Navigation.GoBack()) return;
                break;
            case NavKind.Forward:
                if (!_shell.Navigation.GoForward()) return;
                break;
        }

        AnimateTo(_shell.Navigation.Current, kind);
    }

    private void AnimateTo(Route target, NavKind kind)
    {
        _isAnimating = true;
        _pageShift.BeginAnimation(TranslateTransform.XProperty, null);
        _pageShift.X = 0;
        PageHost.BeginAnimation(OpacityProperty, null);

        var reduce = _shell.ReduceMotion;

        // Swap the page first, then fade the new one in: no bitmap cross-fade of the old page.
        // A snapshot is only removed on Completed, and until then it sits on top of the real
        // content -- which is exactly the "old page lingers for a moment" bug.
        PageHost.Opacity = 0;
        _shell.SetDisplayed(target);
        SyncNavSelection(target.Key, true);
        PageHost.UpdateLayout();

        // Back to the top: otherwise the new page inherits the previous scroll offset and
        // virtualised lists paint rows from the old position, which reads as leftover content.
        var scroller = FindDescendant<ScrollViewer>(PageHost);
        scroller?.ScrollToVerticalOffset(0);

        // Drill-down: library to instance detail. This is not a sibling page swap, so it scales
        // up slightly while sliding in, and going back mirrors that motion.
        // One motion for every page: the new content fades in and rises a few pixels.
        // Sliding sideways and scaling the whole host made text shimmer while it settled,
        // and the direction cue was never readable at these distances anyway.
        var rise = reduce ? 0.0 : 12.0;
        _pageShift.X = 0;
        _pageShift.Y = 0;

        var ms = reduce ? 80 : 240;
        var easing = (IEasingFunction)FindResource("EasePageSmooth");

        // Start at 0.55 rather than 0: fading in from zero shows one blank frame, which reads as a flash.
        PageHost.Opacity = 0.55;
        var fadeIn = new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = easing
        };
        Storyboard.SetTarget(fadeIn, PageHost);
        Storyboard.SetTargetProperty(fadeIn, new PropertyPath(UIElement.OpacityProperty));

        var sb = new Storyboard();
        sb.Children.Add(fadeIn);

        if (rise > 0.01)
        {
            var lift = new DoubleAnimation
            {
                From = rise,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(ms + 30),
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            };
            // Target the element with a RenderTransform path. Handing a Freezable to SetTarget
            // leaves the Storyboard unable to resolve it, and the animation silently never runs.
            Storyboard.SetTarget(lift, PageHost);
            Storyboard.SetTargetProperty(lift,
                new PropertyPath("(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.Y)"));
            sb.Children.Add(lift);
        }

        sb.Completed += (_, _) =>
        {
            PageHost.BeginAnimation(OpacityProperty, null);
            PageHost.Opacity = 1;
            _pageShift.BeginAnimation(TranslateTransform.XProperty, null);
            _pageShift.BeginAnimation(TranslateTransform.YProperty, null);
            _pageShift.X = 0;
            _pageShift.Y = 0;
            _pageScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _pageScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _pageScale.ScaleX = 1;
            _pageScale.ScaleY = 1;
            _displayedKey = target.Key;
            _isAnimating = false;

            var queued = _pendingRequest;
            _pendingRequest = null;
            if (queued is { } next)
                OnNavigationRequested(next.Route, next.Kind);
        };

        sb.Begin();
        TraceTransition(target.Key, ms);
    }

    /// <summary>
    /// Animation self-check: with LANTERN_ANIM_TRACE=1, logs opacity and offset every 20 ms for 400 ms.
    /// Lets a transition be proven to run without sitting there watching it.
    /// </summary>
    private void TraceTransition(PageKey key, int durationMs)
    {
        if (Environment.GetEnvironmentVariable("LANTERN_ANIM_TRACE") != "1") return;

        var path = System.IO.Path.Combine(Services.AppInfo.DataDir, "anim-trace.txt");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lines = new System.Collections.Generic.List<string> { $"# nav -> {key}, planned {durationMs}ms" };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };

        timer.Tick += (_, _) =>
        {
            lines.Add($"{sw.ElapsedMilliseconds,4}ms  opacity={PageHost.Opacity:F2}  y={_pageShift.Y:F1}");
            if (sw.ElapsedMilliseconds < 400) return;
            timer.Stop();
            try { System.IO.File.WriteAllLines(path, lines); } catch (System.IO.IOException) { }
        };

        timer.Start();
    }

    // Nav rail

    private static int NavIndex(PageKey key) => key switch
    {
        PageKey.Home => 0,
        PageKey.Library => 1,
        PageKey.Browse => 2,
        PageKey.Packs => 3,
        PageKey.Downloads => 4,
        PageKey.Settings => 5,
        _ => 0,
    };

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var deep = FindDescendant<T>(child);
            if (deep is not null) return deep;
        }
        return null;
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncNav) return;
        if (!IsLoaded) return;
        if (NavList.SelectedItem is ListBoxItem { Tag: string tag } &&
            Enum.TryParse<PageKey>(tag, out var key))
        {
            _shell.Request(new Route(key), NavKind.Normal);
        }
    }

    private void SyncNavSelection(PageKey key, bool animate)
    {
        var index = key switch
        {
            PageKey.Home => 0,
            PageKey.Library => 1,
            PageKey.Browse => 2,
            PageKey.Packs => 3,
            PageKey.Downloads => 4,
            PageKey.Settings => 5,
            PageKey.InstanceDetail => 1,   // detail still counts as part of Library
        PageKey.InstanceMods => 1,
            _ => -1
        };

        if (NavList.SelectedIndex != index)
        {
            _syncNav = true;
            NavList.SelectedIndex = index;
            _syncNav = false;
        }

        UpdateIndicator(animate);
    }

    private void UpdateIndicator(bool animate = false)
    {
        var index = NavList.SelectedIndex;
        if (index < 0)
        {
            NavIndicator.Opacity = 0;
            NavPill.Opacity = 0;
            return;
        }

        if (NavList.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem item)
        {
            // On the first frame the container may not exist yet (common during Loaded); retry after layout or the highlight never shows.
            if (_indicatorRetry) return;
            _indicatorRetry = true;
            Dispatcher.BeginInvoke(new Action(() => { _indicatorRetry = false; UpdateIndicator(animate); }),
                DispatcherPriority.Loaded);
            return;
        }

        var top = item.TransformToAncestor(NavRoot).Transform(new Point(0, 0)).Y;
        var target = top + Math.Max(0, (item.ActualHeight - NavIndicator.Height) / 2);
        var pillTop = top + (item.ActualHeight - NavPill.Height) / 2;
        var ease = (IEasingFunction)FindResource("EaseMove");

        NavIndicator.Opacity = 1;
        NavPill.Opacity = 1;

        if (animate)
        {
            var anim = new DoubleAnimation(NavShift.Y, target, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = ease
            };
            NavShift.BeginAnimation(TranslateTransform.YProperty, anim);

            // Highlight and indicator move on the same beat.
            var pillAnim = new DoubleAnimation(NavPillShift.Y, pillTop, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = ease
            };
            NavPillShift.BeginAnimation(TranslateTransform.YProperty, pillAnim);

            if (Environment.GetEnvironmentVariable("LANTERN_NAV_TRACE") == "1")
            {
                var path = System.IO.Path.Combine(Services.AppInfo.DataDir, "nav-trace.txt");
                var from = NavPillShift.Y;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                timer.Tick += (_, _) =>
                {
                    try
                    {
                        System.IO.File.AppendAllText(path,
                            $"pill {sw.ElapsedMilliseconds,4}ms y={NavPillShift.Y:F1} (from {from:F1} to {pillTop:F1}){Environment.NewLine}");
                    }
                    catch (System.IO.IOException) { }
                    if (sw.ElapsedMilliseconds > 320) timer.Stop();
                };
                timer.Start();
            }
        }
        else
        {
            NavShift.BeginAnimation(TranslateTransform.YProperty, null);
            NavShift.Y = target;

            NavPillShift.BeginAnimation(TranslateTransform.YProperty, null);
            NavPillShift.Y = pillTop;
        }
    }

    // Window chrome

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        var hwnd = new WindowInteropHelper(this).Handle;
        SendMessage(hwnd, 0xA1 /*WM_NCLBUTTONDOWN*/, (IntPtr)0x2 /*HTCAPTION*/, IntPtr.Zero);
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        ApplyWindowFrame();

        if (WindowState == WindowState.Maximized)
        {
            var t = SystemParameters.WindowResizeBorderThickness;
            RootGrid.Margin = new Thickness(t.Left, t.Top, t.Right, t.Bottom);
            MaxButton.Content = "\uE923";
        }
        else
        {
            RootGrid.Margin = new Thickness(0);
            MaxButton.Content = "\uE922";
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => UpdateIndicator()));
    }

    // Go through SystemCommands (WM_SYSCOMMAND) rather than setting WindowState directly:
    // the latter bypasses the system window procedure and DWM stops animating minimise/maximise.
    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Back_Click(object sender, RoutedEventArgs e) => _shell.Request(null, NavKind.Back);
    private void Forward_Click(object sender, RoutedEventArgs e) => _shell.Request(null, NavKind.Forward);

    private void Avatar_Click(object sender, RoutedEventArgs e)
    {
        if (AccountPopup.IsOpen)
        {
            Common.DropdownMotion.CloseMenu(AccountPopup);
            return;
        }

        AccountPopup.PlacementTarget = AvatarButton;
        Common.DropdownMotion.OpenMenu(AccountPopup);
    }

    /// <summary>
    /// The menu keeps itself open so a second click on the avatar can toggle it. Clicks anywhere
    /// else in the window close it, except the ones that land on the avatar - those are the
    /// toggle and are handled by the click itself, not by this hook.
    /// </summary>
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!AccountPopup.IsOpen) return;
        if (e.OriginalSource is DependencyObject source && IsWithin(source, AvatarButton)) return;

        Common.DropdownMotion.CloseMenu(AccountPopup);
    }

    private static bool IsWithin(DependencyObject? node, DependencyObject ancestor)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    private void AccountSettings_Click(object sender, RoutedEventArgs e)
    {
        Common.DropdownMotion.CloseMenu(AccountPopup);
        _shell.Request(new Route(PageKey.Settings));
    }

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        Common.DropdownMotion.CloseMenu(AccountPopup);
        _shell.ShowToast("Offline profile — nothing to sign out of.", ToastType.Info);
    }

    // Keyboard

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;

        switch (e.Key)
        {
            case Key.D1: _shell.Request(new Route(PageKey.Home)); e.Handled = true; break;
            case Key.D2: _shell.Request(new Route(PageKey.Library)); e.Handled = true; break;
            case Key.D3: _shell.Request(new Route(PageKey.Browse)); e.Handled = true; break;
            case Key.D4: _shell.Request(new Route(PageKey.Packs)); e.Handled = true; break;
            case Key.D5: _shell.Request(new Route(PageKey.Downloads)); e.Handled = true; break;
            case Key.K: _shell.Request(new Route(PageKey.Browse)); e.Handled = true; break;
            case Key.OemComma: _shell.Request(new Route(PageKey.Settings)); e.Handled = true; break;
            case Key.W: Close(); e.Handled = true; break;
        }
    }
}
