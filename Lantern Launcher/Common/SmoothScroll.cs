using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LanternLauncher.Common;

/// <summary>
/// Wheel scrolling: replaces WPF's one-notch-per-click jump with an eased chase toward a target offset.
/// Each frame moves current toward target by a fixed fraction (current += (target - current) * k),
/// which works out to a time constant of roughly 85 ms:
/// - a single notch reads as a short glide with a gentle settle;
/// - rapid notches keep adding to target, so the motion stays continuous instead of restarting.
/// With reduced motion enabled this bails out and lets the platform scroll natively.
/// </summary>
public static class SmoothScroll
{
    /// <summary>Time constant in seconds. Smaller feels tighter, larger feels floatier.</summary>
    private const double TimeConstant = 0.085;

    /// <summary>Distance below which the animation snaps to target, in pixels.</summary>
    private const double SettleEpsilon = 0.4;

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(SmoothScroll),
            new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) =>
        element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) =>
        (bool)element.GetValue(EnabledProperty);

    private sealed class State
    {
        public double Target;
        public bool Running;
        public long LastTicks;
        public EventHandler? Tick;
    }

    private static readonly ConditionalWeakTable<ScrollViewer, State> States = new();

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer) return;

        viewer.PreviewMouseWheel -= OnPreviewMouseWheel;
        viewer.ScrollChanged -= OnScrollChanged;

        if (e.NewValue is true)
        {
            viewer.PreviewMouseWheel += OnPreviewMouseWheel;
            viewer.ScrollChanged += OnScrollChanged;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer) return;
        if (viewer.ScrollableHeight <= 0.5) return;
            if (ReduceMotion) return;                 // hand it back to native scrolling

        if (Trace)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(Services.AppInfo.DataDir, "scroll-hit.txt"),
                    $"wheel delta={e.Delta} scrollable={viewer.ScrollableHeight:F0} offset={viewer.VerticalOffset:F0}" + Environment.NewLine);
            }
            catch (System.IO.IOException) { }
        }

        var state = States.GetOrCreateValue(viewer);

        // Dragging the thumb or paging with the keyboard means target must follow the real offset.
        if (!state.Running) state.Target = viewer.VerticalOffset;

        var step = Math.Max(48, viewer.ViewportHeight * 0.12);
        var next = state.Target - Math.Sign(e.Delta) * step;
        state.Target = Math.Clamp(next, 0, viewer.ScrollableHeight);

        e.Handled = true;

        if (!state.Running) Start(viewer, state);
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer viewer) return;
        if (!States.TryGetValue(viewer, out var state)) return;

        // Any scroll this handler did not cause (thumb drag, keyboard, ScrollIntoView) resyncs target.
        if (!state.Running) state.Target = viewer.VerticalOffset;
    }

    private static void Start(ScrollViewer viewer, State state)
    {
        state.Running = true;
        state.LastTicks = Stopwatch.GetTimestamp();

        state.Tick = (_, _) => Step(viewer, state);
        CompositionTarget.Rendering += state.Tick;
    }

    private static readonly bool Trace =
        Environment.GetEnvironmentVariable("LANTERN_SCROLL_TRACE") == "1";

    private static readonly System.Collections.Generic.List<string> TraceLines = new();

    private static void Step(ScrollViewer viewer, State state)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = Math.Max(0.001, (now - state.LastTicks) / (double)Stopwatch.Frequency);
        state.LastTicks = now;

        var current = viewer.VerticalOffset;
        var remaining = state.Target - current;

        if (Trace)
            TraceLines.Add($"  {dt * 1000,5:F0}ms  cur={current,8:F1}  target={state.Target,8:F1}  delta={remaining,7:F1}");

        if (Math.Abs(remaining) < SettleEpsilon)
        {
            viewer.ScrollToVerticalOffset(state.Target);
            if (Trace)
            {
                TraceLines.Add($"  settle at {state.Target:F1}");
                try
                {
                    System.IO.File.WriteAllLines(
                        System.IO.Path.Combine(Services.AppInfo.DataDir, "scroll-trace.txt"), TraceLines);
                }
                catch (System.IO.IOException) { }
                TraceLines.Clear();
            }
            Stop(state);
            return;
        }

        // Exponential approach: frame-rate independent, so a dropped frame does not make it jump.
        var k = 1 - Math.Exp(-dt / TimeConstant);
        viewer.ScrollToVerticalOffset(current + remaining * k);
    }

    private static void Stop(State state)
    {
        state.Running = false;
        if (state.Tick is null) return;
        CompositionTarget.Rendering -= state.Tick;
        state.Tick = null;
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
