using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LanternLauncher.Models;

namespace LanternLauncher.Services;

/// <summary>
/// Self-check used while porting: writes the results of Java discovery, version resolution and command line assembly to a report.
/// Runs only with <c>--dump</c>; normal use never touches it.
/// </summary>
public static class PortDiagnostics
{
    public static string ReportPath => AppInfo.DataFile("port-report.txt");

    public static string WriteReport()
    {
        var sb = new StringBuilder();
        var mcDir = JavaService.DefaultMinecraftDir();
        var java = new JavaService(mcDir);
        var versions = new VersionService(mcDir);

        sb.AppendLine("== Environment ==");
        sb.AppendLine($"MinecraftDir: {mcDir}  exists={Directory.Exists(mcDir)}");
        sb.AppendLine($".NET: {Environment.Version}  64bit={Environment.Is64BitOperatingSystem}");
        sb.AppendLine();

        // Java
        sb.AppendLine("== Java ==");
        var runtimes = java.Discover();
        sb.AppendLine($"Found {runtimes.Count} runtime(s)");
        foreach (var rt in runtimes)
            sb.AppendLine($"  [{rt.Major,2}] {rt.Vendor ?? "?",-18} {rt.FullVersion ?? "?",-16} {rt.Source,-14} {rt.ExecutablePath}");
        sb.AppendLine();

        // Versions
        sb.AppendLine("== Versions ==");
        var entries = versions.Scan();
        sb.AppendLine($"Found {entries.Count} version(s)");
        foreach (var v in entries.Take(12))
            sb.AppendLine($"  {v.Id,-22} type={v.Type ?? "?",-9} inherits={v.InheritsFrom ?? "-"}");
        sb.AppendLine();

        // Resolve every version
        foreach (var entry in entries.Take(6))
        {
            sb.AppendLine($"== Resolving {entry.Id} ==");
            var resolved = versions.Resolve(entry.Id);
            if (resolved is null)
            {
                sb.AppendLine("  resolve failed");
                sb.AppendLine();
                continue;
            }

            sb.AppendLine($"  BaseId={resolved.BaseId}  Type={resolved.Type}  MainClass={resolved.MainClass}");
            sb.AppendLine($"  JavaMajor={resolved.JavaMajor}  Modern={resolved.IsModern}  AssetIndex={resolved.AssetIndexId}");
            sb.AppendLine($"  Classpath: {resolved.Classpath.Count} entries, {resolved.MissingLibraries.Count} missing");
            foreach (var missing in resolved.MissingLibraries.Take(5))
                sb.AppendLine($"    MISSING {missing}");
            sb.AppendLine($"  Natives: {resolved.NativeJars.Count}  {string.Join(", ", resolved.NativeJars.Select(n => n.Classifier).Take(5))}");
            sb.AppendLine($"  ClientJar: {(resolved.ClientJar is null ? "missing" : "OK")}");
            var index = versions.AssetIndexPath(resolved.AssetIndexId);
            sb.AppendLine($"  AssetIndex: {(File.Exists(index) ? "OK" : "missing")}  {index}");

            var picked = java.Pick(runtimes, resolved.JavaMajor);
            sb.AppendLine($"  Java: {(picked is null ? "none" : $"Java {picked.Major} @ {picked.ExecutablePath}")}");

            if (picked is not null)
            {
                var ctx = new LaunchContext
                {
                    JavaExecutable = picked.ExecutablePath,
                    PlayerName = "Steve",
                    Uuid = "00000000000000000000000000000000",
                    AccessToken = "0",
                    UserType = "legacy",
                    VersionName = resolved.Id,
                    GameDir = mcDir,
                    AssetsDir = versions.AssetsDir,
                    AssetIndexName = resolved.AssetIndexId ?? "legacy",
                    NativesDir = Path.Combine(mcDir, "versions", resolved.BaseId, resolved.BaseId + "-natives"),
                    Classpath = string.Join(";", resolved.Classpath.Append(resolved.ClientJar ?? "")),
                    MemoryMb = 4096,
                };

                var argv = LaunchArgumentBuilder.Build(resolved, ctx);
                sb.AppendLine($"  Command line: {argv.Count} segments, {string.Join(" ", argv).Length} chars");
                sb.AppendLine($"  Preview: {Truncate(string.Join(" ", argv), 900)}");
                sb.AppendLine($"  JVM: {string.Join(" ", argv.Take(Math.Min(argv.Count, 8)))}");
                var mainIdx = argv.IndexOf(resolved.MainClass);
                if (mainIdx > 0 && mainIdx < argv.Count - 1)
                    sb.AppendLine($"  Game args: {Truncate(string.Join(" ", argv.Skip(mainIdx + 1)), 400)}");
            }

            sb.AppendLine();
        }

        var path = ReportPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + " …";
}
