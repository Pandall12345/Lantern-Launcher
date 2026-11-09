using System;
using System.Windows.Media.Animation;

namespace LanternLauncher.Common;

/// <summary>
/// A real cubic-bezier easing function; WPF's CubicEase only ships fixed shapes.
/// The curve is baked into EaseInCore, so XAML must pass EasingMode="EaseIn" to mean
/// "use this curve as-is" -- any other mode flips it a second time.
/// </summary>
public sealed class CubicBezierEase : EasingFunctionBase
{
    public double X1 { get; set; } = 0.25;
    public double Y1 { get; set; }
    public double X2 { get; set; } = 0.75;
    public double Y2 { get; set; } = 1;

    protected override System.Windows.Freezable CreateInstanceCore() => new CubicBezierEase();

    protected override double EaseInCore(double normalizedTime)
    {
        if (normalizedTime <= 0) return 0;
        if (normalizedTime >= 1) return 1;

        // Solve for t where the curve's x component equals the input time. 24 bisection steps land below 1e-6.
        double lo = 0, hi = 1, mid = normalizedTime;
        for (var i = 0; i < 24; i++)
        {
            mid = (lo + hi) * 0.5;
            var x = Component(mid, X1, X2);
            if (Math.Abs(x - normalizedTime) < 1e-6) break;
            if (x < normalizedTime) lo = mid; else hi = mid;
        }
        return Component(mid, Y1, Y2);
    }

    private static double Component(double t, double p1, double p2)
    {
        var u = 1 - t;
        return 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t;
    }
}
