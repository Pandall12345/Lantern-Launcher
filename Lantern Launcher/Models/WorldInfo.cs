using System;
using System.IO;
using System.Linq;

namespace LanternLauncher.Models;

/// <summary>A world folder. Name, icon, size and last-entry time all come from disk.</summary>
public sealed class WorldInfo
{
    public string Name { get; init; } = "";
    public string Folder { get; init; } = "";
    public string IconPath { get; init; } = "";
    public bool HasIcon { get; init; }
    public long SizeBytes { get; init; }
    public DateTime LastWriteUtc { get; init; }

    /// <summary>Label shown in the UI. Non-Latin folder names fall back to Play in the English interface.</summary>
    public string DisplayName => Name.All(c => c < 128) ? Name : "Play";

    public string SizeText => SizeBytes switch
    {
        >= 1024L * 1024 * 1024 => $"{SizeBytes / 1024.0 / 1024 / 1024:F1} GB",
        >= 1024L * 1024 => $"{SizeBytes / 1024.0 / 1024:F0} MB",
        >= 1024 => $"{SizeBytes / 1024} KB",
        _ => $"{SizeBytes} B",
    };

    public string LastText
    {
        get
        {
            var span = DateTime.UtcNow - LastWriteUtc;
            if (span.TotalMinutes < 2) return "just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours} h ago";
            if (span.TotalDays < 30) return $"{(int)span.TotalDays} days ago";
            return LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd");
        }
    }
}

/// <summary>Scans .minecraft/saves. Filesystem only, no network.</summary>
public sealed class WorldService
{
    private readonly string _savesDir;

    public WorldService(string? minecraftDir = null)
    {
        var root = minecraftDir ?? Services.JavaService.DefaultMinecraftDir();
        _savesDir = Path.Combine(root, "saves");
    }

    public string SavesDir => _savesDir;

    public System.Collections.Generic.List<WorldInfo> Scan()
    {
        var result = new System.Collections.Generic.List<WorldInfo>();
        if (!Directory.Exists(_savesDir)) return result;

        foreach (var dir in Directory.EnumerateDirectories(_savesDir))
        {
            var folder = Path.GetFileName(dir);
            if (folder.StartsWith(".", StringComparison.Ordinal)) continue;

            var level = Path.Combine(dir, "level.dat");
            if (!File.Exists(level)) continue;

            var icon = Path.Combine(dir, "icon.png");
            result.Add(new WorldInfo
            {
                Name = folder,
                Folder = dir,
                IconPath = File.Exists(icon) ? icon : "",
                HasIcon = File.Exists(icon),
                SizeBytes = DirectorySize(dir),
                LastWriteUtc = File.GetLastWriteTimeUtc(level),
            });
        }

        return result.OrderByDescending(w => w.LastWriteUtc).ToList();
    }

    private static long DirectorySize(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(f =>
                {
                    try { return new FileInfo(f).Length; }
                    catch (IOException) { return 0L; }
                });
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }
}
