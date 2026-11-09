using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LanternLauncher.Models;

namespace LanternLauncher.Services;

public sealed record LaunchProfile(
    string PlayerName = "Steve",
    string Uuid = "00000000000000000000000000000000",
    string AccessToken = "0",
    int MemoryMb = 4096,
    string? GameDir = null,
    int WindowWidth = 0,
    int WindowHeight = 0)
{
    public static LaunchProfile Default { get; } = new();
}

public sealed record LaunchOutcome(
    bool Started,
    int? ExitCode,
    string? Error,
    string LogPath,
    IReadOnlyList<string> Command,
    int? Pid = null);

/// <summary>
/// Turns a resolved version into a running process: pick Java, extract natives, build the command line, pump stdout and stderr.
/// </summary>
public sealed class LaunchService
{
    private readonly VersionService _versions;
    private readonly JavaService _java;

    public LaunchService(VersionService? versions = null, JavaService? java = null)
    {
        _versions = versions ?? new VersionService();
        _java = java ?? new JavaService(_versions.MinecraftDir);
    }

    public string LogDirectory => Path.Combine(AppInfo.DataDir, "logs");

    public LaunchOutcome Launch(string versionId, LaunchProfile? profile = null, bool waitForExit = false)
    {
        profile ??= LaunchProfile.Default;

        var resolved = _versions.Resolve(versionId);
        if (resolved is null)
            return new LaunchOutcome(false, null, $"Could not resolve version {versionId}", "", Array.Empty<string>());

        if (resolved.Classpath.Count == 0)
            return new LaunchOutcome(false, null, "Empty classpath", "", Array.Empty<string>());

        var runtimes = _java.Discover();
        var runtime = _java.Pick(runtimes, resolved.JavaMajor);
        if (runtime is null)
            return new LaunchOutcome(false, null, $"No Java runtime satisfies Java {resolved.JavaMajor}+", "", Array.Empty<string>());

        var gameDir = profile.GameDir ?? _versions.MinecraftDir;
        var nativesDir = EnsureNatives(resolved);

        // The client jar must be on the classpath, otherwise the game dies with ClassNotFoundException: net.minecraft.client.main.Main
        var cpEntries = new List<string>(resolved.Classpath);
        if (resolved.ClientJar is not null) cpEntries.Add(resolved.ClientJar);

        var ctx = new LaunchContext
        {
            JavaExecutable = runtime.ExecutablePath,
            PlayerName = profile.PlayerName,
            Uuid = profile.Uuid,
            AccessToken = profile.AccessToken,
            VersionName = resolved.Id,
            GameDir = gameDir,
            AssetsDir = _versions.AssetsDir,
            AssetIndexName = resolved.AssetIndexId ?? "legacy",
            NativesDir = nativesDir,
            Classpath = string.Join(Path.PathSeparator, cpEntries),
            MemoryMb = profile.MemoryMb,
            WindowWidth = profile.WindowWidth,
            WindowHeight = profile.WindowHeight,
        };

        var argv = LaunchArgumentBuilder.Build(resolved, ctx);
        Directory.CreateDirectory(LogDirectory);

        var logPath = Path.Combine(LogDirectory, $"{versionId}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var psi = new ProcessStartInfo
        {
            FileName = argv[0],
            WorkingDirectory = gameDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var a in argv.Skip(1)) psi.ArgumentList.Add(a);

        try
        {
            var proc = Process.Start(psi);
            if (proc is null)
            return new LaunchOutcome(false, null, "Process.Start returned null", logPath, argv);

            var log = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (log) log.WriteLine(e.Data); } };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (log) log.WriteLine(e.Data); } };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            if (waitForExit)
            {
                if (!proc.WaitForExit(120_000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    log.Dispose();
            return new LaunchOutcome(true, null, "Timed out and was killed", logPath, argv, proc.Id);
                }

                log.Dispose();
                return new LaunchOutcome(true, proc.ExitCode, null, logPath, argv, proc.Id);
            }

            return new LaunchOutcome(true, null, null, logPath, argv, proc.Id);
        }
        catch (Exception ex)
        {
            return new LaunchOutcome(false, null, ex.Message, logPath, argv);
        }
    }

    /// <summary>Older versions need their natives jars extracted; 1.19+ ships none, so an empty folder is fine there.</summary>
    private string EnsureNatives(ResolvedVersion resolved)
    {
        var dir = Path.Combine(_versions.VersionsDir, resolved.Id, resolved.Id + "-natives");
        Directory.CreateDirectory(dir);
        if (resolved.NativeJars.Count == 0) return dir;

        foreach (var (_, jar) in resolved.NativeJars)
        {
            if (!File.Exists(jar)) continue;
            try
            {
                using var zip = ZipFile.OpenRead(jar);
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.EndsWith('/')) continue;
                    var target = Path.Combine(dir, Path.GetFileName(entry.FullName));
                    entry.ExtractToFile(target, overwrite: true);
                }
            }
            catch (IOException) { }
            catch (InvalidDataException) { }
        }

        return dir;
    }
}
