using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using LanternLauncher.Models;

namespace LanternLauncher.Services;

/// <summary>Runtime context for a launch. Everything is passed in explicitly; nothing is read from global state.</summary>
public sealed class LaunchContext
{
    public string JavaExecutable { get; init; } = "java";
    public string PlayerName { get; init; } = "Player";
    public string Uuid { get; init; } = "00000000000000000000000000000000";
    public string AccessToken { get; init; } = "0";
    public string UserType { get; init; } = "legacy";
    public string VersionName { get; init; } = "";
    public string GameDir { get; init; } = "";
    public string AssetsDir { get; init; } = "";
    public string AssetIndexName { get; init; } = "legacy";
    public string NativesDir { get; init; } = "";
    public string Classpath { get; init; } = "";
    public string LauncherName { get; init; } = "Lantern Launcher";
    public string LauncherVersion { get; init; } = "1.0";
    public int MemoryMb { get; init; } = 4096;
    public List<string> ExtraJvmArgs { get; init; } = new();
    public Dictionary<string, bool> Features { get; init; } = new();
    public int WindowWidth { get; init; }
    public int WindowHeight { get; init; }
}

/// <summary>
/// Turns a resolved version into a runnable command line.
/// Order follows PCL's ModLaunch: JVM args, classpath, main class, game args,
/// with OptiFine's tweakClass always last.
/// </summary>
public static class LaunchArgumentBuilder
{
    public static List<string> Build(ResolvedVersion version, LaunchContext ctx)
    {
        var args = new List<string> { ctx.JavaExecutable };

        // Memory and encoding first; ExtraJvmArgs may override them.
        args.Add($"-Xmx{ctx.MemoryMb}M");
        args.Add($"-Xms{Math.Min(ctx.MemoryMb, 1024)}M");
        if (version.JavaMajor >= 18)
            args.Add("-Dfile.encoding=UTF-8");

        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["auth_player_name"] = ctx.PlayerName,
            ["auth_uuid"] = ctx.Uuid,
            ["auth_access_token"] = ctx.AccessToken,
            ["auth_session"] = ctx.AccessToken,
            ["user_type"] = ctx.UserType,
            ["version_name"] = ctx.VersionName,
            ["version_type"] = version.Type ?? "release",
            ["game_directory"] = ctx.GameDir,
            ["assets_root"] = ctx.AssetsDir,
            ["assets_index_name"] = ctx.AssetIndexName,
            ["natives_directory"] = ctx.NativesDir,
            ["classpath"] = ctx.Classpath,
            ["classpath_separator"] = ";",
            ["library_directory"] = "",
            ["launcher_name"] = ctx.LauncherName,
            ["launcher_version"] = ctx.LauncherVersion,
            ["clientid"] = "",
            ["auth_xuid"] = "",
            ["resolution_width"] = ctx.WindowWidth > 0 ? ctx.WindowWidth.ToString() : "",
            ["resolution_height"] = ctx.WindowHeight > 0 ? ctx.WindowHeight.ToString() : "",
        };

        // JVM arguments
        if (version.JvmArguments.Count > 0)
            args.AddRange(Expand(version.JvmArguments, vars, ctx.Features));

        foreach (var extra in ctx.ExtraJvmArgs) args.Add(extra);

        if (!args.Any(a => a.StartsWith("-Djava.library.path=", StringComparison.Ordinal)))
            args.Add($"-Djava.library.path={ctx.NativesDir}");

        if (!args.Any(a => a == "-cp" || a == "-classpath"))
        {
            args.Add("-cp");
            args.Add(ctx.Classpath);
        }

        args.Add(version.MainClass);

        // Game arguments
        if (version.GameArguments.Count > 0)
        {
            args.AddRange(Expand(version.GameArguments, vars, ctx.Features));
        }
        else if (!string.IsNullOrWhiteSpace(version.MinecraftArguments))
        {
            foreach (var token in version.MinecraftArguments!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                args.Add(Substitute(token, vars));
        }
        else
        {
            // Legacy fallback
            args.AddRange(new[]
            {
                "--username", ctx.PlayerName,
                "--version", ctx.VersionName,
                "--gameDir", ctx.GameDir,
                "--assetsDir", ctx.AssetsDir,
                "--assetIndex", ctx.AssetIndexName,
                "--uuid", ctx.Uuid,
                "--accessToken", ctx.AccessToken,
                "--userType", ctx.UserType,
                "--versionType", version.Type ?? "release",
            });
        }

        DropEmptyValues(args);
        MoveTweakClassToEnd(args);
        return args;
    }

