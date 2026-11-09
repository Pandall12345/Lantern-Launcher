using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LanternLauncher.Models;

namespace LanternLauncher.Services;

/// <summary>
/// Java runtime discovery. Scan locations follow PCL.Core's java scanners,
/// but only sources that state a fact are kept: JAVA_VERSION inside the <c>release</c> file.
/// Output of <c>java -version</c> is not parsed -- it is localized and written to stderr.
/// </summary>
public sealed class JavaService
{
    private static readonly string[] RootCandidates =
    {
        @"C:\Program Files\Java",
        @"C:\Program Files\Eclipse Adoptium",
        @"C:\Program Files\Eclipse Foundation",
        @"C:\Program Files\Microsoft",
        @"C:\Program Files\Zulu",
        @"C:\Program Files\BellSoft",
        @"C:\Program Files\Amazon Corretto",
        @"C:\Program Files\Semeru",
        @"C:\Program Files (x86)\Java",
    };

    private readonly string _minecraftDir;

    public JavaService(string? minecraftDir = null)
        => _minecraftDir = minecraftDir ?? DefaultMinecraftDir();

    public static string DefaultMinecraftDir()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".minecraft");

    /// <summary>Scans every source, newest major version first.</summary>
    public List<JavaRuntimeInfo> Discover()
    {
        var found = new List<JavaRuntimeInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(string javaExe, string source)
        {
            if (string.IsNullOrWhiteSpace(javaExe) || !File.Exists(javaExe)) return;
            var home = Path.GetDirectoryName(Path.GetDirectoryName(javaExe));
            if (home is null) return;
            var real = javaExe;
            try { real = new FileInfo(javaExe).FullName; } catch { }
            if (!seen.Add(real)) return;
            found.Add(Read(javaExe, home, source));
        }

        // 1) JAVA_HOME
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
            Consider(Path.Combine(javaHome, "bin", "java.exe"), "JAVA_HOME");

        // 2) Common install roots, one level deep.
        foreach (var root in RootCandidates)
        {
            if (!Directory.Exists(root)) continue;
            if (File.Exists(Path.Combine(root, "bin", "java.exe")))
                Consider(Path.Combine(root, "bin", "java.exe"), "install root");
            foreach (var sub in SafeDirs(root))
                Consider(Path.Combine(sub, "bin", "java.exe"), "install root");
        }

        // 3) Per-user installs.
        var localApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        foreach (var sub in SafeDirs(localApps))
            Consider(Path.Combine(sub, "bin", "java.exe"), "user install");

        // 4) Mojang runtime shipped with the game (.minecraft/runtime/<component>/<platform>/<component>/bin/java.exe)
        var runtimeRoot = Path.Combine(_minecraftDir, "runtime");
        if (Directory.Exists(runtimeRoot))
            foreach (var exe in SafeJavaExes(runtimeRoot, depth: 5))
                Consider(exe, "mojang runtime");

        return found
            .OrderByDescending(x => x.Major)
            .ThenBy(x => x.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Picks the best Java for a Minecraft version: meet the minimum, then take the closest match.</summary>
    public JavaRuntimeInfo? Pick(IReadOnlyList<JavaRuntimeInfo> all, int requiredMajor)
    {
        if (all.Count == 0) return null;
        if (requiredMajor <= 0) return all[0];
        var ok = all.Where(x => x.Major >= requiredMajor).OrderBy(x => x.Major).FirstOrDefault();
        return ok ?? all.OrderByDescending(x => x.Major).First();
    }

    private static JavaRuntimeInfo Read(string javaExe, string home, string source)
    {
        var major = 0;
        string? full = null;
        string? vendor = null;
        var is64 = Environment.Is64BitOperatingSystem;

        var release = Path.Combine(home, "release");
        if (File.Exists(release))
        {
            foreach (var line in SafeLines(release))
            {
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                var val = line[(eq + 1)..].Trim().Trim('"');
                switch (key)
                {
                    case "JAVA_VERSION":
                        full = val;
                        major = ParseMajor(val);
                        break;
                    case "IMPLEMENTOR":
                        vendor = val;
                        break;
                    case "OS_ARCH":
                        is64 = val.Contains("64", StringComparison.OrdinalIgnoreCase);
                        break;
                }
            }
        }

        // No release file (rare): fall back to the version number in the path.
        if (major == 0)
        {
            var m = Regex.Match(home, @"(?:jdk|jre)[- _]?(\d+)(?:[._](\d+))?", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                major = int.Parse(m.Groups[1].Value);
                if (major == 1 && m.Groups[2].Success) major = int.Parse(m.Groups[2].Value);
            }
        }

        return new JavaRuntimeInfo
        {
            ExecutablePath = javaExe,
            Major = major,
            FullVersion = full,
            Vendor = vendor,
            Is64Bit = is64,
            Source = source,
        };
    }

    /// <summary>"1.8.0_503" to 8, "25.0.4" to 25.</summary>
    public static int ParseMajor(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;
        var m = Regex.Match(version, @"^(\d+)(?:\.(\d+))?");
        if (!m.Success) return 0;
        var first = int.Parse(m.Groups[1].Value);
        if (first != 1) return first;
        return m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1;
    }

    private static IEnumerable<string> SafeDirs(string path)
    {
        if (!Directory.Exists(path)) yield break;
        string[] dirs;
        try { dirs = Directory.GetDirectories(path); }
        catch { yield break; }
        foreach (var d in dirs) yield return d;
    }

    private static IEnumerable<string> SafeJavaExes(string root, int depth)
    {
        if (depth < 0) yield break;
        string[] files, dirs;
        try
        {
            files = Directory.GetFiles(root, "java.exe", SearchOption.TopDirectoryOnly);
            dirs = Directory.GetDirectories(root);
        }
        catch { yield break; }
        foreach (var f in files) yield return f;
        foreach (var d in dirs)
            foreach (var f in SafeJavaExes(d, depth - 1))
                yield return f;
    }

    private static IEnumerable<string> SafeLines(string file)
    {
        string[] lines;
        try { lines = File.ReadAllLines(file); }
        catch { yield break; }
        foreach (var l in lines) yield return l;
    }
}
