using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace LanternLauncher.Services;

/// <summary>
/// Launch smoke test: starts a real process, waits for a marker line in the log, kills it and writes a report.
/// <c>LanternLauncher.exe --launch &lt;id&gt; [--smoke]</c>
/// </summary>
public static class LaunchCli
{
    private static readonly string[] Markers =
    {
        "Setting user:",
        "Backend library:",
        "LWJGL Version",
        "OpenAL initialized",
        "Narrator library",
    };

    public static string ReportPath => AppInfo.DataFile("launch-report.txt");

    public static string Run(string versionId, bool smoke, int seconds = 45)
    {
        var sb = new StringBuilder();
        var launch = new LaunchService();
        var outcome = launch.Launch(versionId);

        sb.AppendLine($"Version: {versionId}");
        sb.AppendLine($"Started: {outcome.Started}  PID={outcome.Pid}  error={outcome.Error ?? "-"}");
        sb.AppendLine($"Log: {outcome.LogPath}");
        if (outcome.Command.Count > 0)
        {
            sb.AppendLine($"Command line: {outcome.Command.Count} segments, first 6:");
            foreach (var a in outcome.Command.Take(6)) sb.AppendLine("  " + a);
        }

        if (!outcome.Started || outcome.Pid is null) return sb.ToString();
        if (!smoke) return sb.ToString();

        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var hits = new System.Collections.Generic.List<string>();
        var exited = false;

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(1000);

            if (File.Exists(outcome.LogPath))
            {
                string text;
                try
                {
                    using var fs = new FileStream(outcome.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs, Encoding.UTF8);
                    text = reader.ReadToEnd();
                }
                catch (IOException) { text = ""; }

                foreach (var marker in Markers)
                {
                    if (!hits.Contains(marker) && text.Contains(marker, StringComparison.Ordinal))
                        hits.Add(marker);
                }

                if (hits.Count >= 2) break;
            }

            try
            {
                var p = Process.GetProcessById(outcome.Pid.Value);
                if (p.HasExited) { exited = true; sb.AppendLine($"Process exited early, ExitCode={p.ExitCode}"); break; }
            }
            catch (ArgumentException) { exited = true; break; }
        }

        sb.AppendLine(hits.Count > 0
            ? "Log markers hit: " + string.Join(" | ", hits)
            : "No log marker yet (see the tail of the log)");

        try
        {
            var p = Process.GetProcessById(outcome.Pid.Value);
            if (!p.HasExited) { p.Kill(entireProcessTree: true); sb.AppendLine("Process killed"); }
        }
        catch (ArgumentException) { }

        if (File.Exists(outcome.LogPath))
        {
            try
            {
                var tail = File.ReadLines(outcome.LogPath).TakeLast(12);
                sb.AppendLine("Log tail:");
                foreach (var line in tail) sb.AppendLine("  " + line);
            }
            catch (IOException) { }
        }

        if (exited && hits.Count == 0) sb.AppendLine("Result: launch failed (process exited with no marker)");
        else if (hits.Count >= 2) sb.AppendLine("Result: game reached initialization (pass)");
        else sb.AppendLine("Result: process alive but no marker yet (inspect the log)");

        return sb.ToString();
    }
}
