using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LanternLauncher.Shell;

namespace LanternLauncher.Controls;

public partial class ToastNotification : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(4) };
    private bool _closing;

    public ToastNotification()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => _timer.Stop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ToastMessage msg) return;

        MessageText.Text = msg.Message;
        Dot.Fill = msg.Type switch
        {
            ToastType.Success => (Brush)FindResource("SuccessBrush"),
            ToastType.Warning => (Brush)FindResource("WarningBrush"),
            ToastType.Error => (Brush)FindResource("DangerBrush"),
            _ => (Brush)FindResource("AccentBrush")
        };

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(167))
        {
            EasingFunction = (IEasingFunction)FindResource("EaseEnter")
        };
        var slide = new DoubleAnimation(-10, 0, TimeSpan.FromMilliseconds(167))
        {
            EasingFunction = (IEasingFunction)FindResource("EaseEnter"),
            FillBehavior = FillBehavior.Stop
        };
        BeginAnimation(OpacityProperty, fadeIn);
        Shift.BeginAnimation(TranslateTransform.YProperty, slide);

        _timer.Tick += (_, _) => Dismiss();
        _timer.Start();
    }

    private void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();

        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(83))
        {
            EasingFunction = (IEasingFunction)FindResource("EaseExit")
        };
        fadeOut.Completed += (_, _) =>
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            if (DataContext is ToastMessage msg) AppShell.Instance.DismissToast(msg);
        };
        BeginAnimation(OpacityProperty, fadeOut);
    }
}
