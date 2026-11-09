using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LanternLauncher.Shell;

namespace LanternLauncher.Services;

/// <summary>
/// Smooth-scroll self-check: <c>LanternLauncher.exe --scroll-probe</c>
/// Opens Modpacks, throws a real PreviewMouseWheel at the page ScrollViewer,
/// then samples VerticalOffset every 16 ms. A single jump means easing is not running;
/// a gradual approach means the smoothing works.
/// </summary>
public static class ScrollProbeCli
{
    public static string ReportPath => AppInfo.DataFile("scroll-probe.txt");

    public static async Task RunAsync(Window window)
    {
        var sb = new StringBuilder();

        // Squash the window so the page actually overflows (at full height most pages fit on one screen).
        window.Width = 1100;
        window.Height = 560;
        await Task.Delay(400);

        AppShell.Instance.Request(PageKey.Packs);

        // Wait for data; a short page has ScrollableHeight = 0 and the probe measures nothing.
        for (var i = 0; i < 80 && AppShell.Instance.Packs.State != Common.LoadState.Ready; i++)
            await Task.Delay(250);

        await Task.Delay(900);

        var scroller = Find<ScrollViewer>(window);
        if (scroller is null)
        {
            sb.AppendLine("ScrollViewer not found");
            await Write(sb);
            return;
        }

        sb.AppendLine($"ScrollableHeight={scroller.ScrollableHeight:F0}  Viewport={scroller.ViewportHeight:F0}");
        sb.AppendLine($"before={scroller.VerticalOffset:F1}");

        var sw = Stopwatch.StartNew();
        var samples = new StringBuilder();
        var done = new TaskCompletionSource<bool>();

        void Sample(object? s, EventArgs e)
        {
            samples.AppendLine($"  {sw.ElapsedMilliseconds,4}ms  offset={scroller.VerticalOffset,8:F1}");
            if (sw.ElapsedMilliseconds < 420) return;
            CompositionTarget.Rendering -= Sample;
            done.TrySetResult(true);
        }

        CompositionTarget.Rendering += Sample;

        // One wheel notch downwards
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
            Source = scroller,
        };
        scroller.RaiseEvent(args);

        await Task.WhenAny(done.Task, Task.Delay(1200));

        sb.AppendLine("frame samples:");
        sb.Append(samples);

        var frames = scroller.VerticalOffset;
        sb.AppendLine($"after={frames:F1}");
        sb.AppendLine(frames > 1 ? "Result: scrolled" : "Result: no scrolling (not scrollable, or the event never arrived)");

        await Write(sb);
    }

    private static async Task Write(StringBuilder sb)
    {
        try { await File.WriteAllTextAsync(ReportPath, sb.ToString()); }
        catch (IOException) { }
    }

    private static ScrollViewer? Find<T>(DependencyObject root) where T : DependencyObject
    {
        ScrollViewer? best = null;
        Walk(root);
        return best;

        void Walk(DependencyObject node)
        {
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is ScrollViewer sv && (best is null || sv.ScrollableHeight > best.ScrollableHeight))
                    best = sv;
                Walk(child);
            }
        }
    }
}
