using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LanternLauncher.Models;

namespace LanternLauncher.Services;

/// <summary>
/// Scans .minecraft/versions and turns the folders on disk into instances the UI can use.
/// Metadata only: no downloads, no network.
/// </summary>
public sealed class InstanceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly VersionService _versions;
    private readonly LibraryKind _kind;

    public InstanceService(VersionService? versions = null, LibraryKind kind = LibraryKind.Lantern)
    {
        _versions = versions ?? new VersionService();
        _kind = kind;
    }

    public string MinecraftDir => _versions.MinecraftDir;

    public List<GameInstance> Scan()
    {
        var result = new List<GameInstance>();
        if (!Directory.Exists(_versions.VersionsDir)) return result;

        foreach (var dir in Directory.EnumerateDirectories(_versions.VersionsDir))
        {
            var id = Path.GetFileName(dir);
            var jsonPath = Path.Combine(dir, id + ".json");
            if (!File.Exists(jsonPath)) continue;

            var instance = Read(dir, id, jsonPath);
            if (instance is not null) result.Add(instance);
        }

        return result
            .OrderByDescending(i => i.LastPlayedUtc)
            .ToList();
    }

    private GameInstance? Read(string dir, string id, string jsonPath)
    {
        VersionJson? json;
        try
        {
            json = JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(jsonPath), JsonOptions);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }

        if (json is null) return null;

        // Find the root version: a child inherits its library list from the parent.
        var chain = _versions.ResolveChain(id);
        var allLibraries = chain.SelectMany(c => c.Libraries).ToList();
        var loader = DetectLoader(allLibraries) ?? "Vanilla";

        var jar = Path.Combine(dir, id + ".jar");
        var jarMissing = !File.Exists(jar);

        var modsDir = Path.Combine(dir, "mods");
        var modCount = Directory.Exists(modsDir)
            ? Directory.GetFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly).Length
            : 0;

        var lastWrite = File.GetLastWriteTimeUtc(jsonPath);
        var instance = new GameInstance
        {
            Name = id,
            Version = id,
            VersionId = id,
            ModLoader = loader,
            LastPlayedUtc = lastWrite,
            LastPlayed = Relative(lastWrite),
            CoverUrl = string.Empty,
            PlayTime = modCount > 0 ? $"{modCount} mods" : "No mods",
            Description = BuildDescription(json, allLibraries.Count, jarMissing),
            Status = jarMissing ? "Incomplete" : "Ready",
            SizeBytes = SafeSize(jar),
            FileSize = FormatSize(SafeSize(jar)),
            LibraryKind = _kind,
            LibraryRoot = _versions.MinecraftDir,
            InstanceDir = dir,
            ModsCount = modCount,
            PackIconPath = FindPackIcon(dir),
        };

        return instance;
    }

    /// <summary>Pack icon: prefer the outer folder, then the one inside versions.</summary>
    private static string? FindPackIcon(string dir)
    {
        string[] names = { "icon.png", "pack.png", "cover.png", "logo.png" };
        foreach (var name in names)
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB",
    };

    private static string BuildDescription(VersionJson json, int libraryCount, bool jarMissing)
    {
        var parts = new List<string>();
        if (json.JavaVersion is { MajorVersion: > 0 } jv) parts.Add("Java " + jv.MajorVersion);
        parts.Add(libraryCount + " libraries");
        if (jarMissing) parts.Add("client jar missing");
        return string.Join(" · ", parts);
    }

    private static string? DetectLoader(List<LibraryJson> libraries)
    {
        foreach (var lib in libraries)
        {
            var name = lib.Name;
            if (name.Contains("fabric-loader", StringComparison.OrdinalIgnoreCase)) return "Fabric";
            if (name.Contains("quilt-loader", StringComparison.OrdinalIgnoreCase)) return "Quilt";
            if (name.Contains("neoforge", StringComparison.OrdinalIgnoreCase)) return "NeoForge";
            if (name.Contains("forge", StringComparison.OrdinalIgnoreCase)) return "Forge";
        }
        return null;
    }

    private static long SafeSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
    }

    public static string Relative(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalMinutes < 2) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} minutes ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} hours ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} days ago";
        return utc.ToLocalTime().ToString("yyyy-MM-dd");
    }
}
