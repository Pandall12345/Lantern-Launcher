using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace LanternLauncher;

public partial class App : Application
{
    private static readonly string CrashLog = Services.AppInfo.DataFile("crash.log");

    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            Log("DispatcherUnhandled", e.Exception);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("DomainUnhandled", e.ExceptionObject as Exception);

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("UnobservedTask", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Move data from the older locations (Launcher, LanternMC) to LanternLauncher. Runs once.
        Services.AppInfo.MigrateLegacy();

        // Self-check entry point: LanternLauncher.exe --dump writes a report and never opens a window.
        // Smooth-scroll self-check: LanternLauncher.exe --scroll-probe
        if (e.Args.Any(a => string.Equals(a, "--scroll-probe", StringComparison.OrdinalIgnoreCase)))
        {
            var win = new MainWindow();
            win.Show();

            async void Probe()
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(900);
                    await Services.ScrollProbeCli.RunAsync(win);
                }
                catch (Exception ex) { Log("ScrollProbe", ex); }
                finally { Shutdown(0); }
            }

            Probe();
            return;
        }

        // Screenshot self-check: LanternLauncher.exe --shots <dir> (RenderTargetBitmap, needs no foreground window).
        var shotsIdx = Array.FindIndex(e.Args, a => string.Equals(a, "--shots", StringComparison.OrdinalIgnoreCase));
        if (shotsIdx >= 0 && shotsIdx + 1 < e.Args.Length)
        {
            var dir = e.Args[shotsIdx + 1];
            var win = new MainWindow();
            win.Show();

            async void Shoot()
            {
                try
                {
                    var frame = win.Content as FrameworkElement;
                    win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                    await System.Threading.Tasks.Task.Delay(1200);
                    await Services.ShotCli.RunAsync(win, dir);
                    File.WriteAllText(Path.Combine(dir, "shots.txt"), "ok " + DateTime.Now.ToString("s"));
                }
                catch (Exception ex) { Log("Shots", ex); }
                finally { Shutdown(0); }
            }

            Shoot();
            return;
        }

        // Launch smoke test: LanternLauncher.exe --launch <versionId> [--smoke]
        var launchIdx = Array.FindIndex(e.Args, a => string.Equals(a, "--launch", StringComparison.OrdinalIgnoreCase));
        if (launchIdx >= 0 && launchIdx + 1 < e.Args.Length)
        {
            var id = e.Args[launchIdx + 1];
            var smoke = e.Args.Any(a => string.Equals(a, "--smoke", StringComparison.OrdinalIgnoreCase));
            try
            {
                var text = Services.LaunchCli.Run(id, smoke);
                File.WriteAllText(Services.LaunchCli.ReportPath, text);
            }
            catch (Exception ex) { Log("Launch", ex); }
            Shutdown(0);
            return;
        }

        // Install self-check: LanternLauncher.exe --install <versionId> [--assets]
        var installIdx = Array.FindIndex(e.Args, a => string.Equals(a, "--install", StringComparison.OrdinalIgnoreCase));
        if (installIdx >= 0 && installIdx + 1 < e.Args.Length)
        {
            var id = e.Args[installIdx + 1];
            var withAssets = e.Args.Any(a => string.Equals(a, "--assets", StringComparison.OrdinalIgnoreCase));
            try
            {
                var text = Services.InstallCli.Run(id, withAssets);
                Console.WriteLine(text);
                File.WriteAllText(Services.InstallCli.ReportPath, text);
            }
            catch (Exception ex) { Log("Install", ex); }
            Shutdown(0);
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--dump", StringComparison.OrdinalIgnoreCase)))
        {
            try { Services.PortDiagnostics.WriteReport(); }
            catch (Exception ex) { Log("Dump", ex); }
            Shutdown(0);
            return;
        }

        base.OnStartup(e);

        Common.DropdownMotion.Initialize();

        // StartupUri was removed from App.xaml and the window is created here instead,
        // otherwise the --shots path races StartupUri and opens a second window.
        var main = new MainWindow();
        main.Show();
    }

    private static void Log(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLog)!);
            File.AppendAllText(CrashLog,
                $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}\n{ex}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Debug.WriteLine($"[{source}] {ex}");
    }
}
