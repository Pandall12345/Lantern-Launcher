using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace LanternLauncher.Services;

/// <summary>
/// Command line install self-check: no window, writes a report when finished.
/// <c>LanternLauncher.exe --install 1.21.1 [--assets]</c>
/// </summary>
public static class InstallCli
{
    public static string ReportPath => AppInfo.DataFile("install-report.txt");

    public static string Run(string versionId, bool includeAssets)
    {
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        var versions = new VersionService();
        var resolved = versions.Resolve(versionId);

        if (resolved is null)
        {
            sb.AppendLine($"Could not resolve {versionId} (versions/{versionId}/{versionId}.json missing or unreadable)");
            return sb.ToString();
        }

        sb.AppendLine($"Version: {resolved.Id}  Main class: {resolved.MainClass}  Java: {resolved.JavaMajor}");
        sb.AppendLine($"Target: {versions.MinecraftDir}");

        var downloads = new DownloadService(versions);
        var plan = downloads.PlanAsync(resolved, includeAssets).GetAwaiter().GetResult();
        var need = plan.Where(p => !File.Exists(p.LocalPath)).ToList();
        var bytes = need.Sum(p => p.Size);

        sb.AppendLine($"Plan: {plan.Count} files, {need.Count} missing, about {bytes / 1024.0 / 1024.0:F1} MB");

        var lastReport = 0;
        var progress = new Progress<DownloadProgress>(p =>
        {
            // One line every 20 files to keep the log readable.
            if (p.Completed - lastReport < 20 && p.Completed != p.Total) return;
            lastReport = p.Completed;
            sb.AppendLine($"  [{p.Completed,4}/{p.Total}] {p.BytesDone / 1024.0 / 1024.0,7:F1} MB  {p.CurrentLabel}");
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var summary = downloads.RunAsync(plan, progress, cts.Token).GetAwaiter().GetResult();

        sw.Stop();
        sb.AppendLine();
        sb.AppendLine($"Done: {summary.Downloaded} downloaded / {summary.Skipped} skipped / {summary.Failed} failed");
        sb.AppendLine($"Bytes: {summary.Bytes / 1024.0 / 1024.0:F1} MB in {sw.Elapsed.TotalSeconds:F1}s");
        foreach (var err in summary.Errors.Take(20)) sb.AppendLine("  FAIL " + err);

        // Resolve once more to confirm the classpath gaps are gone.
        var after = versions.Resolve(versionId);
        if (after is not null)
        {
            sb.AppendLine($"Recheck: classpath {after.Classpath.Count} entries, {after.MissingLibraries.Count} missing, client jar {(after.ClientJar is null ? "missing" : "ready")}");
            foreach (var m in after.MissingLibraries.Take(5)) sb.AppendLine("  STILL MISSING " + m);
        }

        return sb.ToString();
    }
}
