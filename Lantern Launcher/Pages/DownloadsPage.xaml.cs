using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LanternLauncher.Pages;

public partial class DownloadsPage : UserControl
{
    private bool _isOverlayAnimating;

    public DownloadsPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is DownloadsViewModel oldVm) oldVm.PropertyChanged -= OnViewModelChanged;
        if (e.NewValue is DownloadsViewModel newVm) newVm.PropertyChanged += OnViewModelChanged;
    }

    /// <summary>
    /// Visibility is driven here rather than bound: a collapse would cut the exit animation
    /// off at frame one, so the page hides the overlay only when the storyboard is finished.
    /// </summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DownloadsViewModel.IsAddOpen)) return;
        if (sender is not DownloadsViewModel vm) return;

        if (vm.IsAddOpen) ShowOverlay();
        else HideOverlay();
    }

    private void ShowOverlay()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        OverlayRoot.Visibility = Visibility.Visible;

        Scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.26, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        });

        OverlayCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        });

        AnimateRise(12, 0, 240, null);
    }

    private void HideOverlay()
    {
        if (OverlayRoot.Visibility != Visibility.Visible) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        _isOverlayAnimating = true;

        Scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0.26, 0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        });

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };

        fade.Completed += (_, _) =>
        {
            _isOverlayAnimating = false;
            OverlayCard.Opacity = 1;
            if (DataContext is DownloadsViewModel { IsAddOpen: false })
                OverlayRoot.Visibility = Visibility.Collapsed;
        };

        OverlayCard.BeginAnimation(OpacityProperty, fade);
        AnimateRise(0, 6, 150, ease);
    }

    private void AnimateRise(double from, double to, int milliseconds, IEasingFunction? ease)
    {
        if (OverlayCard.RenderTransform is not TranslateTransform offset) return;

        offset.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = ease ?? new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
    }

    private void OnScrimClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_isOverlayAnimating) return;
        if (DataContext is DownloadsViewModel vm) vm.CloseOverlayCommand.Execute(null);
    }
}
