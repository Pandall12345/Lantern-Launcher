using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LanternLauncher.Shell;

namespace LanternLauncher.Services;

/// <summary>
/// Screenshot self-check: renders every page to PNG with RenderTargetBitmap.
/// Independent of window focus and free of the DPI errors that screen capture brings.
/// <c>LanternLauncher.exe --shots &lt;dir&gt;</c>
/// </summary>
public static class ShotCli
{
    private static readonly (PageKey Key, string Name)[] Pages =
    {
        (PageKey.Home, "home"),
        (PageKey.Library, "library"),
        (PageKey.Browse, "browse"),
        (PageKey.Packs, "packs"),
        (PageKey.Downloads, "downloads"),
        (PageKey.Settings, "settings"),
    };

    public static async Task RunAsync(Window window, string dir)
    {
        Directory.CreateDirectory(dir);

        foreach (var (key, name) in Pages)
        {
            AppShell.Instance.Request(key);
            // Delay is overridable via LANTERN_SHOT_DELAY_MS; lower it to catch loading states.
            var delayMs = 4200;
            if (int.TryParse(Environment.GetEnvironmentVariable("LANTERN_SHOT_DELAY_MS"), out var custom) && custom > 0)
                delayMs = custom;

            await Task.Delay(delayMs);
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

            var path = Path.Combine(dir, "p-" + name + ".png");
            Save(window, path);
        }

        AppShell.Instance.Request(PageKey.Home);
        await Task.Delay(400);
    }

    private static void Save(Window window, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var w = Math.Max(1, (int)Math.Round(window.ActualWidth * dpi.DpiScaleX));
        var h = Math.Max(1, (int)Math.Round(window.ActualHeight * dpi.DpiScaleY));

        var rtb = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        rtb.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    public static Dictionary<string, string> Describe() => new()
    {
        ["mode"] = "RenderTargetBitmap",
        ["pages"] = string.Join(",", Array.ConvertAll(Pages, p => p.Name)),
    };
}
