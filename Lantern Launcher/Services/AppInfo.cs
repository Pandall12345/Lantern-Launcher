using System;
using System.IO;

namespace LanternLauncher.Services;

/// <summary>
/// Single source of truth for the app identity and data folders. Renaming means editing this file only.
/// The product is called <c>Lantern Launcher</c>; file and folder names drop the space
/// and use <c>LanternLauncher</c>.
/// </summary>
public static class AppInfo
{
    /// <summary>Display name shown in the UI.</summary>
    public const string DisplayName = "Lantern Launcher";

    /// <summary>Name used for file paths and HTTP identifiers (no spaces).</summary>
    public const string Slug = "LanternLauncher";

    public const string Version = "0.1.0";

    /// <summary>Brand string passed as -Dminecraft.launcher.brand.</summary>
    public static string Brand => DisplayName;

    /// <summary>HTTP User-Agent.</summary>
    public static string UserAgent => $"{Slug}/{Version}";

    /// <summary>Where logs, reports and crash records land.</summary>
    public static string DataDir => EnsureDir(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Slug));

    /// <summary>Where settings live (roaming, follows the user profile).</summary>
    public static string SettingsDir => EnsureDir(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Slug));

    public static string DataFile(string fileName) => Path.Combine(DataDir, fileName);

    public static string SettingsFile(string fileName) => Path.Combine(SettingsDir, fileName);

    private static string EnsureDir(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Moves folders left behind by earlier names into the current location. Runs once; failure is not fatal.
    /// Previous names: Launcher (before the project had one) and LanternMC (briefly).
    /// </summary>
    public static void MigrateLegacy()
    {
        foreach (var legacy in new[] { "Launcher", "LanternMC" })
        {
            TryMove(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), legacy),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Slug));

            TryMove(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), legacy),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Slug));
        }
    }

    private static void TryMove(string from, string to)
    {
        try
        {
            if (!Directory.Exists(from) || string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;

            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from))
            {
                var target = Path.Combine(to, Path.GetFileName(file));
                if (File.Exists(target))
                {
                    File.Delete(file);
                    continue;
                }
                File.Move(file, target);
            }

            // Subfolders such as logs move as a whole.
            foreach (var dir in Directory.GetDirectories(from))
            {
                var target = Path.Combine(to, Path.GetFileName(dir));
                if (Directory.Exists(target)) continue;
                Directory.Move(dir, target);
            }

            if (Directory.GetFileSystemEntries(from).Length == 0) Directory.Delete(from);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
