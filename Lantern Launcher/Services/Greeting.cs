using System;
using System.Globalization;

namespace LanternLauncher.Services;

/// <summary>
/// Time-of-day greeting for the Home header. Computed locally, no network call;
/// swapping in a remote lines endpoint later only touches <see cref="Line"/>.
/// </summary>
public static class Greeting
{
    public static string Line(DateTime now, string? name = null)
    {
        var hour = now.Hour;
        var part = hour switch
        {
            < 5 => "Still up",
            < 12 => "Good morning",
            < 18 => "Good afternoon",
            _ => "Good evening",
        };

        return string.IsNullOrWhiteSpace(name) ? part : $"{part}, {name.Trim()}";
    }

    /// <summary>Wednesday, 5 November</summary>
    public static string DateLine(DateTime now) =>
        now.ToString("dddd, d MMMM", CultureInfo.InvariantCulture);
}