    /// <summary>
    /// With OptiFine and Forge both present, <c>--tweakClass optifine.*</c> crashes the game unless it is the last argument.
    /// Ported verbatim from the fix that shipped in PCL.
    /// </summary>
    private static void MoveTweakClassToEnd(List<string> args)
    {
        var idx = args.FindIndex(a => a.StartsWith("--tweakClass", StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;

        string? tweak = null;
        if (args[idx].Contains('='))
        {
            tweak = args[idx];
            args.RemoveAt(idx);
        }
        else if (idx + 1 < args.Count)
        {
            tweak = "--tweakClass " + args[idx + 1];
            args.RemoveRange(idx, 2);
        }

        if (tweak is null) return;
        if (args.Any(a => a.Contains("optifine.OptiFine", StringComparison.OrdinalIgnoreCase)))
            args.Add(tweak);
        else
            args.Add(tweak);
    }

    /// <summary>
    /// Subscription arguments (--clientId, --xuid, ...) carry no value in an offline profile;
    /// leaving them in produces dangling switches and the game rejects the command line.
    /// </summary>
    private static void DropEmptyValues(List<string> args)
    {
        for (var i = args.Count - 1; i >= 0; i--)
        {
            if (args[i].Length == 0) { args.RemoveAt(i); continue; }

            if (!args[i].StartsWith("--", StringComparison.Ordinal) || args[i].Contains('=')) continue;
            var hasValue = i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            if (!hasValue) args.RemoveAt(i);
        }
    }

    // Argument expansion

    private static IEnumerable<string> Expand(
        IEnumerable<object> entries,
        IReadOnlyDictionary<string, string> vars,
        IReadOnlyDictionary<string, bool> features)
    {
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case JsonElement el when el.ValueKind == JsonValueKind.String:
                    {
                        var s = Substitute(el.GetString() ?? "", vars);
                        if (s.Length > 0) yield return s;
                        break;
                    }

                case JsonElement el when el.ValueKind == JsonValueKind.Object:
                    {
                        if (!RuleAllowed(el, features)) break;
                        if (!el.TryGetProperty("value", out var value)) break;

                        if (value.ValueKind == JsonValueKind.String)
                        {
                            var s = Substitute(value.GetString() ?? "", vars);
                            if (s.Length > 0) yield return s;
                        }
                        else if (value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in value.EnumerateArray())
                            {
                                if (item.ValueKind != JsonValueKind.String) continue;
                                var s = Substitute(item.GetString() ?? "", vars);
                                if (s.Length > 0) yield return s;
                            }
                        }
                        break;
                    }

                case string s:
                    {
                        var sub = Substitute(s, vars);
                        if (sub.Length > 0) yield return sub;
                        break;
                    }
            }
        }
    }

    private static bool RuleAllowed(JsonElement entry, IReadOnlyDictionary<string, bool> features)
    {
        if (!entry.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            return true;

        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            var action = rule.TryGetProperty("action", out var a) ? a.GetString() : "allow";
            var matches = true;

            if (rule.TryGetProperty("os", out var os))
            {
                if (os.TryGetProperty("name", out var name) &&
                    !string.Equals(name.GetString(), "windows", StringComparison.OrdinalIgnoreCase))
                    matches = false;
            }

            if (matches && rule.TryGetProperty("features", out var feat) &&
                feat.ValueKind == JsonValueKind.Object)
            {
                foreach (var f in feat.EnumerateObject())
                {
                    var wanted = f.Value.ValueKind == JsonValueKind.True;
                    var actual = features.TryGetValue(f.Name, out var v) && v;
                    if (actual != wanted) { matches = false; break; }
                }
            }

            if (matches)
                allowed = !string.Equals(action, "disallow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    private static string Substitute(string input, IReadOnlyDictionary<string, string> vars)
    {
        if (input.IndexOf("${", StringComparison.Ordinal) < 0) return input;

        var result = input;
        foreach (var (key, value) in vars)
            result = result.Replace("${" + key + "}", value, StringComparison.OrdinalIgnoreCase);
        return result;
    }
}
