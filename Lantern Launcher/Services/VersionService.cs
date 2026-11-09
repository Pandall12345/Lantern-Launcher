using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LanternLauncher.Models;

namespace LanternLauncher.Services;

/// <summary>
/// Reads <c>.minecraft/versions</c>, parses version JSON, merges the inheritance chain and filters libraries per platform.
/// Same model and filtering rules as PCL, rewritten from scratch with no PCL runtime dependency.
/// </summary>
public sealed class VersionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _minecraftDir;

    public VersionService(string? minecraftDir = null)
        => _minecraftDir = minecraftDir ?? JavaService.DefaultMinecraftDir();

    public string MinecraftDir => _minecraftDir;
    public string VersionsDir => Path.Combine(_minecraftDir, "versions");
    public string LibrariesDir => Path.Combine(_minecraftDir, "libraries");
    public string AssetsDir => Path.Combine(_minecraftDir, "assets");

    // Scan

    public List<VersionEntry> Scan()
    {
        var list = new List<VersionEntry>();
        if (!Directory.Exists(VersionsDir)) return list;

        foreach (var dir in Directory.GetDirectories(VersionsDir))
        {
            var id = Path.GetFileName(dir);
            var jsonPath = Path.Combine(dir, id + ".json");
            if (!File.Exists(jsonPath)) continue;

            string? type = null, inherits = null, release = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var t)) type = t.GetString();
                if (root.TryGetProperty("inheritsFrom", out var i)) inherits = i.GetString();
                if (root.TryGetProperty("releaseTime", out var r)) release = r.GetString();
            }
            catch (JsonException) { continue; }
            catch (IOException) { continue; }

            list.Add(new VersionEntry
            {
                Id = id,
                Type = type,
                InheritsFrom = inherits,
                JsonPath = jsonPath,
                ReleaseTime = release,
            });
        }

        return list
            .OrderByDescending(v => v.ReleaseTime ?? "", StringComparer.Ordinal)
            .ThenBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Parse

    /// <summary>
    /// Walks the inheritance chain and returns the raw JSON list oldest-first (parent before child), for download plans.
    /// </summary>
    public List<VersionJson> ResolveChain(string id)
    {
        var chain = new List<VersionJson>();
        var cursor = id;

        for (var guard = 0; guard < 8 && !string.IsNullOrEmpty(cursor); guard++)
        {
            var path = Path.Combine(VersionsDir, cursor, cursor + ".json");
            if (!File.Exists(path)) break;

            VersionJson? json;
            try
            {
                json = JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(path), JsonOptions);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                break;
            }

            if (json is null || string.IsNullOrEmpty(json.Id)) break;
            chain.Add(json);
            cursor = json.InheritsFrom;
        }

        return Enumerable.Reverse(chain).ToList();
    }

    public ResolvedVersion? Resolve(string id)
    {
        // Inheritance chain: parent first, each level overriding the one before it.
        var ordered = ResolveChain(id);
        if (ordered.Count == 0) return null;
        var baseVersion = ordered[0];
        var leaf = ordered[^1];

        var libs = new List<LibraryJson>();
        var jvm = new List<object>();
        var game = new List<object>();
        var mainClass = "";
        var assetIndexId = (string?)null;
        var javaMajor = 0;
        var minecraftArgs = (string?)null;
        var modern = false;

        foreach (var v in ordered)
        {
            if (v.Libraries is { Count: > 0 }) libs.AddRange(v.Libraries);
            if (v.Arguments is not null)
            {
                modern = true;
                if (v.Arguments.Jvm is { Count: > 0 }) jvm.AddRange(v.Arguments.Jvm);
                if (v.Arguments.Game is { Count: > 0 }) game.AddRange(v.Arguments.Game);
            }
            if (!string.IsNullOrWhiteSpace(v.MainClass)) mainClass = v.MainClass!;
            if (v.AssetIndex is not null && !string.IsNullOrWhiteSpace(v.AssetIndex.Id))
                assetIndexId = v.AssetIndex.Id;
            else if (!string.IsNullOrWhiteSpace(v.Assets) && v.AssetIndex is null)
                assetIndexId ??= v.Assets;
            if (v.JavaVersion is { MajorVersion: > 0 }) javaMajor = v.JavaVersion.MajorVersion;
            if (!string.IsNullOrWhiteSpace(v.MinecraftArguments)) minecraftArgs = v.MinecraftArguments;
        }

        var classpath = new List<string>();
        var natives = new List<(string, string)>();
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var lib in libs)
        {
            if (!IsAllowed(lib.Rules)) continue;

            var artifactPath = lib.Downloads?.Artifact?.Path;
            if (!string.IsNullOrWhiteSpace(artifactPath) && seen.Add(artifactPath!))
            {
                var abs = Path.Combine(LibrariesDir, artifactPath!.Replace('/', Path.DirectorySeparatorChar));
                classpath.Add(abs);
                if (!File.Exists(abs)) missing.Add(lib.Name);
            }

            if (lib.Natives is not null &&
                lib.Natives.TryGetValue("windows", out var classifierKey) &&
                lib.Downloads?.Classifiers is not null &&
                lib.Downloads.Classifiers.TryGetValue(classifierKey, out var nativeArt) &&
                !string.IsNullOrWhiteSpace(nativeArt.Path))
            {
                var abs = Path.Combine(LibrariesDir, nativeArt.Path!.Replace('/', Path.DirectorySeparatorChar));
                natives.Add((classifierKey, abs));
                if (!File.Exists(abs)) missing.Add(lib.Name + " (natives)");
            }
        }

        var clientJar = Path.Combine(VersionsDir, baseVersion.Id, baseVersion.Id + ".jar");

        return new ResolvedVersion
        {
            Id = leaf.Id,
            BaseId = baseVersion.Id,
            Type = leaf.Type ?? baseVersion.Type,
            MainClass = mainClass,
            AssetIndexId = assetIndexId ?? baseVersion.Assets,
            JavaMajor = javaMajor,
            IsModern = modern,
            MinecraftArguments = minecraftArgs,
            JvmArguments = jvm,
            GameArguments = game,
            Classpath = classpath,
            NativeJars = natives,
            ClientJar = File.Exists(clientJar) ? clientJar : null,
            MissingLibraries = missing,
        };
    }

    // Rules

    /// <summary>No rules means allow. With rules, the last matching entry decides.</summary>
    public static bool IsAllowed(List<RuleJson>? rules, IReadOnlyDictionary<string, bool>? features = null)
    {
        if (rules is null || rules.Count == 0) return true;

        var allowed = false;
        foreach (var rule in rules)
        {
            if (!Matches(rule, features)) continue;
            allowed = !string.Equals(rule.Action, "disallow", StringComparison.OrdinalIgnoreCase);
        }
        return allowed;
    }

    private static bool Matches(RuleJson rule, IReadOnlyDictionary<string, bool>? features)
    {
        if (rule.Os is not null)
        {
            var os = rule.Os;
            if (!string.IsNullOrWhiteSpace(os.Name) &&
                !string.Equals(os.Name, "windows", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrWhiteSpace(os.Arch))
            {
                var arch = Environment.Is64BitOperatingSystem ? "x86_64" : "x86";
                var archAlt = Environment.Is64BitOperatingSystem ? "x64" : "x86";
                if (!string.Equals(os.Arch, arch, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(os.Arch, archAlt, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }

        if (rule.Features is { Count: > 0 })
        {
            foreach (var (key, wanted) in rule.Features)
            {
                var actual = features is not null && features.TryGetValue(key, out var v) && v;
                if (actual != wanted) return false;
            }
        }

        return true;
    }

    public string AssetIndexPath(string? indexId)
        => Path.Combine(AssetsDir, "indexes", (indexId ?? "legacy") + ".json");
}
